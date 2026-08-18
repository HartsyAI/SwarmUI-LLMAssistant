using System.IO;
using System.Linq;
using FreneticUtilities.FreneticDataSyntax;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using SwarmUI.Utils;
using HartsyInference.Engine;
using HartsyInference.Engine.Dispatch;
using HartsyInference.Engine.Requests;
using HartsyInference.Engine.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hartsy.Extensions.LLMAssistant.Backends;

/// <summary>Thin local LLM backend: maps the extension's chat input onto HartsyInference.Engine's native
/// <see cref="ITextService"/> contract (<see cref="TextRequest"/>/<see cref="TextChunk"/>) and lets the engine
/// own model loading, device slots, sampling, chat templating, and the vision path. No GGUF/tokenizer/pipeline
/// code lives here — see HartsyInference's <c>TextService</c> for that orchestration.</summary>
public class HartsyLocalLLMProvider : LLMProviderBackend
{
    public class HartsyLocalLLMProviderSettings : AutoConfiguration
    {
        [ConfigComment("Compute device to run inference on.\nCUDA = NVIDIA GPU (fast, recommended). CPU = no GPU needed (much slower).\nMore devices (Vulkan, etc.) will be added later. The GPU kernels ship with the engine — nothing to configure.")]
        [ManualSettingsOptions(Vals = ["cuda", "cpu"], ManualNames = ["CUDA (NVIDIA GPU)", "CPU"])]
        public string Device = "cuda";

        [ConfigComment("Which CUDA device ordinal to use (only when Device = CUDA).")]
        public int GPUDeviceId = 0;

        [ConfigComment("Keep quantized weights compressed on-device (lower VRAM, slower decode) instead of caching dequantized F16 weights.")]
        public bool LowVramQuant = false;

        [ConfigComment("Repetition penalty on already-generated tokens (1.0 = off).\nSmall models (eg 0.5B) loop/repeat without this — ~1.1 is a good default. Ignored at temperature 0 (greedy).")]
        public double RepetitionPenalty = 1.1;

        [ConfigComment("Top-K sampling: keep only the K highest-probability tokens each step (0 = off).\n~40 is a sane default that curbs small-model gibberish.")]
        public int TopK = 40;

        [ConfigComment("Min-P sampling: drop tokens below this fraction of the top token's probability (0 = off).")]
        public double MinP = 0.0;

        [ConfigComment("If enabled, the model is unloaded immediately after each generation completes.\nIf false, it stays resident for faster subsequent requests.")]
        public bool AlwaysFreeMemory = false;

        [ConfigComment("Use CUDA-graph decode when eligible (plain dense Llama/Qwen/Mistral-shape models, CUDA backend only).\nRemoves per-token kernel-launch overhead for faster decode, but only kicks in when the request ends up greedy\n(Temperature = 0) — normal temperature sampling on the chat UI still uses the regular decode path for now.")]
        public bool GraphDecode = false;

        [ConfigComment("Use prompt-lookup speculative decoding when eligible (no draft model — drafts from repeated n-grams in the\nprompt/response so far). Same eligibility as GraphDecode (greedy / Temperature = 0 only) plus JSON mode must be off.\nBiggest win on repetitive output (regenerating similar structure, quoting earlier text); costs nothing extra otherwise.")]
        public bool SpeculativeDecode = false;

        [ConfigComment("When tools are enabled for a chat, grammar-mask the JSON body of a <tool_call>{...}</tool_call> block so it's\nalways syntactically valid — plain chat text stays completely unconstrained, only the JSON between the tags is\nguaranteed-valid (same technique every major chat API uses: constrain only the tool-call span, never the whole\nreply). Off by default until verified against real models — see docs.")]
        public bool StructuredToolCalling = false;
    }

    /// <summary>The settings for this backend.</summary>
    public HartsyLocalLLMProviderSettings Settings => SettingsRaw as HartsyLocalLLMProviderSettings;

    /// <summary>The engine instance backing this backend's requests. One per backend instance, disposed on shutdown.</summary>
    private InferenceEngine Engine;

    /// <inheritdoc/>
    public override string ProviderKind => "hartsy-local";

    /// <inheritdoc/>
    public override string DisplayName => "Local LLM (HartsyInference, GGUF)";

