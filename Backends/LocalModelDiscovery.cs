using System.IO;
using System.Linq;
using System.Text.Json;
using HartsyInference.LLM.Models.MossVl;
using HartsyInference.ModelAssets.Checkpoints;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Backends;

/// <summary>Finds the local models the engine can run besides single GGUF files: Hugging Face checkpoint <em>folders</em> of a
/// model the engine loads from a folder (today MOSS-VL-Realtime in BF16, NF4 or FP8). Pure file logic over the folders it is given,
/// so it is unit-testable without a SwarmUI host.</summary>
internal static class LocalModelDiscovery
{
    /// <summary>The deepest folder level searched below a model folder, so a stray huge tree is not walked end to end.</summary>
    private const int MaxDepth = 5;

    /// <summary>A checkpoint folder listed as a chat model.</summary>
    /// <param name="Id">The model id: the folder's path relative to its model folder, with <c>/</c> separators.</param>
    /// <param name="Name">The folder's name.</param>
    /// <param name="FullPath">The folder's full path, without a trailing separator. The engine compares loaded paths as strings, so the
    /// same spelling must be passed on every request or the model reloads.</param>
    /// <param name="SizeBytes">The total size of its safetensors files.</param>
    /// <param name="Quantization">A label for the weights: BF16, NF4, FP8 or the config's own quantization method.</param>
    /// <param name="ContextLength">The text model's maximum positions, or -1 when the config does not say.</param>
    internal sealed record CheckpointModel(string Id, string Name, string FullPath, long SizeBytes, string Quantization, int ContextLength);

    /// <summary>Every MOSS-VL checkpoint folder under <paramref name="folders"/>. A folder that is a checkpoint is not searched further
    /// (its shards are not models). A folder that holds both a GGUF and the checkpoint is ambiguous to the engine, so it is skipped and logged.</summary>
    internal static IEnumerable<CheckpointModel> EnumerateCheckpointModels(IEnumerable<string> folders)
    {
        foreach (string root in folders)
        {
            foreach (CheckpointModel model in Walk(root, root, 0))
            {
                yield return model;
            }
        }
    }

    private static IEnumerable<CheckpointModel> Walk(string root, string directory, int depth)
    {
        HfCheckpointInfo info = HfCheckpointDirectory.TryProbe(directory);
        if (info is not null)
        {
            if (!string.Equals(info.ModelType, MossVlConfig.ModelType, StringComparison.Ordinal))
            {
                yield break;
            }
            if (Directory.EnumerateFiles(directory, "*.gguf").Any())
            {
                Logs.Warning($"[LLMAssistant] '{directory}' holds both a GGUF and a checkpoint, which the engine cannot tell apart. Keep them in separate folders; this one is not listed.");
                yield break;
            }
            yield return Describe(root, info);
            yield break;
        }
        if (depth >= MaxDepth)
        {
            yield break;
        }
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logs.Debug($"[LocalModelDiscovery] Could not list '{directory}': {ex.Message}");
            yield break;
        }
        foreach (string child in children)
        {
            string name = Path.GetFileName(child);
            if (name.StartsWith('.') || name.StartsWith('_'))
            {
                continue;
            }
            foreach (CheckpointModel model in Walk(root, child, depth + 1))
            {
                yield return model;
            }
        }
    }

    private static CheckpointModel Describe(string root, HfCheckpointInfo info)
    {
        string full = Path.GetFullPath(info.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string id = Path.GetRelativePath(root, full).Replace('\\', '/');
        long size = 0;
        foreach (string shard in Directory.EnumerateFiles(full, "*.safetensors"))
        {
            try
            {
                FileInfo file = new(shard);
                size += (file.ResolveLinkTarget(true) as FileInfo)?.Length ?? file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logs.Debug($"[LocalModelDiscovery] Could not stat '{shard}': {ex.Message}");
            }
        }
        (string quantization, int context) = ReadConfig(info.ConfigPath);
        return new CheckpointModel(id, Path.GetFileName(full), full, size, quantization, context);
    }

    /// <summary>The weights label and context length from a checkpoint's <c>config.json</c>; ("BF16", -1) when it says neither.</summary>
    internal static (string Quantization, int ContextLength) ReadConfig(string configPath)
    {
        string quantization = "BF16";
        int context = -1;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(configPath));
            JsonElement config = document.RootElement;
            if (TryGetObject(config, "quantization_config", out JsonElement quant))
            {
                string method = quant.TryGetProperty("quant_method", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                quantization = method switch
                {
                    "bitsandbytes" => quant.TryGetProperty("bnb_4bit_quant_type", out JsonElement t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString().ToUpperInvariant() : "INT8",
                    "compressed-tensors" => "FP8",
                    null => "quantized",
                    _ => method.ToUpperInvariant(),
                };
            }
            JsonElement text = TryGetObject(config, "text_config", out JsonElement t2) ? t2 : config;
            if (text.TryGetProperty("max_position_embeddings", out JsonElement positions) && positions.TryGetInt32(out int value) && value > 0)
            {
                context = value;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Logs.Debug($"[LocalModelDiscovery] Could not read '{configPath}': {ex.Message}");
        }
        return (quantization, context);
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement child)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        child = default;
        return false;
    }

    /// <summary>Resolves a model id to the path the engine loads: a GGUF whose file name (or full path) matches, else a checkpoint folder
    /// whose id matches (either slash style, any case). Null when nothing matches.</summary>
    internal static string ResolveModelPath(string modelId, IReadOnlyCollection<string> folders)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }
        foreach (string folder in folders)
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).Equals(modelId, StringComparison.OrdinalIgnoreCase) || file.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        string wanted = modelId.Replace('\\', '/').Trim('/');
        return EnumerateCheckpointModels(folders).FirstOrDefault(m => m.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase))?.FullPath;
    }
}
