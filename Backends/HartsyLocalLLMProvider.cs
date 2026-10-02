using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FreneticUtilities.FreneticDataSyntax;
using Newtonsoft.Json.Linq;
using SwarmUI.Backends;
using SwarmUI.Core;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using SwarmUI.Utils;
using HartsyInference.Core.MemoryManagement;
using HartsyInference.Engine;
using HartsyInference.Engine.Dispatch;
using HartsyInference.Engine.Requests;
using HartsyInference.Engine.Services;
using HartsyInference.ModelAssets.Gguf;
using HartsyInference.Tools;
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

        [ConfigComment("How hard the engine should work to fit models in VRAM.\n\n'Auto' (default) reads the card's size for a starting posture, then measures free VRAM. Right for almost everyone.\n\n'Performance' keeps the model loaded and never auto-evicts — fastest for repeated chats, but leaves the card occupied.\n\n'Balanced' and 'Aggressive' progressively free more between requests, for sharing a card with image generation.\n\n'Maximum' also turns on quantized compute (the same lever as the separate setting below) and frees the model after every request.")]
        [ManualSettingsOptions(Impl = null, Vals = ["Auto", "Performance", "Balanced", "Aggressive", "Maximum"],
            ManualNames = ["Auto (recommended)", "Performance (stay loaded)", "Balanced", "Aggressive", "Maximum (free after every request)"])]
        public string VramMode = "Auto";

        [ConfigComment("Keep quantized weights compressed on-device (lower VRAM, slower decode) instead of caching dequantized F16 weights.\n\nNOT the same thing as VRAM Mode above, despite both being about VRAM: this changes HOW a quantized weight is multiplied (compressed with a transient dequant per call, vs. one cached F16 copy), while VRAM Mode decides what stays loaded and when. They compose — 'Maximum' turns this on for you.\nOnly does anything for a quantized (GGUF) checkpoint; on an unquantized one it is inert, and the lever that helps there is splitting layers across GPUs.")]
        public bool LowVramQuant = false;

        [ConfigComment("Repetition penalty on already-generated tokens (1.0 = off).\nSmall models (eg 0.5B) loop/repeat without this — ~1.1 is a good default. Ignored at temperature 0 (greedy).")]
        public double RepetitionPenalty = 1.1;

        [ConfigComment("Top-K sampling: keep only the K highest-probability tokens each step (0 = off).\n~40 is a sane default that curbs small-model gibberish.")]
        public int TopK = 40;

        [ConfigComment("Min-P sampling: drop tokens below this fraction of the top token's probability (0 = off).")]
        public double MinP = 0.0;

        [ConfigComment("If enabled, the model is unloaded immediately after each generation completes.\nIf false, it stays resident for faster subsequent requests.")]
        public bool AlwaysFreeMemory = false;

        [ConfigComment("Free host RAM, in GB, below which loading a new local model first unloads the currently resident one.\n\nUnlike AudioLab's equivalent setting, this is enforced entirely by the extension itself: the LLM engine has no built-in host-RAM eviction of its own to delegate to. 0 disables the check (default), matching today's behavior where a resident model stays loaded regardless of free RAM.")]
        public int EvictBelowGb = 0;

        [ConfigComment("Use CUDA-graph decode when eligible (plain dense Llama/Qwen/Mistral-shape models, CUDA backend only).\nRemoves per-token kernel-launch overhead for faster decode, but only kicks in when the request ends up greedy\n(Temperature = 0) — normal temperature sampling on the chat UI still uses the regular decode path for now.")]
        public bool GraphDecode = false;

        [ConfigComment("Use prompt-lookup speculative decoding when eligible (no draft model — drafts from repeated n-grams in the\nprompt/response so far). Same eligibility as GraphDecode (greedy / Temperature = 0 only) plus JSON mode must be off.\nBiggest win on repetitive output (regenerating similar structure, quoting earlier text); costs nothing extra otherwise.")]
        public bool SpeculativeDecode = false;

        [ConfigComment("When tools are enabled for a chat, grammar-mask the JSON body of a <tool_call>{...}</tool_call> block so it's\nalways syntactically valid — plain chat text stays completely unconstrained, only the JSON between the tags is\nguaranteed-valid (same technique every major chat API uses: constrain only the tool-call span, never the whole\nreply). Off by default until verified against real models — see docs.")]
        public bool StructuredToolCalling = false;

        [ConfigComment("Keep each conversation's prompt prefix (system prompt, tool definitions, the history so far) cached on the device between turns, so a chat thread or voice session only processes its new tokens from the second turn on — a much shorter wait for the first word on a long conversation. Replies are equivalent (byte-identical on CPU; on a GPU the reused cache can round slightly differently).\n\nThe engine bounds what this holds. After each turn a conversation's cache shrinks to its length plus vram.prefixCacheHeadroomTokens (256 tokens), about 0.34 GiB for a 1,000-token Qwen3-4B chat, and grows again by an on-device copy when the next turn needs room. A conversation that would need more than vram.prefixCacheMaxBytes (1.5 GiB) is not kept, so a very long thread runs uncached. Each GPU keeps at most vram.prefixCacheMaxEntries (4) conversations within that same budget, least recently used first. Free Memory (including Swarm's idle VRAM clear), switching models or editing this backend releases them.\n\nHas no effect while 'Always Free Memory' is on, or with VRAM Mode 'Aggressive' or 'Maximum'.")]
        public bool ReuseConversationPrefix = true;
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

    /// <summary>True exactly when <see cref="HartsyLocalLLMProviderSettings.StructuredToolCalling"/> is on —
    /// provider-wide, not aware of which model a particular request names. <b>Do not use this alone to decide
    /// whether to skip the tag-based tool prompt for a request</b>: this backend serves whatever GGUF
    /// <c>ExtendedLLMInput.Model</c> names per call, <see cref="OnProviderInit"/> installs exactly one format
    /// (Hermes/Qwen) for the whole engine instance, and the engine exposes no per-<see cref="ModelSpec"/> way
    /// to ask whether a checkpoint's own chat template renders tools in that format without loading it
    /// (<c>GgufLanguageModel.BuildTemplate</c> is internal to <c>HartsyInference.LLM</c>). Callers that need a
    /// correct per-request answer use <see cref="SupportsNativeToolCallingFor"/> instead — this property only
    /// exists to satisfy <see cref="ILLMProvider.SupportsNativeToolCalling"/> for a caller with no request to
    /// check against. The default stays false, so no existing installation's behavior changes.</summary>
    public bool SupportsNativeToolCalling => Settings.StructuredToolCalling;

    /// <summary>Whether native tool calling actually works for a request naming <paramref name="modelId"/>
    /// right now: <see cref="HartsyLocalLLMProviderSettings.StructuredToolCalling"/> is on, <b>and</b> the
    /// resolved checkpoint's own <c>tokenizer.chat_template</c> actually instructs the Hermes JSON
    /// <c>&lt;tool_call&gt;</c> convention the engine's installed filter parses (<see cref="OnProviderInit"/>
    /// installs only that one format, for the whole engine instance, not per-model — there is currently no
    /// per-request hook to vary it: <c>EngineOptions.TextStreamFilterFactory</c> is a
    /// <c>Func&lt;TextRequest,…&gt;</c>, and <c>TextRequest</c> carries no model id, only the separate
    /// <see cref="ModelSpec"/> argument alongside it).
    ///
    /// <para>An earlier version of this checked the model's <i>filename</i> against a family allow-list
    /// (Qwen/Hermes/GLM/DeepSeek). Independent review found a real false positive it: DeepSeek's R1
    /// distillations (both the Qwen- and the Llama-architecture base) overwrite the base model's chat
    /// template with DeepSeek's own <c>&lt;｜tool▁calls▁begin｜&gt;…</c> delimiter scheme, which has no
    /// <c>&lt;tool_call&gt;</c> tag and never references <c>tools</c> at all — "deepseek" (or "qwen", for the
    /// Qwen-base variant) in the filename said nothing true about the template actually loaded. Reading the
    /// template itself removes the whole class of name-vs-template mismatch, not just that one case: Qwen3.5
    /// and Qwen3-Coder-style checkpoints keep a literal <c>&lt;tool_call&gt;</c> tag but switch the body to an
    /// XML <c>&lt;function=…&gt;</c>/<c>&lt;parameter=…&gt;</c> form (verified against the real Qwen3.5-0.8B
    /// GGUF below), and GLM-4.5 keeps the tag but switches to XML <c>&lt;arg_key&gt;</c>/<c>&lt;arg_value&gt;</c>
    /// pairs (verified against its published <c>chat_template.jinja</c>) — "qwen"/"glm" in the filename would
    /// have said yes to both, and the installed Hermes-JSON filter can parse neither.</para>
    ///
    /// <para>For every family whose template doesn't instruct the Hermes JSON shape (Llama-3.2, Mistral,
    /// Gemma, DeepSeek's R1 distillations, GLM's description-only and XML-argument templates, Qwen3.5's
    /// XML-argument template, a model GGUF with no <c>tokenizer.chat_template</c> key, or one <see cref="ResolvePath"/>
    /// can't find on disk at all), this returns false so the caller falls back to the tag-prompt convention
    /// that worked before <c>StructuredToolCalling</c> existed — the model's own template would otherwise
    /// render tools in ITS native, non-Hermes convention (via <see cref="BuildRequestAsync"/>'s
    /// <see cref="TextRequest.Tools"/>), which the installed Hermes-only filter cannot parse, silently losing
    /// every call. <see cref="BuildRequestAsync"/>, <see cref="WebAPI.ChatEndpoints.ApplyToolsToInput"/> and
    /// <see cref="WebAPI.ChatEndpoints.LLMAssistantVoiceTurnWS"/> all gate on this, not the provider-wide
    /// property above.</para></summary>
    public bool SupportsNativeToolCallingFor(string modelId)
    {
        if (!Settings.StructuredToolCalling)
        {
            return false;
        }
        string path = ResolvePath(modelId);
        return path is not null && InstructsHermesJsonToolCallsFromFile(path);
    }

    /// <summary>A short, human-readable reason <see cref="SupportsNativeToolCallingFor"/> returned false for
    /// <paramref name="modelId"/> — for a caller that wants to tell a user why tools got dropped from a turn
    /// (<see cref="WebAPI.ChatEndpoints.LLMAssistantVoiceTurnWS"/>'s <c>notice</c> frame) rather than silently
    /// falling back. Callers check <see cref="SupportsNativeToolCallingFor"/> first; calling this when it
    /// returned true just describes the <see cref="HartsyLocalLLMProviderSettings.StructuredToolCalling"/>-off
    /// case, which is misleading but harmless (nothing calls it in that order today).</summary>
    public string DescribeNativeToolCallingUnavailability(string modelId)
    {
        if (!Settings.StructuredToolCalling)
        {
            return "Structured Tool Calling is turned off (Server > Backends)";
        }
        // Distinct from the "wrong template" case below: a model id that doesn't resolve to a file at all
        // would otherwise get the misleading "chat template doesn't instruct..." reason here, immediately
        // followed by ResolveSpecAndRequestAsync's own "model not found" error frame once the turn actually
        // tries to run — two different reasons for the same turn, only one of them true.
        return ResolvePath(modelId) is null
            ? $"'{modelId}' could not be resolved to a model file"
            : $"'{modelId}'s chat template doesn't instruct Hermes/Qwen-style JSON tool calls";
    }

    /// <summary>Per-(path, length, last-write-time) cache of <see cref="InstructsHermesJsonToolCallsFromFile"/>'s
    /// verdict, so a hot provider doesn't re-mmap and re-scan the same GGUF header on every request. The key
    /// doubles as a cheap staleness check: replacing the file on disk (a re-download, a re-quant) changes its
    /// length and/or write time, which misses the cache and re-reads rather than trusting a stale verdict.</summary>
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime LastWriteUtc), bool> _hermesTemplateCache = new();

    /// <summary>Reads <paramref name="ggufPath"/>'s <c>tokenizer.chat_template</c> metadata key — metadata
    /// only; <see cref="GgufLoader.Load"/> memory-maps and parses the header/tensor directory, never tensor
    /// data — and classifies it with <see cref="InstructsHermesJsonToolCalls(string)"/>. Any failure (file
    /// missing, not a GGUF, no such key, a malformed header) is a safe false, logged once at Debug rather than
    /// thrown: a provider that can't confirm native support falls back to the tag prompt, which is exactly
    /// the behavior a model this can't even read should get.</summary>
    internal static bool InstructsHermesJsonToolCallsFromFile(string ggufPath)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(ggufPath);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[LLMAssistant] Could not stat '{ggufPath}' for the chat-template cache: {ex.Message}");
            return false;
        }
        if (!info.Exists)
        {
            return false;
        }
        (string Path, long Length, DateTime LastWriteUtc) key = (ggufPath, info.Length, info.LastWriteTimeUtc);
        if (_hermesTemplateCache.TryGetValue(key, out bool cached))
        {
            return cached;
        }
        bool verdict;
        try
        {
            using GgufLoader loader = new();
            loader.Load(ggufPath);
            verdict = InstructsHermesJsonToolCalls(loader.Metadata.GetString("tokenizer.chat_template"));
        }
        catch (Exception ex)
        {
            Logs.Debug($"[LLMAssistant] Could not read '{ggufPath}'s chat template: {ex.Message}");
            verdict = false;
        }
        _hermesTemplateCache[key] = verdict;
        return verdict;
    }

    /// <summary>The actual classification, over the template text directly — pulled out from
    /// <see cref="InstructsHermesJsonToolCallsFromFile"/> so it is unit-testable against fixture strings with
    /// no file I/O. True only when the template both (a) references the <c>tools</c> variable Jinja tool
    /// definitions are passed in as (a bare word match, not a substring of some other identifier — eg
    /// <c>tool_calls</c>/<c>custom_tools</c> don't count on their own unless <c>tools</c> itself also appears,
    /// which it does for every real template seen so far that uses either), and (b) instructs the exact
    /// Qwen2.5/Qwen3 Hermes JSON shape: a literal <c>&lt;tool_call&gt;</c> tag <i>and</i> a JSON object with
    /// <c>"name"</c> and <c>"arguments"</c> keys — checked as three independent literal substrings rather than
    /// one combined pattern, because Jinja interpolation splits the JSON object's literal pieces apart (eg
    /// Qwen3's real template emits <c>'{"name": "'</c>, then the tool-call name, then <c>'", "arguments": '</c>
    /// as separate template fragments), so no single regex matches the rendered-looking text contiguously in
    /// the template's own source. <paramref name="chatTemplate"/> is normalized with <c>\"</c> → <c>"</c>
    /// first, in case the metadata value reached here still carries the escaping its own GGUF/JSON storage
    /// used — the three substrings above are checked against the literal, unescaped characters.
    ///
    /// <para>Verified against real templates (see <c>Tests/HermesTemplateDetectionTests.cs</c> for the
    /// fixtures and their sources): true for Qwen3-4B and Qwen2.5-1.5B-Instruct. False for Qwen3.5-0.8B (a
    /// literal <c>&lt;tool_call&gt;</c> tag, but <c>&lt;function=…&gt;</c>/<c>&lt;parameter=…&gt;</c> XML
    /// arguments inside it, never <c>"name"</c>/<c>"arguments"</c> as JSON keys — Qwen3-Coder's convention, not
    /// Qwen3's own); DeepSeek-R1-Distill-Qwen-1.5B and DeepSeek-R1-Distill-Llama-8B (DeepSeek's own
    /// <c>&lt;｜tool▁calls▁begin｜&gt;</c> delimiters, no <c>tools</c> reference and no <c>&lt;tool_call&gt;</c>
    /// tag at all); GLM-4-9B-0414 (references <c>tools</c>, but describes each one as raw JSON schema in prose
    /// with no <c>&lt;tool_call&gt;</c> tag); GLM-4.5 (a literal <c>&lt;tool_call&gt;</c> tag, but
    /// <c>&lt;arg_key&gt;</c>/<c>&lt;arg_value&gt;</c> XML pairs inside it); Llama-3.2-1B-Instruct (references
    /// <c>tools</c>, instructs a bare <c>{"name": ..., "parameters": ...}</c> object — note <c>"parameters"</c>,
    /// not <c>"arguments"</c> — with no tag at all); Mistral-7B-Instruct-v0.3 and Phi-3-mini (no tool-calling
    /// instructions whatsoever); gemma-4-E2B-it (references <c>tools</c>, but its tag is
    /// <c>&lt;|tool_call&gt;</c> — a leading pipe, which is not the literal substring <c>&lt;tool_call&gt;</c>).</para></summary>
    internal static bool InstructsHermesJsonToolCalls(string chatTemplate)
    {
        if (string.IsNullOrWhiteSpace(chatTemplate))
        {
            return false;
        }
        string normalized = chatTemplate.Replace("\\\"", "\"");
        bool referencesTools = Regex.IsMatch(normalized, @"\btools\b");
        bool instructsHermesJson = normalized.Contains("<tool_call>", StringComparison.Ordinal)
            && normalized.Contains("\"name\"", StringComparison.Ordinal)
            && normalized.Contains("\"arguments\"", StringComparison.Ordinal);
        return referencesTools && instructsHermesJson;
    }

    /// <inheritdoc/>
    protected override Task OnProviderInit()
    {
        EngineOptions options = new() { VramPolicy = ParseVramMode(Settings.VramMode) };
        // Installs the Tools package's stream filter: it only ever does anything for a request that sets
        // TextRequest.Tools (ToolCalling.CreateFilter returns null otherwise), so this is safe to install
        // unconditionally — every existing request without Tools set keeps its exact current code path.
        // Hermes (the default format) is Qwen's <tool_call>{...}</tool_call> convention. It is the ONLY format
        // installed for this whole engine instance (see SupportsNativeToolCallingFor for why a non-Hermes
        // model never reaches this filter with Tools set at all, rather than reaching it and failing to parse).
        ToolCalling.Install(options);
        // The policy reaches the text slots because TextService applies the engine's policy to the backends it
        // builds per device key — without that it would only govern this shell engine's own unused backend.
        Engine = new InferenceEngine(
            string.Equals(Settings.Device, "cpu", StringComparison.OrdinalIgnoreCase) ? "cpu" : "cuda",
            options);
        Status = BackendStatus.RUNNING; // Lazy: load on first request.
        return Task.CompletedTask;
    }

    /// <summary>Maps the VRAM Mode setting to an engine policy; null (Auto or an unrecognized value) leaves the engine on its own default.</summary>
    private static VramPolicy ParseVramMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || mode.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!Enum.TryParse(mode.Trim(), ignoreCase: true, out VramTier tier))
        {
            Logs.Warning($"[LLMAssistant] VRAM mode '{mode}' is not recognized; using Auto. "
                + "Valid: Auto, Performance, Balanced, Aggressive, Maximum.");
            return null;
        }
        return VramPolicy.For(tier);
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

    /// <summary>Resolves the GGUF path, applies the low-memory eviction check, and builds the
    /// <see cref="ModelSpec"/>/<see cref="TextRequest"/> pair every entry point into the engine needs
    /// (<see cref="GenerateLive"/> and <see cref="StreamToolLoopAsync"/>). Throws
    /// <see cref="SwarmReadableErrorException"/> when the model id doesn't resolve, same as before this was
    /// pulled out.</summary>
    private async Task<(ModelSpec Spec, TextRequest Request, string DeviceKey)> ResolveSpecAndRequestAsync(ExtendedLLMInput input, CancellationToken ct)
    {
        string deviceKey = NormalizeDeviceKey(input.Device);
        string path = ResolvePath(input.Model);
        if (path is null)
        {
            throw new SwarmReadableErrorException($"LLM model '{input.Model}' not found in the LLM model folder(s). Drop a .gguf file into Models/llm.");
        }
        await MaybeEvictForLowMemory();
        ModelSpec spec = new() { Requested = input.Model, Modality = Modality.Text, LocalPath = path };
        TextRequest request = await BuildRequestAsync(input, deviceKey, ct);
        return (spec, request, deviceKey);
    }

    /// <inheritdoc/>
    public override async Task GenerateLive(ExtendedLLMInput input, string batchId, Func<JObject, Task> onChunk, CancellationToken ct)
    {
        (ModelSpec spec, TextRequest request, string deviceKey) = await ResolveSpecAndRequestAsync(input, ct);
        await foreach (TextChunk chunk in Engine.Text.StreamAsync(spec, request, ct))
        {
            switch (chunk.Kind)
            {
                case TextChunkKind.Chunk:
                    await onChunk(new JObject() { ["chunk"] = chunk.Text });
                    break;
                case TextChunkKind.NativeToolCall:
                    // Only reachable when SupportsNativeToolCalling is true (StructuredToolCalling on), which
                    // is what makes BuildRequestAsync offer TextRequest.Tools and ToolCalling.Install's filter
                    // active for this request in the first place. Same wire shape AnthropicLLMProvider already
                    // emits, so LLMStreamHelper's agentic loop (ChatEndpoints' general chat path) and the Tools
                    // package's own callers both already know how to read it.
                    if (chunk.ToolCall is { } nativeCall)
                    {
                        await onChunk(new JObject() { ["native_tool_call"] = NativeToolCallJson(nativeCall) });
                    }
                    break;
                case TextChunkKind.StopReason:
                    // Only surface truncation/cancellation/error — a normal finish (Stop) is the common case and
                    // the old provider never emitted a stopReason event for it; matching that keeps the wire
                    // format's "stopReason present" signal meaning "something other than a clean finish happened".
                    if (chunk.Stop is StopReason.Length)
                    {
                        await onChunk(new JObject() { ["stopReason"] = "length" });
                    }
                    else if (chunk.Stop is StopReason.Cancelled)
                    {
                        await onChunk(new JObject() { ["stopReason"] = "cancelled" });
                    }
                    else if (chunk.Stop is StopReason.Error)
                    {
                        // Log it as well as forwarding it. The wire event alone reaches the chat UI, but a
                        // caller that only accumulates text (LLMProviderBackend.Generate, and through it the
                        // one-shot and voice routes) sees nothing but an empty string, and nothing is written
                        // anywhere else — an engine-side failure was indistinguishable from "the model chose
                        // to say nothing", at any log level. Cost an hour on 2026-09-05 to a backend left on
                        // the default Device=cuda that could not actually run.
                        Logs.Warning($"[LLMAssistant] Local LLM '{input.Model}' stopped with an engine error on "
                            + $"device '{deviceKey}' — no text was produced. If this device is not usable, "
                            + "switch the backend's Device setting (cpu/cuda) in Server > Backends.");
                        await onChunk(new JObject() { ["stopReason"] = "error" });
                    }
                    break;
                    // Result duplicates the text already streamed as Chunk events — LLMProviderBackend.Generate's
                    // non-streaming accumulator appends both "chunk" and "result", so forwarding Result here would
                    // double the text.
            }
        }
    }

    /// <summary>Builds the <c>{id, name, arguments}</c> shape <see cref="GenerateLive"/>'s <c>native_tool_call</c>
    /// event and <see cref="AnthropicLLMProvider"/>'s <c>tool_use</c> handling both emit — <c>arguments</c> is a
    /// parsed <see cref="JObject"/>, not the raw JSON string, matching every existing consumer
    /// (<see cref="Hartsy.Extensions.LLMAssistant.LLMs.LLMStreamHelper"/>'s <c>native_tool_call</c> branch reads
    /// it as one). A call that (contrary to its contract) carries malformed arguments JSON fails soft into an
    /// empty object rather than losing the whole chunk.</summary>
    internal static JObject NativeToolCallJson(NativeToolCall call)
    {
        JObject args;
        try
        {
            args = string.IsNullOrWhiteSpace(call.Arguments) ? new JObject() : JObject.Parse(call.Arguments);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[LLMAssistant] Malformed native tool-call arguments JSON for '{call.Name}': {ex.Message}");
            args = new JObject();
        }
        return new JObject { ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = args };
    }

    /// <summary>Streams one user turn through the Tools package's agent loop
    /// (<see cref="ToolLoop.RunAsync"/>): the model's own chat template renders <paramref name="registry"/>'s
    /// tools (or <see cref="TextRequest.Tools"/> when <paramref name="input"/> already set some — see
    /// <see cref="BuildRequestAsync"/>), a completed call dispatches through <paramref name="registry"/>, and
    /// the model is asked again with the result appended as a <see cref="TextRole.Tool"/> message, up to
    /// <paramref name="maxRounds"/> model invocations. Chunk kinds match <see cref="ToolLoop"/>'s own contract
    /// exactly (<see cref="TextChunkKind.Chunk"/>, <see cref="TextChunkKind.NativeToolCall"/>,
    /// <see cref="TextChunkKind.Status"/> tool-result/round-limit chunks, one final
    /// <see cref="TextChunkKind.Result"/> then <see cref="TextChunkKind.StopReason"/>) — callers translate those
    /// the same way <see cref="GenerateLive"/> translates <see cref="Engine"/>'s plain stream, they are just a
    /// layer further from the wire than <c>GenerateLive</c>'s <c>JObject</c> shape. Requires
    /// <see cref="SupportsNativeToolCalling"/>; callers check that first — this does not, so a caller that
    /// skips the check gets whatever the engine does with <see cref="HartsyLocalLLMProviderSettings.StructuredToolCalling"/>
    /// off (no <see cref="TextRequest.Tools"/>, so the loop ends after one round with no calls).</summary>
    public async IAsyncEnumerable<TextChunk> StreamToolLoopAsync(ExtendedLLMInput input, ToolRegistry registry,
        int maxRounds = ToolLoop.DefaultMaxRounds, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        (ModelSpec spec, TextRequest request, _) = await ResolveSpecAndRequestAsync(input, ct);
        await foreach (TextChunk chunk in ToolLoop.RunAsync(Engine.Text, spec, request, registry, maxRounds, ct).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>Builds the engine's native request from the extension's input: messages/roles, sampling knobs
    /// from settings plus per-request overrides, the vision attachment (decoded only for the final user turn —
    /// the engine's vision path only ever looks at the latest one), tool definitions when structured tool
    /// calling is enabled, and the conversation's prefix-cache key (see <see cref="BuildRequestCore"/>).</summary>
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
            messages.Add(ToTextMessage(m, content, images));
        }
        List<ToolDefinition> tools = null;
        // Gated on SupportsNativeToolCallingFor, not just the StructuredToolCalling setting: for a non-Hermes
        // model this must stay null even with the setting on, or the model's own Jinja template renders tools
        // in ITS native (non-Hermes) convention and the engine's Hermes-only installed filter (OnProviderInit)
        // activates on this request (it keys only on Tools being set) but can never parse what streams out —
        // every call silently lost, with no tag-prompt fallback either (ApplyToolsToInput skips that too once
        // it sees Tools already on the request). Gating here keeps that filter inert for this request instead.
        if (SupportsNativeToolCallingFor(input.Model) && input.Tools is { Count: > 0 })
        {
            tools = [.. input.Tools.Select(t => new ToolDefinition
            {
                Name = t["name"]?.ToString() ?? "",
                Description = t["description"]?.ToString() ?? "",
                JsonSchema = (t["parameters"] as JObject ?? new JObject()).ToString()
            })];
        }
        // The user comes from the server-side session, never from the request body.
        return BuildRequestCore(input, messages, tools, deviceKey, Settings, input.RequestSession?.User?.UserID);
    }

    /// <summary>The pure tail of <see cref="BuildRequestAsync"/>: turns the already-resolved messages/tools
    /// plus <paramref name="input"/>'s per-request overrides and <paramref name="settings"/>'s provider-wide
    /// defaults into the engine's native <see cref="TextRequest"/>. Pulled out so every scalar mapping
    /// (<see cref="TextRequest.EnableThinking"/> included) is unit-testable against a freshly constructed
    /// <see cref="HartsyLocalLLMProviderSettings"/> — the rest of <see cref="BuildRequestAsync"/> needs a live
    /// host (<see cref="SupportsNativeToolCallingFor"/>'s <see cref="ResolvePath"/> call reads
    /// <c>Program.ServerSettings</c>; the image-decode loop is async I/O), this does neither.
    ///
    /// <para><b>Prefix-KV reuse.</b> A request continuing a conversation (<see cref="ExtendedLLMInput.ConversationId"/>
    /// set by its route) for a known <paramref name="userId"/> carries <see cref="TextRequest.PrefixCacheKey"/>
    /// (<see cref="PrefixCacheKeyFor(string, string, string)"/>): the engine keeps that conversation's KV cache
    /// between calls and prefills only the tokens past the longest prefix it already holds — equivalent to a fresh
    /// prefill, byte-identical on CPU (on CUDA the reused KV of an earlier reply came from decode steps, not one
    /// batched prefill, so it can round differently). Every round of a tool loop is built from the same input, so
    /// it carries the same key. No <see cref="TextRequest.PrefixCacheCapacityHint"/>: from engine alpha.242 it only
    /// sizes a key's first allocation. The engine shrinks what a request retains to its length plus
    /// <c>vram.prefixCacheHeadroomTokens</c> when the request ends, and grows it by an on-device copy when a later
    /// request needs more room, so anything past this request's own prompt + <c>MaxTokens</c> would be allocated only
    /// to be copied away. The engine's default (exactly that) is also what an uncached request allocates.
    ///
    /// No key when <see cref="HartsyLocalLLMProviderSettings.ReuseConversationPrefix"/> is off, or when
    /// <see cref="HartsyLocalLLMProviderSettings.AlwaysFreeMemory"/> is on, which unloads the slot (and
    /// the engine's prefix store with it) after every request, so a key could only make each request allocate more
    /// KV than it needs; or when <see cref="HartsyLocalLLMProviderSettings.VramMode"/> is Aggressive or Maximum
    /// (<see cref="VramModeHoldsLeastBetweenRequests"/>), which the user picked to hold the least VRAM between
    /// requests — a retained entry is live memory that a same-process pipeline's OOM recovery or a pool trim
    /// cannot reclaim, unlike a finished request's KV.
    /// <see cref="TextRequest.CacheWeightCasts"/>/<see cref="TextRequest.PreloadRedundantWeightSplits"/> stay at
    /// the backend default: nothing here measured a reason to move them.</para></summary>
    internal static TextRequest BuildRequestCore(ExtendedLLMInput input, List<TextMessage> messages,
        List<ToolDefinition> tools, string deviceKey, HartsyLocalLLMProviderSettings settings, string userId = null)
    {
        string prefixCacheKey = settings.ReuseConversationPrefix && !settings.AlwaysFreeMemory
            && !VramModeHoldsLeastBetweenRequests(settings.VramMode)
            ? PrefixCacheKeyFor(userId, input.ConversationId, input.Model)
            : null;
        return new()
        {
            Messages = messages,
            // Not SystemPrompt too: ExtendedLLMInput always folds the system prompt into Messages[0] (and
            // keeps it in sync — see ApplyToolsToInput and ExtendedLLMInput.CreateFromMessages), so setting both
            // here double-injects it into the chat template. Confirmed live: this produced garbage/off-topic
            // output from real GGUF vision models under the WS chat path (verified 2026-07-25 testing against
            // llava-v1.5-7b/Qwen2.5-VL-7B).
            Temperature = Math.Max(0, input.Temperature),
            TopP = input.TopP > 0 ? input.TopP : 1.0,
            TopK = settings.TopK > 0 ? settings.TopK : null,
            MinP = settings.MinP > 0 ? settings.MinP : null,
            RepetitionPenalty = settings.RepetitionPenalty > 0 ? settings.RepetitionPenalty : null,
            MaxTokens = input.MaxTokens > 0 ? input.MaxTokens : 4096,
            Seed = input.Seed,
            Greedy = input.Temperature <= 0,
            // Voice callers send false (Qwen3 thinking adds hundreds of tokens before the first spoken word); null
            // (every caller before this field existed, and still every caller except the new messages/enableThinking
            // WS request fields) leaves the template's own default alone -- unchanged behavior.
            EnableThinking = input.EnableThinking,
            Device = deviceKey,
            Tools = tools,
            GraphDecode = settings.GraphDecode ? true : null,
            SpeculativeDecode = settings.SpeculativeDecode ? true : null,
            LowVramQuant = settings.LowVramQuant ? "true" : null,
            AlwaysFreeMemory = settings.AlwaysFreeMemory,
            PrefixCacheKey = prefixCacheKey
        };
    }

    /// <summary>Whether the VRAM Mode setting is Aggressive or Maximum, the two tiers a user picks to hold the least
    /// VRAM between requests — <see cref="BuildRequestCore"/> keeps no conversation's KV cache between turns under
    /// either. Parsed exactly as <see cref="ParseVramMode"/> parses it, so a blank or unrecognized value (Auto
    /// there) keeps reuse available.</summary>
    internal static bool VramModeHoldsLeastBetweenRequests(string vramMode)
        => !string.IsNullOrWhiteSpace(vramMode) && Enum.TryParse(vramMode.Trim(), ignoreCase: true, out VramTier tier)
            && tier is VramTier.Aggressive or VramTier.Maximum;

    /// <summary>The engine prefix-cache key for one user's conversation on one model, or null unless all three are
    /// present, so a request with no conversation (or no user) never touches the engine's store. The user id comes
    /// from the server-side session, so a client choosing its own conversation id can only ever reach its own
    /// user's entries. Hashed (SHA-256, first 128 bits) rather than concatenated, so no raw id — on the voice
    /// routes that includes a SwarmUI session id, which is a credential — sits in the engine's key store or in
    /// anything that logs it; each part is length-prefixed first, so no choice of ids makes two different triples
    /// hash the same input. The model id is case-folded, matching <see cref="ResolvePath"/>'s own comparison.</summary>
    internal static string PrefixCacheKeyFor(string userId, string conversationId, string model)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }
        string modelKey = model.Trim().ToLowerInvariant();
        string material = $"{userId.Length}:{userId}|{conversationId.Length}:{conversationId}|{modelKey.Length}:{modelKey}";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "llmassistant:" + Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
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

    /// <summary>Maps the extension's role string onto the engine's native <see cref="TextRole"/>. A Tool-role
    /// message used to fall through to <see cref="TextRole.User"/> like any other unrecognized string — harmless
    /// while nothing ever constructed one, but wrong the moment something does (eg a native-tool-calling turn
    /// replaying history that already contains a <see cref="LLMRoles.Tool"/> message): the engine's chat
    /// templates render a <c>tool</c> turn differently (Qwen's <c>&lt;tool_response&gt;</c>, the Jinja
    /// OpenAI-shaped <c>tool_call_id</c>/<c>name</c> fields), and a model expecting that shape sees a plain user
    /// turn instead.</summary>
    internal static TextRole RoleFor(string role) => role switch
    {
        LLMRoles.System => TextRole.System,
        LLMRoles.Assistant => TextRole.Assistant,
        LLMRoles.Tool => TextRole.Tool,
        _ => TextRole.User
    };

    /// <summary>Builds one engine <see cref="TextMessage"/> from an extension <see cref="LLMMessage"/>, including
    /// the id/name a <see cref="LLMRoles.Tool"/> turn carries. Pulled out of <see cref="BuildRequestAsync"/> so the
    /// role/id mapping is unit-testable without constructing a provider (which needs a live <c>SettingsRaw</c>).</summary>
    internal static TextMessage ToTextMessage(LLMMessage m, string content, List<ImageData> images)
    {
        bool isTool = m.Role == LLMRoles.Tool;
        // TextMessage's ToolCallId/Name/ToolCalls are init-only, so they're set in the same initializer rather
        // than assigned after construction.
        return new TextMessage
        {
            Role = RoleFor(m.Role),
            Content = content,
            Images = images is { Count: > 0 } ? images : null,
            ToolCallId = isTool ? m.ToolCallId : null,
            Name = isTool ? m.Name : null,
            ToolCalls = m.Role == LLMRoles.Assistant && m.ToolCalls is { Count: > 0 }
                ? [.. m.ToolCalls.Select(ToNativeToolCall)]
                : null
        };
    }

    /// <summary>Maps one <c>{id, name, arguments}</c> wire-shaped tool call — the exact shape
    /// <see cref="NativeToolCallJson"/> emits on a <c>native_tool_call</c> frame, and so what a client replaying
    /// conversation history is expected to echo back in a <see cref="LLMMessage.ToolCalls"/> entry — onto the
    /// engine's <see cref="NativeToolCall"/>. <c>arguments</c> is accepted either as a parsed <see cref="JObject"/>
    /// (what a client gets from the earlier frame, and what it should normally send back) or as an already-
    /// serialized JSON string (since <see cref="NativeToolCall.Arguments"/> is a string either way, a client
    /// that re-serializes it costs nothing to also accept); anything else fails soft to <c>"{}"</c>, the same
    /// policy <see cref="NativeToolCallJson"/> uses for the inverse direction.</summary>
    internal static NativeToolCall ToNativeToolCall(JObject call) => new()
    {
        Id = call["id"]?.ToString() ?? "",
        Name = call["name"]?.ToString() ?? "",
        Arguments = call["arguments"] switch
        {
            JObject obj => obj.ToString(Newtonsoft.Json.Formatting.None),
            JValue { Type: JTokenType.String } str => str.ToString(),
            _ => "{}"
        }
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
                try { size = (new FileInfo(file).ResolveLinkTarget(true) as FileInfo)?.Length ?? new FileInfo(file).Length; }
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

    /// <summary>Unloads the resident model before a new load if free host RAM is below <see cref="HartsyLocalLLMProviderSettings.EvictBelowGb"/>.
    /// The engine has no host-RAM-aware eviction of its own for text models (unlike AudioLab's audio models), so this
    /// extension enforces it directly using the same host memory reader SwarmUI's own admin status page uses
    /// (<see cref="SystemStatusMonitor.HardwareInfo"/>). A no-op while the setting is left at its default (0).</summary>
    private async Task MaybeEvictForLowMemory()
    {
        int evictBelowGb = Settings.EvictBelowGb;
        if (evictBelowGb <= 0)
        {
            return;
        }
        ulong? availableBytes = SystemStatusMonitor.HardwareInfo?.MemoryStatus?.AvailablePhysical;
        if (availableBytes is null)
        {
            return;
        }
        ulong thresholdBytes = (ulong)evictBelowGb * 1024 * 1024 * 1024;
        if (availableBytes.Value < thresholdBytes)
        {
            Logs.Info($"[LLMAssistant] Free host RAM ({availableBytes.Value / 1024 / 1024 / 1024}GB) is below EvictBelowGb ({evictBelowGb}GB), unloading the resident local LLM before loading a new one.");
            await FreeMemory(true);
        }
    }
}