    /// <inheritdoc/>
    public override IEnumerable<string> SupportedFeatures => ["llm", "local_llm"];

    /// <inheritdoc/>
    protected override Task OnProviderInit()
    {
        Engine = new InferenceEngine(string.Equals(Settings.Device, "cpu", StringComparison.OrdinalIgnoreCase) ? "cpu" : "cuda");
        Status = BackendStatus.RUNNING; // Lazy: load on first request.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task OnProviderShutdown()
    {
        Engine?.Dispose();
        Engine = null;
        Status = BackendStatus.DISABLED;
        return Task.CompletedTask;
    }

    /// <summary>This backend's configured/default device key ("cpu" or "cuda:{ordinal}").</summary>
    private string PrimaryDeviceKey()
        => string.Equals(Settings.Device, "cpu", StringComparison.OrdinalIgnoreCase) ? "cpu" : $"cuda:{Settings.GPUDeviceId}";

    /// <summary>The devices this backend can run a model on. A CUDA backend can also fall back to the CPU,
    /// so it offers both — letting compare mode place one lane on the GPU and one on the CPU to run at once.</summary>
    private List<string> SupportedDevices()
    {
        string primary = PrimaryDeviceKey();
        List<string> devs = [primary];
        if (primary != "cpu")
        {
            devs.Add("cpu");
        }
        return devs;
    }

    /// <summary>Normalizes a requested device string to a slot key. Blank or bare "cuda" → this backend's
    /// configured device; otherwise the lowercased key ("cpu", "cuda:1", …) as-is.</summary>
    private string NormalizeDeviceKey(string device)
    {
        if (string.IsNullOrWhiteSpace(device))
        {
            return PrimaryDeviceKey();
        }
        string key = device.Trim().ToLowerInvariant();
        return key == "cuda" ? PrimaryDeviceKey() : key;
    }

    /// <summary>The configured LLM model folders (mirrors LLMAssistantExtension.RegisterLLMModelType).</summary>
    private static IEnumerable<string> ModelFolders()
    {
        foreach (string root in Program.ServerSettings.Paths.ActualModelRoots)
        {
            string path = Path.Combine(root, "llm");
            if (Directory.Exists(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>Resolves a model id (file name) to a full GGUF path, or null if not found.</summary>
    private static string ResolvePath(string modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            return null;
        }
        foreach (string folder in ModelFolders())
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).Equals(modelId, StringComparison.OrdinalIgnoreCase) || file.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        return null;
    }

    /// <inheritdoc/>
    public override async Task GenerateLive(ExtendedLLMInput input, string batchId, Action<JObject> onChunk, CancellationToken ct)
    {
        string deviceKey = NormalizeDeviceKey(input.Device);
        string path = ResolvePath(input.Model);
        if (path is null)
        {
            throw new SwarmReadableErrorException($"LLM model '{input.Model}' not found in the LLM model folder(s). Drop a .gguf file into Models/llm.");
        }
        ModelSpec spec = new() { Requested = input.Model, Modality = Modality.Text, LocalPath = path };
        TextRequest request = await BuildRequestAsync(input, deviceKey, ct);
        await foreach (TextChunk chunk in Engine.Text.StreamAsync(spec, request, ct))
        {
            switch (chunk.Kind)
            {
                case TextChunkKind.Chunk:
                    onChunk(new JObject() { ["chunk"] = chunk.Text });
                    break;
                case TextChunkKind.StopReason:
                    // Only surface truncation/cancellation/error — a normal finish (Stop) is the common case and
                    // the old provider never emitted a stopReason event for it; matching that keeps the wire
                    // format's "stopReason present" signal meaning "something other than a clean finish happened".
                    if (chunk.Stop is StopReason.Length)
                    {
                        onChunk(new JObject() { ["stopReason"] = "length" });
                    }
                    else if (chunk.Stop is StopReason.Cancelled)
                    {
                        onChunk(new JObject() { ["stopReason"] = "cancelled" });
                    }
                    else if (chunk.Stop is StopReason.Error)
                    {
                        onChunk(new JObject() { ["stopReason"] = "error" });
                    }
                    break;
                    // Result duplicates the text already streamed as Chunk events — LLMProviderBackend.Generate's
                    // non-streaming accumulator appends both "chunk" and "result", so forwarding Result here would
                    // double the text. NativeToolCall is never emitted by TextService today (contract-only).
            }
        }
    }

    /// <summary>Builds the engine's native request from the extension's input: messages/roles, sampling knobs
    /// from settings plus per-request overrides, the vision attachment (decoded only for the final user turn —
    /// the engine's vision path only ever looks at the latest one), and tool definitions when structured tool
    /// calling is enabled.</summary>
    private async Task<TextRequest> BuildRequestAsync(ExtendedLLMInput input, string deviceKey, CancellationToken ct)
    {
        List<LLMMessage> source = input.Messages is { Count: > 0 } ? input.Messages : SyntheticMessages(input);
        int lastUserIdx = -1;
        for (int i = 0; i < source.Count; i++)
        {
            if (source[i].Role == LLMRoles.User)
            {
                lastUserIdx = i;
            }
        }
        List<TextMessage> messages = [];
        for (int i = 0; i < source.Count; i++)
        {
            LLMMessage m = source[i];
            string content = m.Content ?? "";
            List<ImageData> images = null;
            if (i == lastUserIdx && m.Media is { Count: > 0 })
            {
                images = await DecodeImagesAsync(m.Media, ct);
                if (images.Count > 0)
                {
                    // The chat layer inlines "[Attached image URL: ...]" into the text alongside real media so
                    // the model can reference the path when chaining tools — that annotation must never reach a
                    // vision model's literal question (it badly degrades small VLMs), so strip it here.
                    content = StripImageAnnotations(content);
                }
            }
            messages.Add(new TextMessage
            {
                Role = RoleFor(m.Role),
                Content = content,
                Images = images is { Count: > 0 } ? images : null
            });
        }
        List<ToolDefinition> tools = null;
        if (Settings.StructuredToolCalling && input.Tools is { Count: > 0 })
        {
            tools = [.. input.Tools.Select(t => new ToolDefinition
            {
                Name = t["name"]?.ToString() ?? "",
                Description = t["description"]?.ToString() ?? "",
                JsonSchema = (t["parameters"] as JObject ?? new JObject()).ToString()
            })];
        }
        return new TextRequest
        {
            Messages = messages,
            // Not SystemPrompt too: ExtendedLLMInput always folds the system prompt into Messages[0] (and
            // keeps it in sync — see ApplyToolsToInput), so setting both here double-injects it into the
            // chat template. Confirmed live: this produced garbage/off-topic output from real GGUF vision
            // models under the WS chat path (verified 2026-07-25 testing against llava-v1.5-7b/Qwen2.5-VL-7B).
            Temperature = Math.Max(0, input.Temperature),
            TopP = input.TopP > 0 ? input.TopP : 1.0,
            TopK = Settings.TopK > 0 ? Settings.TopK : null,
            MinP = Settings.MinP > 0 ? Settings.MinP : null,
            RepetitionPenalty = Settings.RepetitionPenalty > 0 ? Settings.RepetitionPenalty : null,
            MaxTokens = input.MaxTokens > 0 ? input.MaxTokens : 4096,
            Seed = input.Seed,
            Greedy = input.Temperature <= 0,
            Device = deviceKey,
            Tools = tools,
            GraphDecode = Settings.GraphDecode ? true : null,
            SpeculativeDecode = Settings.SpeculativeDecode ? true : null,
            LowVramQuant = Settings.LowVramQuant ? "true" : null,
            AlwaysFreeMemory = Settings.AlwaysFreeMemory
        };
    }

    /// <summary>Builds a minimal message list from the legacy UserMessage/SystemPrompt fields, for callers that
    /// never populated <see cref="ExtendedLLMInput.Messages"/> (mirrors <see cref="ExtendedLLMInput.Create"/>).</summary>
    private static List<LLMMessage> SyntheticMessages(ExtendedLLMInput input)
    {
        List<LLMMessage> list = [];
        if (!string.IsNullOrEmpty(input.SystemPrompt))
        {
            list.Add(new LLMMessage { Role = LLMRoles.System, Content = input.SystemPrompt });
        }
        list.Add(new LLMMessage { Role = LLMRoles.User, Content = input.UserMessage ?? "" });
        return list;
    }

    private static TextRole RoleFor(string role) => role switch
    {
        LLMRoles.System => TextRole.System,
        LLMRoles.Assistant => TextRole.Assistant,
        _ => TextRole.User
    };

    /// <summary>Resolves each attachment (URL/base64/data-URI) to bytes and decodes to interleaved RGB — the
    /// engine's native <see cref="ImageData"/> shape, so it owns resizing/normalization per vision encoder.</summary>
    private static async Task<List<ImageData>> DecodeImagesAsync(List<LLMMediaAttachment> media, CancellationToken ct)
    {
        List<ImageData> images = [];
        foreach (LLMMediaAttachment att in media)
        {
            if (att is null || string.IsNullOrEmpty(att.Data))
            {
                continue;
            }
            string reference = att.Type == "base64"
                ? $"data:{(string.IsNullOrEmpty(att.MediaType) ? "image/png" : att.MediaType)};base64,{att.Data}"
                : att.Data;
            ImageInputResolver.ResolvedImage resolved = await ImageInputResolver.ResolveAsync(reference, ct);
            using SixLabors.ImageSharp.Image<Rgb24> img = SixLabors.ImageSharp.Image.Load<Rgb24>(resolved.Bytes);
            byte[] rgb = new byte[img.Width * img.Height * 3];
            img.CopyPixelDataTo(rgb);
            images.Add(new ImageData { Rgb = rgb, Width = img.Width, Height = img.Height });
        }
        return images;
    }

    /// <summary>Removes the "[Attached image URL: …]" lines the chat layer appends to a user message (for
    /// tool-chaining). Those must never reach the VLM's question — the image is already delivered as pixels,
    /// not as a URL, and the raw path text badly degrades small models.</summary>
    private static string StripImageAnnotations(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        IEnumerable<string> kept = text.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("[Attached image URL:", StringComparison.OrdinalIgnoreCase));
        return string.Join('\n', kept).Trim();
    }

    /// <inheritdoc/>
    public override int? CountTokens(string text)
    {
        if (Engine is null)
        {
            return null;
        }
        // ITextService.CountTokens never returns null (it falls back to a chars/4 heuristic internally when no
        // slot is loaded), so this can report a count as "exact" to LLMDispatcher even when nothing is resident.
        // Harmless: the returned number is the same estimate either way, just possibly mislabeled as exact.
        return Engine.Text.CountTokens(new ModelSpec { Requested = "", Modality = Modality.Text }, text ?? "");
    }

    /// <inheritdoc/>
    public override Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default)
    {
        List<LLMModelInfo> models = [];
        string deviceLabel = PrimaryDeviceKey();
        string devices = string.Join(",", SupportedDevices());
        foreach (string folder in ModelFolders())
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string id = Path.GetFileName(file);
                // mmproj projectors are companions to a text model, not selectable chat models — hide them.
                if (id.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                long size = -1;
                try { size = new FileInfo(file).Length; }
                catch (Exception ex) { Logs.Debug($"[HartsyLocalLLMProvider] Could not stat '{file}': {ex.Message}"); }
                LLMModelInfo info = new()
                {
                    Id = id,
                    Name = Path.GetFileNameWithoutExtension(file),
                    Provider = "hartsy-local",
                    BackendId = AbstractBackendData?.ID ?? -1,
                    SizeBytes = size,
                    // ITextService exposes no slot-residency query, so "loaded" can't be answered here without
                    // the extension tracking its own (easily-stale) shadow of engine state — report unknown
                    // rather than a badge that can silently lie after an out-of-band free/evict.
                    IsLoaded = false,
                    Metadata = { ["device"] = deviceLabel, ["devices"] = devices }
                };
                // Advertise vision capability when a sidecar mmproj sits next to the model (UI can badge it).
                if (TextService.FindMmproj(file) is not null)
                {
                    info.Metadata["vision"] = "true";
                }
                models.Add(info);
            }
        }
        return Task.FromResult(models);
    }

    /// <inheritdoc/>
    public override Task<bool> FreeMemory(bool systemRam) => Task.FromResult(Engine?.Text.Unload() ?? false);
}
