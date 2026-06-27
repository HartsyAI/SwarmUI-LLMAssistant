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

    /// <summary>Serializes load + generate; the transformer/backend are not safe for concurrent use.</summary>
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IBackend _backend;
    private GgufLanguageModel _model;
    private TextGenerationPipeline _pipeline;
    private string _loadedPath;

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
        await _lock.WaitAsync();
        try
        {
            Unload();
            _backend?.Dispose();
            _backend = null;
        }
        finally
        {
            _lock.Release();
        }
        Status = BackendStatus.DISABLED;
    }

    /// <summary>Creates the compute backend for the configured <c>Device</c>. The CUDA PTX kernels ship with
    /// the engine and are auto-copied next to the extension DLL (NuGet build targets, or the csproj's local
    /// Content copy), so the user never configures a kernel path.</summary>
    private IBackend CreateBackend()
    {
        string dev = (Settings.Device ?? "cuda").Trim().ToLowerInvariant();
        return dev switch
        {
            "cpu" => new CpuBackend(),
            "cuda" => new CudaBackend(deviceOrdinal: Settings.GPUDeviceId, ptxDir: ResolvePtxDir()),
            _ => throw new SwarmReadableErrorException($"Local LLM device '{Settings.Device}' is not supported yet — choose CUDA or CPU."),
        };
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

    /// <summary>Loads the requested model if it isn't already the loaded one. Caller holds <see cref="_lock"/>.</summary>
    private void Load(string modelId)
    {
        string path = ResolvePath(modelId);
        if (path is null)
        {
            throw new SwarmReadableErrorException($"LLM model '{modelId}' not found in the LLM model folder(s). Drop a .gguf file into Models/llm.");
        }
        if (_model is not null && _loadedPath == path)
        {
            return;
        }
        Unload();
        _backend ??= CreateBackend();
        _model = GgufLanguageModel.Load(path, Settings.LowVramQuant);
        if (_backend is CudaBackend)
        {
            _backend.PreloadWeights(_model.Transformer.EnumerateWeights());
        }
        _pipeline = new TextGenerationPipeline(_model.Transformer, _model.Tokenizer, _backend, _model.Template);
        _loadedPath = path;
        Logs.Info($"[HartsyLocalLLMProvider] Loaded GGUF model '{Path.GetFileName(path)}' ({_model.Architecture}).");
    }

    /// <summary>Frees the loaded model (keeps the backend/device alive). Caller holds <see cref="_lock"/>.</summary>
    private void Unload()
    {
        if (_model is not null && _backend is CudaBackend)
        {
            try { _backend.FreeWeights(_model.Transformer.EnumerateWeights()); }
            catch (Exception ex) { Logs.Debug($"[HartsyLocalLLMProvider] FreeWeights failed: {ex.Message}"); }
        }
        _pipeline = null;
        _model?.Dispose();
        _model = null;
        _loadedPath = null;
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
        await _lock.WaitAsync(ct);
        try
        {
            Load(input.Model);
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
                string full = _model.Tokenizer.Decode(acc);
                if (full.Length > emitted)
                {
                    onChunk(new JObject() { ["chunk"] = full[emitted..] });
                    emitted = full.Length;
                }
            }
            await Task.Run(() => _pipeline.Generate(request, OnToken), ct);
            if (Settings.AlwaysFreeMemory)
            {
                Unload();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public override int? CountTokens(string text)
    {
        // Non-blocking: if a generation holds the lock (and may be swapping/disposing the model),
        // don't race it — return null so the caller uses the heuristic. Never loads a model here.
        if (!_lock.Wait(0))
        {
            return null;
        }
        try
        {
            return _model?.Tokenizer.EncodeOrdinary(text ?? "").Length;
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public override async Task<List<LLMModelInfo>> ListModels(CancellationToken ct = default)
    {
        List<LLMModelInfo> models = [];
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
                    IsLoaded = _loadedPath == file
                });
            }
        }
        return models;
    }

    /// <inheritdoc/>
    public override async Task<bool> FreeMemory(bool systemRam)
    {
        await _lock.WaitAsync();
        try
        {
            bool had = _model is not null;
            Unload();
            return had;
        }
        finally
        {
            _lock.Release();
        }
    }
}
