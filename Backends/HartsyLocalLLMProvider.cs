using System.Collections.Concurrent;
using System.IO;
using FreneticUtilities.FreneticDataSyntax;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Extensions.LLMAssistant.LLMs;
using SwarmUI.Utils;
using HartsyInference.Core.Backends;
using HartsyInference.Cpu;
using HartsyInference.Cuda;
using HartsyInference.LLM.ChatTemplates;
using HartsyInference.LLM.Generation;
using HartsyInference.LLM.Sampling;

namespace SwarmUI.Extensions.LLMAssistant.Backends;

/// <summary>Fully-native local LLM backend powered by the pure-C# HartsyInference.LLM engine
/// (no llama.cpp binding, no external process). Loads GGUF text models (Qwen2/Qwen3/Llama family)
/// from the <c>LLM</c> model folder and streams tokens straight from the C# transformer.</summary>
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
    }

    /// <summary>The settings for this backend.</summary>
    public HartsyLocalLLMProviderSettings Settings => SettingsRaw as HartsyLocalLLMProviderSettings;

    /// <summary>One compute device's loaded state. A single transformer/backend/pipeline is NOT safe for
    /// concurrent use, so each slot has its own lock — but two DIFFERENT slots (eg cuda:0 + cpu) can generate
    /// at the same time. This is what lets compare mode run two lanes truly in parallel: put them on
    /// different devices.</summary>
    private sealed class DeviceSlot
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public IBackend Backend;
        public GgufLanguageModel Model;
        public TextGenerationPipeline Pipeline;
        public string LoadedPath;
    }

    /// <summary>Loaded state per device key ("cpu", "cuda:0", …). Created lazily on first request for a
    /// device. Each holds at most one model (swapped when a different model is asked of that device).</summary>
    private readonly ConcurrentDictionary<string, DeviceSlot> _slots = new();

    /// <inheritdoc/>
    public override string ProviderKind => "hartsy-local";

    /// <inheritdoc/>
    public override string DisplayName => "Local LLM (HartsyInference, GGUF)";

    /// <inheritdoc/>
    public override IEnumerable<string> SupportedFeatures => ["llm", "local_llm"];

    /// <inheritdoc/>
    protected override async Task OnProviderInit() => Status = BackendStatus.RUNNING; // Lazy: load on first request.

    /// <inheritdoc/>
    protected override async Task OnProviderShutdown()
    {
        foreach (DeviceSlot slot in _slots.Values)
        {
            await slot.Lock.WaitAsync();
            try
            {
                UnloadSlot(slot);
                slot.Backend?.Dispose();
                slot.Backend = null;
            }
            finally
            {
                slot.Lock.Release();
            }
        }
        _slots.Clear();
        Status = BackendStatus.DISABLED;
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

    /// <summary>Creates the compute backend for a device key ("cpu" / "cuda:{ordinal}"). The CUDA PTX kernels
    /// ship with the engine and are auto-copied next to the extension DLL, so no kernel path is configured.</summary>
    private static IBackend CreateBackendFor(string deviceKey)
    {
        string key = (deviceKey ?? "cuda").Trim().ToLowerInvariant();
        if (key == "cpu")
        {
            return new CpuBackend();
        }
        if (key.StartsWith("cuda"))
        {
            int ordinal = 0;
            int colon = key.IndexOf(':');
            if (colon >= 0 && int.TryParse(key[(colon + 1)..], out int n))
            {
                ordinal = n;
            }
            return new CudaBackend(deviceOrdinal: ordinal, ptxDir: ResolvePtxDir());
        }
        throw new SwarmReadableErrorException($"Local LLM device '{deviceKey}' is not supported yet — choose CUDA or CPU.");
    }

    /// <summary>The bundled CUDA PTX kernel directory: <c>Ptx/</c> next to the extension DLL. (Can't use
    /// <c>AppContext.BaseDirectory</c> — that's the SwarmUI host dir inside Swarm; derive from this assembly,
    /// matching AudioLab / the HartsyInference backend extension.)</summary>
    private static string ResolvePtxDir()
    {
        string extDir = Path.GetDirectoryName(typeof(HartsyLocalLLMProvider).Assembly.Location) ?? AppContext.BaseDirectory;
        return Path.Combine(extDir, "Ptx");
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

    /// <summary>The lazily-created slot for a device key.</summary>
    private DeviceSlot GetSlot(string deviceKey) => _slots.GetOrAdd(deviceKey, _ => new DeviceSlot());

    /// <summary>Loads the requested model onto the slot's device if it isn't already loaded there. The
    /// slot's backend is created on first use for that device. Caller holds <c>slot.Lock</c>.</summary>
    private void LoadInto(DeviceSlot slot, string deviceKey, string modelId)
    {
        string path = ResolvePath(modelId);
        if (path is null)
        {
            throw new SwarmReadableErrorException($"LLM model '{modelId}' not found in the LLM model folder(s). Drop a .gguf file into Models/llm.");
        }
        if (slot.Model is not null && slot.LoadedPath == path)
        {
            return;
        }
        UnloadSlot(slot);
        slot.Backend ??= CreateBackendFor(deviceKey);
        slot.Model = GgufLanguageModel.Load(path, Settings.LowVramQuant);
        if (slot.Backend is CudaBackend)
        {
            slot.Backend.PreloadWeights(slot.Model.Transformer.EnumerateWeights());
        }
        slot.Pipeline = new TextGenerationPipeline(slot.Model.Transformer, slot.Model.Tokenizer, slot.Backend, slot.Model.Template);
        slot.LoadedPath = path;
        Logs.Info($"[HartsyLocalLLMProvider] Loaded GGUF model '{Path.GetFileName(path)}' ({slot.Model.Architecture}) on {deviceKey}.");
    }

    /// <summary>Frees the slot's loaded model (keeps its backend/device alive). Caller holds <c>slot.Lock</c>.</summary>
    private static void UnloadSlot(DeviceSlot slot)
    {
        // Full device eviction, not per-weight FreeWeights. A slot's CUDA backend holds exactly one model at a
        // time, so on unload/swap everything on its context is dead — weights, the dequantized F16 weight-casts,
        // any lingering activations, and the KV cache. Per-weight FreeWeights(EnumerateWeights()) missed the
        // cast/activation/pool memory (and depends on Tensor reference identity), which leaked several GB per
        // model swap → VRAM climbed to ~10 GB and OOM'd after a few models. FreeAllDeviceMemory = EvictAll +
        // TrimPool reclaims all of it and returns the stream-ordered pool reservations to the driver.
        if (slot.Model is not null && slot.Backend is CudaBackend cuda)
        {
            try { cuda.FreeAllDeviceMemory(); }
            catch (Exception ex) { Logs.Debug($"[HartsyLocalLLMProvider] FreeAllDeviceMemory failed: {ex.Message}"); }
        }
        slot.Pipeline = null;
        slot.Model?.Dispose();
        slot.Model = null;
        slot.LoadedPath = null;
    }

    /// <summary>Builds the engine generation request from the extension's input. Per-request controls
    /// (temperature/top-p/seed/max-tokens) come from the chat UI; model-tuning knobs (top-k / repetition
    /// penalty / min-p) come from this backend's settings.</summary>
    private GenerationRequest BuildRequest(ExtendedLLMInput input)
    {
        SamplingOptions sampling = SamplingOptions.Default with
        {
            Temperature = (float)Math.Max(0, input.Temperature),
            TopP = (float)(input.TopP > 0 ? input.TopP : 1.0),
            TopK = Math.Max(0, Settings.TopK),
            MinP = (float)Math.Max(0, Settings.MinP),
            RepetitionPenalty = (float)(Settings.RepetitionPenalty > 0 ? Settings.RepetitionPenalty : 1.0),
            Seed = input.Seed >= 0 ? (ulong)input.Seed : 0,
            Greedy = input.Temperature <= 0
        };
        GenerationRequest request = new()
        {
            MaxTokens = input.MaxTokens > 0 ? input.MaxTokens : 1024,
            Sampling = sampling
        };
        if (input.Messages is not null && input.Messages.Count > 0)
        {
            // GGUF text models are not multimodal here — content text only.
            request = request with { Messages = [.. input.Messages.Select(m => new ChatMessage(m.Role, m.Content ?? ""))] };
        }
        else
        {
            request = request with { Prompt = input.UserMessage ?? "", SystemPrompt = input.SystemPrompt };
        }
        return request;
    }

    /// <inheritdoc/>
    public override async Task GenerateLive(ExtendedLLMInput input, string batchId, Action<JObject> onChunk, CancellationToken ct)
    {
        // Route to the requested device's slot. Different devices → different locks → concurrent generation
        // (this is how two compare lanes on cuda:0 + cpu run at the same time); same device → serialized.
        string deviceKey = NormalizeDeviceKey(input.Device);
        DeviceSlot slot = GetSlot(deviceKey);
        await slot.Lock.WaitAsync(ct);
        try
        {
            LoadInto(slot, deviceKey, input.Model);
            GenerationRequest request = BuildRequest(input);
            // Stream by decoding the running token list and emitting the newly-appeared text. (Byte-level
            // BPE means a single token doesn't cleanly map to a substring, so decode-and-diff is the
            // correct way to surface incremental text.)
            List<int> acc = [];
            int emitted = 0;
            void OnToken(int id)
            {
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                acc.Add(id);
                string full = slot.Model.Tokenizer.Decode(acc);
                if (full.Length > emitted)
                {
                    onChunk(new JObject() { ["chunk"] = full[emitted..] });
                    emitted = full.Length;
                }
            }
            await Task.Run(() => slot.Pipeline.Generate(request, OnToken), ct);
            if (Settings.AlwaysFreeMemory)
            {
                UnloadSlot(slot);
            }
        }
        finally
        {
            slot.Lock.Release();
        }
    }

    /// <inheritdoc/>
    public override int? CountTokens(string text)
    {
        // Non-blocking: use whichever loaded slot we can grab without racing an in-flight generation.
        // Never loads a model here; returns null so the caller falls back to the heuristic if none is free.
        foreach (DeviceSlot slot in _slots.Values)
        {
            if (!slot.Lock.Wait(0))
            {
                continue;
            }
            try
            {
                if (slot.Model is not null)
                {
                    return slot.Model.Tokenizer.EncodeOrdinary(text ?? "").Length;
                }
            }
            catch
            {
                // Try the next slot / fall through to heuristic.
            }
            finally
            {
                slot.Lock.Release();
            }
        }
        return null;
    }

    /// <inheritdoc/>
    public override async Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default)
    {
        List<LLMModelInfo> models = [];
        // "device" = this backend's primary/default device (shown when there's nothing to choose). "devices"
        // = every device it can run a model on (primary + cpu fallback), which the compare picker expands
        // into a per-lane dropdown so a request can be routed to a specific device at generation time.
        string deviceLabel = PrimaryDeviceKey();
        string devices = string.Join(",", SupportedDevices());
        HashSet<string> loadedPaths = [.. _slots.Values.Select(s => s.LoadedPath).Where(p => p is not null)];
        foreach (string folder in ModelFolders())
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string id = Path.GetFileName(file);
                long size = -1;
                try { size = new FileInfo(file).Length; } catch { }
                models.Add(new LLMModelInfo()
                {
                    Id = id,
                    Name = Path.GetFileNameWithoutExtension(file),
                    Provider = "hartsy-local",
                    BackendId = AbstractBackendData?.ID ?? -1,
                    SizeBytes = size,
                    IsLoaded = loadedPaths.Contains(file),
                    Metadata = { ["device"] = deviceLabel, ["devices"] = devices }
                });
            }
        }
        return models;
    }

    /// <inheritdoc/>
    public override async Task<bool> FreeMemory(bool systemRam)
    {
        bool had = false;
        foreach (DeviceSlot slot in _slots.Values)
        {
            await slot.Lock.WaitAsync();
            try
            {
                had |= slot.Model is not null;
                UnloadSlot(slot);
            }
            finally
            {
                slot.Lock.Release();
            }
        }
        return had;
    }
}
