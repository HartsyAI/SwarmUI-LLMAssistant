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
using HartsyInference.Core.Tensors;
using HartsyInference.LLM.Multimodal;
using SwarmUI.Extensions.LLMAssistant.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
        /// <summary>The compute backend (CPU/CUDA) bound to this slot's device.</summary>
        public IBackend Backend;
        /// <summary>The currently-loaded GGUF text model, or null if nothing is loaded.</summary>
        public GgufLanguageModel Model;
        /// <summary>The generation pipeline built for <see cref="Model"/> on <see cref="Backend"/>.</summary>
        public TextGenerationPipeline Pipeline;
        /// <summary>Full path of the currently-loaded GGUF file, or null if nothing is loaded.</summary>
        public string LoadedPath;

        // Multimodal (vision) state, loaded alongside the text model when a sidecar mmproj is found.
        // Exactly one of these is non-null on a vision model; both null on a plain text model.
        public IVlmImageEncoder SpliceVision;    // gemma3 / qwen2-vl / qwen2.5-vl / minicpm-v / internvl / llava / smolvlm
        public MllamaVisionEncoder MllamaVision; // llama-3.2-vision (cross-attention, separate generator)
        public string VisionPath;                // the loaded mmproj path (null = no vision)
    }

    /// <summary>OpenAI CLIP normalization used by the mllama (Llama-3.2-Vision) image processor. The splice
    /// encoders expose their own mean/std via <see cref="IVlmImageEncoder"/>; mllama does not, so it's fixed here.</summary>
    private static readonly float[] MllamaMean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] MllamaStd = [0.26862954f, 0.26130258f, 0.27577711f];

    /// <summary>Loaded state per device key ("cpu", "cuda:0", …). Created lazily on first request for a
    /// device. Each holds at most one model (swapped when a different model is asked of that device).</summary>
    private readonly ConcurrentDictionary<string, DeviceSlot> Slots = new();

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
        foreach (DeviceSlot slot in Slots.Values)
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
        Slots.Clear();
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
    private DeviceSlot GetSlot(string deviceKey) => Slots.GetOrAdd(deviceKey, _ => new DeviceSlot());

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
        bool isCpu = slot.Backend is CpuBackend;
        slot.Model = GgufLanguageModel.Load(path, Settings.LowVramQuant, dequantizeToF32: isCpu);
        if (slot.Backend is CudaBackend)
        {
            slot.Backend.PreloadWeights(slot.Model.Transformer.EnumerateWeights());
        }
        slot.Pipeline = new TextGenerationPipeline(slot.Model.Transformer, slot.Model.Tokenizer, slot.Backend, slot.Model.Template);
        slot.LoadedPath = path;
        LoadVisionInto(slot, path);
        Logs.Info($"[HartsyLocalLLMProvider] Loaded GGUF model '{Path.GetFileName(path)}' ({slot.Model.Architecture}) on {deviceKey}"
            + (slot.VisionPath is not null ? $" + vision '{Path.GetFileName(slot.VisionPath)}'." : "."));
    }

    /// <summary>Pairs the just-loaded text model with a sidecar mmproj GGUF (if one sits next to it) and loads
    /// the matching vision encoder: the cross-attention <see cref="MllamaVisionEncoder"/> for Llama-3.2-Vision,
    /// or a splice <see cref="IVlmImageEncoder"/> (Qwen2.x-VL vs SigLIP-family) for everyone else. No mmproj →
    /// the model stays text-only. Caller holds <c>slot.Lock</c>.</summary>
    private static void LoadVisionInto(DeviceSlot slot, string textPath)
    {
        string mmproj = FindMmproj(textPath);
        if (mmproj is null)
        {
            return;
        }
        try
        {
            bool isMllama = slot.Model.Architecture == "mllama" || (slot.Model.Config.CrossAttnLayers?.Count ?? 0) > 0;
            if (isMllama)
            {
                slot.MllamaVision = MllamaVisionEncoder.Load(mmproj);
            }
            else
            {
                slot.SpliceVision = IsQwen25Vl(mmproj) ? Qwen25VlEncoder.Load(mmproj) : SiglipVlmEncoder.Load(mmproj);
            }
            slot.VisionPath = mmproj;
        }
        catch (Exception ex)
        {
            // A bad/unsupported mmproj must not break text generation — degrade to text-only.
            slot.SpliceVision = null;
            slot.MllamaVision = null;
            slot.VisionPath = null;
            Logs.Warning($"[HartsyLocalLLMProvider] Failed to load vision encoder '{Path.GetFileName(mmproj)}': {ex.Message}. Model stays text-only.");
        }
    }

    /// <summary>Finds a sidecar mmproj GGUF next to a text model (any *.gguf in the same folder whose name
    /// contains "mmproj"), preferring an f16 projector when several are present. Null if none.</summary>
    private static string FindMmproj(string textPath)
    {
        string dir = Path.GetDirectoryName(textPath);
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }
        string best = null;
        foreach (string f in Directory.EnumerateFiles(dir, "*.gguf"))
        {
            string name = Path.GetFileName(f);
            if (!name.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (best is null || name.Contains("f16", StringComparison.OrdinalIgnoreCase))
            {
                best = f;
            }
        }
        return best;
    }

    /// <summary>True if the mmproj is a Qwen2/Qwen2.5-VL merger — detected via the <c>clip.projector_type</c>
    /// metadata (robust to arbitrary file names), falling back to the filename. Mirrors the engine's VlmRunner.</summary>
    private static bool IsQwen25Vl(string mmprojPath)
    {
        try
        {
            using HartsyInference.ModelHandler.Gguf.GgufLoader probe = new();
            probe.Load(mmprojPath);
            string proj = (probe.Metadata.GetString("clip.projector_type") ?? "").ToLowerInvariant();
            if (proj.Contains("qwen")) { return true; }
            if (proj.Length > 0) { return false; }
        }
        catch { /* fall through to the filename heuristic */ }
        return Path.GetFileName(mmprojPath).Replace(".", "").Replace("-", "").Contains("qwen2", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Frees the slot's loaded model (keeps its backend/device alive). Caller holds <c>slot.Lock</c>.</summary>
    private static void UnloadSlot(DeviceSlot slot)
    {
        // Full device eviction, not per-weight free — per-weight missed cast/activation/pool memory and
        // leaked VRAM on model swap. Dispose the vision encoders first so their weights are gone before it.
        slot.SpliceVision?.Dispose();
        slot.SpliceVision = null;
        slot.MllamaVision?.Dispose();
        slot.MllamaVision = null;
        slot.VisionPath = null;
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
    private SamplingOptions BuildSampling(ExtendedLLMInput input) => SamplingOptions.Default with
    {
        Temperature = (float)Math.Max(0, input.Temperature),
        TopP = (float)(input.TopP > 0 ? input.TopP : 1.0),
        TopK = Math.Max(0, Settings.TopK),
        MinP = (float)Math.Max(0, Settings.MinP),
        RepetitionPenalty = (float)(Settings.RepetitionPenalty > 0 ? Settings.RepetitionPenalty : 1.0),
        Seed = input.Seed >= 0 ? (ulong)input.Seed : 0,
        Greedy = input.Temperature <= 0
    };

    /// <summary>Sampling for the vision path. VLMs (especially small quantized ones) are calibrated for a gentle
    /// sampler; forcing the text-model's aggressive Top-K (40) makes weak VLMs hallucinate. So we keep the user's
    /// temperature/top-p/seed but drop Top-K and floor the temperature to the engine's VLM-tuned default (0.4).</summary>
    private SamplingOptions BuildVisionSampling(ExtendedLLMInput input) => SamplingOptions.Default with
    {
        Temperature = input.Temperature <= 0 ? 0f : (float)Math.Max(0.4, input.Temperature),
        TopP = (float)(input.TopP > 0 ? input.TopP : 0.9),
        TopK = 0,
        MinP = (float)Math.Max(0, Settings.MinP),
        RepetitionPenalty = (float)(Settings.RepetitionPenalty > 0 ? Settings.RepetitionPenalty : 1.0),
        Seed = input.Seed >= 0 ? (ulong)input.Seed : 1,
        Greedy = input.Temperature <= 0
    };

    /// <summary>Builds the engine's <see cref="GenerationRequest"/> (chat history or plain prompt) with sampling from <see cref="BuildSampling"/>.</summary>
    private GenerationRequest BuildRequest(ExtendedLLMInput input)
    {
        SamplingOptions sampling = BuildSampling(input);
        GenerationRequest request = new()
        {
            MaxTokens = input.MaxTokens > 0 ? input.MaxTokens : 4096,
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

    /// <summary>The most recent user-message image attachment in the request, or null. The VLM generators take a
    /// single image, so the latest one wins.</summary>
    private static LLMMediaAttachment TryGetImage(ExtendedLLMInput input)
    {
        if (input.Messages is null)
        {
            return null;
        }
        for (int i = input.Messages.Count - 1; i >= 0; i--)
        {
            LLMMessage m = input.Messages[i];
            if (m.Role == LLMRoles.User && m.Media is not null)
            {
                foreach (LLMMediaAttachment a in m.Media)
                {
                    if (a is not null && !string.IsNullOrEmpty(a.Data))
                    {
                        return a;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Runs the vision-language generator: resolves the attachment to bytes, decodes + normalizes to the
    /// encoder's pixel tensor, and generates an answer to the user's question. The engine's multimodal path has
    /// no token callback, so the reply arrives as one block (emitted as a single chunk by the caller).</summary>
    private async Task<string> GenerateVision(DeviceSlot slot, ExtendedLLMInput input, LLMMediaAttachment att, CancellationToken ct)
    {
        string reference = att.Type == "base64"
            ? $"data:{(string.IsNullOrEmpty(att.MediaType) ? "image/png" : att.MediaType)};base64,{att.Data}"
            : att.Data;
        ImageInputResolver.ResolvedImage img = await ImageInputResolver.ResolveAsync(reference, ct);
        string question = VisionQuestion(input);
        SamplingOptions sampling = BuildVisionSampling(input);
        int maxTokens = input.MaxTokens > 0 ? input.MaxTokens : 512;
        return await Task.Run(() =>
        {
            if (slot.MllamaVision is not null)
            {
                using Tensor px = BuildPixels(img.Bytes, slot.MllamaVision.ImageSize, MllamaMean, MllamaStd);
                return new MllamaGenerator(slot.Model, slot.MllamaVision, slot.Backend).Generate(px, question, maxTokens, sampling);
            }
            IVlmImageEncoder v = slot.SpliceVision;
            using Tensor pix = BuildPixels(img.Bytes, v.ImageSize, v.ImageMean, v.ImageStd);
            return new MultimodalGenerator(slot.Model, v, slot.Backend).Generate(pix, question, maxTokens, sampling);
        }, ct);
    }

    /// <summary>The question to ask the VLM: the latest user text, or a sensible default when the turn was just an
    /// image with no words.</summary>
    private static string VisionQuestion(ExtendedLLMInput input)
    {
        string q = input.UserMessage;
        if (string.IsNullOrWhiteSpace(q))
        {
            q = input.Messages?.LastOrDefault(m => m.Role == LLMRoles.User)?.Content;
        }
        q = StripImageAnnotations(q);
        return string.IsNullOrWhiteSpace(q) ? "Describe this image in detail." : q;
    }

    /// <summary>Removes the "[Attached image URL: …]" lines the chat layer appends to a user message (for
    /// tool-chaining). Those file paths must never reach the VLM's question — feeding a path into the vision
    /// prompt badly degrades small models (the image is already delivered as pixels, not as a URL).</summary>
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

    /// <summary>Decodes image bytes (PNG/JPEG/WebP/…) to interleaved RGB and normalizes to the encoder's
    /// [1,3,size,size] pixel tensor via the engine preprocessor (which bilinear-resizes to size).</summary>
    private static Tensor BuildPixels(byte[] bytes, int size, float[] mean, float[] std)
    {
        using Image<Rgb24> img = SixLabors.ImageSharp.Image.Load<Rgb24>(bytes);
        int w = img.Width, h = img.Height;
        byte[] rgb = new byte[w * h * 3];
        img.CopyPixelDataTo(rgb);
        return VlmImagePreprocessor.Preprocess(rgb, w, h, size, mean, std);
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
            // Multimodal path: a vision model + an image in this request → run the (non-streaming) VLM generator
            // and emit the whole answer as one chunk. No image (or a plain text model) falls through to text.
            if ((slot.SpliceVision is not null || slot.MllamaVision is not null) && TryGetImage(input) is LLMMediaAttachment imageAtt)
            {
                string answer = await GenerateVision(slot, input, imageAtt, ct);
                onChunk(new JObject() { ["chunk"] = answer });
                if (Settings.AlwaysFreeMemory)
                {
                    UnloadSlot(slot);
                }
                return;
            }
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
            // The engine has no explicit stop-reason API — but a generation that used every token in the
            // budget almost certainly got cut off mid-thought rather than hitting a natural EOS, so treat
            // reaching the cap as truncation and surface it the same way the remote providers do.
            if (acc.Count >= request.MaxTokens)
            {
                onChunk(new JObject() { ["stopReason"] = "length" });
            }
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
        foreach (DeviceSlot slot in Slots.Values)
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
        HashSet<string> loadedPaths = [.. Slots.Values.Select(s => s.LoadedPath).Where(p => p is not null)];
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
                try { size = new FileInfo(file).Length; } catch { }
                LLMModelInfo info = new()
                {
                    Id = id,
                    Name = Path.GetFileNameWithoutExtension(file),
                    Provider = "hartsy-local",
                    BackendId = AbstractBackendData?.ID ?? -1,
                    SizeBytes = size,
                    IsLoaded = loadedPaths.Contains(file),
                    Metadata = { ["device"] = deviceLabel, ["devices"] = devices }
                };
                // Advertise vision capability when a sidecar mmproj sits next to the model (UI can badge it).
                if (FindMmproj(file) is not null)
                {
                    info.Metadata["vision"] = "true";
                }
                models.Add(info);
            }
        }
        return models;
    }

    /// <inheritdoc/>
    public override async Task<bool> FreeMemory(bool systemRam)
    {
        bool had = false;
        foreach (DeviceSlot slot in Slots.Values)
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
