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
using HartsyInference.Core.Configuration;
using HartsyInference.Core.MemoryManagement;
using HartsyInference.Engine;
using HartsyInference.Engine.Dispatch;
using HartsyInference.Engine.Placement;
using HartsyInference.Engine.Registry;
using HartsyInference.Engine.Requests;
using HartsyInference.LLM.Looped;
using HartsyInference.Engine.Services;
using HartsyInference.ModelAssets.Gguf;
using HartsyInference.Tools;
using HartsyInference.Tools.Parsing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hartsy.Extensions.LLMAssistant.Backends;

/// <summary>Thin local LLM backend: maps the extension's chat input onto HartsyInference.Engine's native
/// <see cref="ITextService"/> contract (<see cref="TextRequest"/>/<see cref="TextChunk"/>) and lets the engine
/// own model loading, device slots, sampling, chat templating, and the vision path. No GGUF/tokenizer/pipeline
/// code lives here; see HartsyInference's <c>TextService</c> for that orchestration.</summary>
public class HartsyLocalLLMProvider : LLMProviderBackend
{
    /// <summary>Settings saved to Data/Backends.fds by field name, so renaming or retyping a field breaks every saved backend.</summary>
    /// <remarks>Assets/llm-backend-cards.js groups these fields into the card's sections.</remarks>
    public class HartsyLocalLLMProviderSettings : AutoConfiguration
    {
        [ConfigComment("Which hardware runs the model. Auto picks the best one this machine supports.\nVulkan runs AMD, Intel and older NVIDIA cards. Only choices that work here are listed. Restart SwarmUI after changing a GPU or driver.")]
        [SettingsOptions(Impl = typeof(LocalDeviceChoices.DeviceOptions))]
        public string Device = "auto";

        [ConfigComment("Which GPU runs the model. Not used on CPU.\nTo use more than one GPU, add one backend per GPU.")]
        [SettingsOptions(Impl = typeof(LocalDeviceChoices.GpuOptions))]
        public int GPUDeviceId = 0;

        [ConfigComment("How hard the engine works to fit models into GPU memory. Auto is right for almost everyone.\nPerformance keeps the model loaded. Balanced, Aggressive and Maximum free more memory between replies, to share the GPU with image generation.")]
        [ManualSettingsOptions(Impl = null, Vals = ["Auto", "Performance", "Balanced", "Aggressive", "Maximum"],
            ManualNames = ["Auto (recommended)", "Performance (stay loaded)", "Balanced", "Aggressive", "Maximum (free after every reply)"])]
        public string VramMode = "Auto";

        [ConfigComment("Where a model goes when it is too big for this GPU. NVIDIA (CUDA) only.\nAuto tries this GPU, then splitting the model across your GPUs, then moving mixture-of-experts layers to the CPU.")]
        [ManualSettingsOptions(Impl = null, Vals = ["auto", "gpu", "split", "offload"],
            ManualNames = ["Auto (recommended)", "GPU only", "Split across GPUs", "Offload experts to CPU"])]
        public string Placement = "auto";

        [ConfigComment("Keeps quantized GGUF weights compressed on the GPU: less VRAM, slower replies. NVIDIA (CUDA) only.\nVRAM Mode Maximum turns this on too.")]
        public bool LowVramQuant = false;

        [ConfigComment("Makes words the model already used less likely, so small models don't loop.\n1.0 turns it off. 1.1 is a good default.")]
        public double RepetitionPenalty = 1.1;

        [ConfigComment("Each step, keeps only the K most likely next words. 0 turns it off.\n40 is a good default.")]
        public int TopK = 40;

        [ConfigComment("Drops unlikely words: any with less than this fraction of the top word's chance. 0 turns it off.\nTry 0.05 if replies wander.")]
        public double MinP = 0.0;

        [ConfigComment("Unloads the model after every reply to free its memory. The next reply loads it again, which is slower.\nVRAM Mode Maximum does this too.")]
        public bool AlwaysFreeMemory = false;

        [ConfigComment("Before each reply, unloads the current model first if free system RAM is below this many GB. 0 turns it off.\nSet it too high and every reply reloads the model.")]
        public int EvictBelowGb = 0;

        [ConfigComment("Faster replies on NVIDIA (CUDA) for common model types. Normal chats need Top K between 1 and 64.\nIf a reply fails with a graph error, turn this off. Vulkan does this by itself.")]
        public bool GraphDecode = false;

        [ConfigComment("Faster replies at Temperature 0 when the reply repeats earlier text, such as code edits or quotes.\nNeeds no extra model.")]
        public bool SpeculativeDecode = false;

        [ConfigComment("When tools are enabled for a chat, grammar-mask the JSON body of a <tool_call>{...}</tool_call> block so it's\nalways syntactically valid — plain chat text stays completely unconstrained, only the JSON between the tags is\nguaranteed-valid (same technique every major chat API uses: constrain only the tool-call span, never the whole\nreply). Off by default until verified against real models — see docs.")]
        public bool StructuredToolCalling = false;

        [ConfigComment("Keeps each chat's earlier messages ready in memory between turns, so long chats start answering sooner.\nNot used while Always Free Memory is on, or with VRAM Mode Aggressive or Maximum.")]
        public bool ReuseConversationPrefix = true;

        [ConfigComment("How many passes a looped model (such as NVIDIA Nemotron Dory) runs when the chat does not set Loops.\nMore passes are slower but more accurate. Other models ignore this.")]
        [SettingsOptions(Impl = typeof(LoopOptions))]
        public int RecurrentLoops = 0;
    }

    /// <summary>The Default Loops dropdown: the model's own default, then 1 up to the most passes a looped model runs.</summary>
    public class LoopOptions : SettingsOptionsAttribute.AbstractImpl
    {
        public override string[] GetOptions => [.. Enumerable.Range(0, LoopedTransformerConfig.DefaultMaxLoops + 1).Select(i => $"{i}")];

        public override string[] Names => LoopNames(LoopedTransformerConfig.DefaultMaxLoops);
    }

    /// <summary>Labels for 0 (the model's default) through <paramref name="max"/> loop passes.</summary>
    internal static string[] LoopNames(int max)
        => ["Model default", .. Enumerable.Range(1, max).Select(i => i == 1 ? "1 (fastest)" : i == max ? $"{i} (most accurate)" : $"{i}")];

    /// <summary>The settings for this backend.</summary>
    public HartsyLocalLLMProviderSettings Settings => SettingsRaw as HartsyLocalLLMProviderSettings;

    /// <summary>The engine instance backing this backend's requests. One per backend instance, disposed on shutdown.</summary>
    private InferenceEngine Engine;

    /// <summary>The kind this backend runs on ("cuda", "vulkan" or "cpu"), resolved from Device at Init. Null before the first Init.</summary>
    private string DeviceKind;

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
    /// installs only that one format, for the whole engine instance, not per-model).
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

    /// <summary>Per-(path, length, last-write-time) cache of <see cref="TemplateTraitsFromFile"/>'s verdicts,
    /// so a hot provider doesn't re-mmap and re-scan the same GGUF header on every request. The key
    /// doubles as a cheap staleness check: replacing the file on disk (a re-download, a re-quant) changes its
    /// length and/or write time, which misses the cache and re-reads rather than trusting a stale verdict.</summary>
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime LastWriteUtc), (bool InstructsHermesJson, bool TakesReasoningContent)> _templateTraitsCache = new();

    /// <summary>Whether <paramref name="ggufPath"/>'s chat template instructs Hermes JSON tool calls (<see cref="InstructsHermesJsonToolCalls(string)"/>).</summary>
    internal static bool InstructsHermesJsonToolCallsFromFile(string ggufPath) => TemplateTraitsFromFile(ggufPath).InstructsHermesJson;

    /// <summary>Whether <paramref name="ggufPath"/>'s chat template takes reasoning only as <c>reasoning_content</c> (<see cref="TakesReasoningContent(string)"/>).</summary>
    internal static bool TakesReasoningContentFromFile(string ggufPath) => TemplateTraitsFromFile(ggufPath).TakesReasoningContent;

    /// <summary>Reads <paramref name="ggufPath"/>'s <c>tokenizer.chat_template</c> metadata key — metadata
    /// only; <see cref="GgufLoader.Load"/> memory-maps and parses the header/tensor directory, never tensor
    /// data, and classifies it with <see cref="InstructsHermesJsonToolCalls(string)"/> and <see cref="TakesReasoningContent(string)"/>. Any failure (file
    /// missing, not a GGUF, no such key, a malformed header) is a safe false, logged once at Debug rather than
    /// thrown: a provider that can't confirm native support falls back to the tag prompt, which is exactly
    /// the behavior a model this can't even read should get.</summary>
    private static (bool InstructsHermesJson, bool TakesReasoningContent) TemplateTraitsFromFile(string ggufPath)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(ggufPath);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[LLMAssistant] Could not stat '{ggufPath}' for the chat-template cache: {ex.Message}");
            return (false, false);
        }
        if (!info.Exists)
        {
            return (false, false);
        }
        (string Path, long Length, DateTime LastWriteUtc) key = (ggufPath, info.Length, info.LastWriteTimeUtc);
        if (_templateTraitsCache.TryGetValue(key, out (bool InstructsHermesJson, bool TakesReasoningContent) cached))
        {
            return cached;
        }
        (bool InstructsHermesJson, bool TakesReasoningContent) verdict;
        try
        {
            using GgufLoader loader = new();
            loader.Load(ggufPath);
            string chatTemplate = loader.Metadata.GetString("tokenizer.chat_template");
            verdict = (InstructsHermesJsonToolCalls(chatTemplate), TakesReasoningContent(chatTemplate));
        }
        catch (Exception ex)
        {
            Logs.Debug($"[LLMAssistant] Could not read '{ggufPath}'s chat template: {ex.Message}");
            verdict = (false, false);
        }
        _templateTraitsCache[key] = verdict;
        return verdict;
    }

    /// <summary>The tags <see cref="ForwardEngineStreamAsync"/> wraps reasoning in, and <see cref="TrySplitThinkBlocks"/> reads back.</summary>
    internal const string ThinkOpen = "<think>";

    /// <summary>Closes a <see cref="ThinkOpen"/> block.</summary>
    internal const string ThinkClose = "</think>";

    /// <summary>Whether a chat template takes an assistant turn's reasoning only as <c>reasoning_content</c>: it reads that field and does
    /// not split <c>&lt;/think&gt;</c> out of the content itself (Muse Spark, Gemma 4, or a template that only wraps the field in its own
    /// tags), so a stored <c>&lt;think&gt;</c> block would reach it as answer text. Qwen3, Qwen3.5 and GLM-4.5 split the content
    /// themselves and DeepSeek R1 never reads the field; sending them the split would change what they render (R1 keeps the newlines
    /// after <c>&lt;/think&gt;</c>, and the Qwen3 family keeps only the text after the last one), so they get the reply as stored. See
    /// <c>Tests/ReasoningHistoryTests.cs</c> for each fixture.</summary>
    internal static bool TakesReasoningContent(string chatTemplate)
    {
        if (string.IsNullOrWhiteSpace(chatTemplate))
        {
            return false;
        }
        string normalized = chatTemplate.Replace("\\\"", "\"");
        // A field access, not the word in a comment or an error string.
        bool readsField = Regex.IsMatch(normalized, @"(?:\.|\[\s*['""]|get\(\s*['""])reasoning_content\b");
        bool splitsContent = Regex.IsMatch(normalized, @"split\(\s*['""]</think>['""]|['""]</think>['""]\s+in\s");
        return readsField && !splitsContent;
    }

    /// <summary>Splits a stored assistant reply into its reasoning, every <see cref="ThinkOpen"/> block joined by a blank line (null when
    /// all are empty), and the rest as the answer, tool-call tags included. A block left open at the end counts as reasoning, since a stop
    /// mid-reasoning can save the reply without its close. Tags match case-insensitively, as the chat UI matches them. False, leaving the
    /// reply as it is, when there is no block or the tags do not pair up: a nested <see cref="ThinkOpen"/> or a stray
    /// <see cref="ThinkClose"/>.</summary>
    internal static bool TrySplitThinkBlocks(string content, out string reasoning, out string answer)
    {
        reasoning = null;
        answer = content;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }
        List<string> thoughts = [];
        StringBuilder rest = new();
        int pos = 0;
        while (pos < content.Length)
        {
            int open = content.IndexOf(ThinkOpen, pos, StringComparison.OrdinalIgnoreCase);
            int strayClose = content.IndexOf(ThinkClose, pos, StringComparison.OrdinalIgnoreCase);
            if (strayClose >= 0 && (open < 0 || strayClose < open))
            {
                return false;
            }
            if (open < 0)
            {
                rest.Append(content, pos, content.Length - pos);
                break;
            }
            rest.Append(content, pos, open - pos);
            int innerStart = open + ThinkOpen.Length;
            int close = content.IndexOf(ThinkClose, innerStart, StringComparison.OrdinalIgnoreCase);
            int nested = content.IndexOf(ThinkOpen, innerStart, StringComparison.OrdinalIgnoreCase);
            if (nested >= 0 && (close < 0 || nested < close))
            {
                return false;
            }
            int innerEnd = close < 0 ? content.Length : close;
            thoughts.Add(content[innerStart..innerEnd].Trim());
            pos = close < 0 ? content.Length : close + ThinkClose.Length;
        }
        if (thoughts.Count == 0)
        {
            return false;
        }
        string joined = string.Join("\n\n", thoughts.Where(t => t.Length > 0));
        reasoning = joined.Length > 0 ? joined : null;
        answer = rest.ToString().Trim();
        return true;
    }

    /// <summary>Whether any assistant reply in <paramref name="messages"/> holds a <see cref="ThinkOpen"/> block, the only case where
    /// <see cref="BuildRequestAsync"/> needs the model's template (<see cref="TakesReasoningContentFromFile"/>) at all.</summary>
    internal static bool HasThinkReply(List<LLMMessage> messages)
        => messages.Any(m => m.Role == LLMRoles.Assistant && m.Content is not null
            && m.Content.Contains(ThinkOpen, StringComparison.OrdinalIgnoreCase));

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
        // Explicit since alpha.375, where a null format means per-request detection; an engine-parsed template (Muse Spark) still gets no filter.
        ToolCalling.Install(options, ToolCallFormat.Hermes);
        // The policy reaches the text slots because TextService applies the engine's policy to the backends it
        // builds per device key; without that it would only govern this shell engine's own unused backend.
        DeviceKind = LocalDeviceChoices.ResolveKind(Settings.Device, Settings.GPUDeviceId);
        if (LocalDeviceChoices.RequestedKind(Settings.Device) == "auto")
        {
            Logs.Info($"[LLMAssistant] Local LLM backend: Device Auto runs on '{DeviceKind}'.");
        }
        Engine = new InferenceEngine(DeviceKind, options);
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

    /// <summary>This backend's device key: "cpu", "cuda:{ordinal}", or "vulkan" (the engine picks) / "vulkan:{index}".</summary>
    private string PrimaryDeviceKey()
        => LocalDeviceChoices.DeviceKeyFor(DeviceKind ?? LocalDeviceChoices.ResolveKind(Settings.Device, Settings.GPUDeviceId), Settings.GPUDeviceId);

    /// <summary>The devices this backend can run a model on. A GPU backend can also fall back to the CPU,
    /// so it offers both, letting compare mode place one lane on the GPU and one on the CPU to run at once.</summary>
    private List<string> SupportedDevices()
    {
        string primary = PrimaryDeviceKey();
        List<string> devs = [primary];
        if (primary != "cpu")
        {
            // Every GPU as one layer split, starting with this backend's own, so compare mode can pick a split explicitly.
            if (SplitDeviceKey(primary, CudaDeviceCount()) is string split)
            {
                devs.Add(split);
            }
            devs.Add("cpu");
        }
        return devs;
    }

    /// <summary>The layer-split key over <paramref name="gpuCount"/> GPUs with <paramref name="primary"/> first
    /// ("cuda:0+cuda:1"), or null with fewer than two.</summary>
    internal static string SplitDeviceKey(string primary, int gpuCount)
    {
        if (gpuCount < 2 || !primary.StartsWith("cuda:", StringComparison.Ordinal)
            || !int.TryParse(primary.AsSpan(5), out int first))
        {
            return null;
        }
        IEnumerable<int> rest = Enumerable.Range(0, gpuCount).Where(o => o != first);
        return string.Join('+', new[] { first }.Concat(rest).Select(o => $"cuda:{o}"));
    }

    private static int _cudaDeviceCount = -1;

    /// <summary>Visible CUDA devices, read once.</summary>
    private static int CudaDeviceCount()
    {
        if (_cudaDeviceCount < 0)
        {
            try { _cudaDeviceCount = HartsyInference.Cuda.CudaContext.IsAvailable() ? HartsyInference.Cuda.CudaContext.GetDeviceCount() : 0; }
            catch (Exception ex) { Logs.Debug($"[HartsyLocalLLMProvider] CUDA device count unavailable: {ex.Message}"); _cudaDeviceCount = 0; }
        }
        return _cudaDeviceCount;
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

    /// <summary>Finds a Clef release directory (the one holding <c>joint_head.safetensors</c>) named <paramref name="model"/>
    /// under any of <paramref name="folders"/>, searching one level deep; null when there is none.</summary>
    internal static string ResolveDecisionRelease(string model, IEnumerable<string> folders)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }
        foreach (string folder in folders)
        {
            foreach (string candidate in new[] { Path.Combine(folder, model), Path.Combine(folder, "clef", model) })
            {
                if (File.Exists(Path.Combine(candidate, "joint_head.safetensors")))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>Downloads the catalog's decision release <paramref name="model"/> (eg <c>clef-flash</c>) under the Swarm model root, not
    /// the engine's default location, and returns its directory. Nothing is fetched when the files are already there.</summary>
    private static async Task<string> EnsureDecisionReleaseAsync(string model, CancellationToken ct)
    {
        CatalogEntry entry = ModelCatalog.Find(model);
        if (entry is null || entry.Modality != Modality.Decision)
        {
            throw new SwarmReadableErrorException($"Clef release '{model}' not found, and it is not a known decision model. Put its files into Models/llm/{model}.");
        }
        if (string.IsNullOrEmpty(EngineKnobs.ModelsRoot.Value))
        {
            KnobStore.Set(EngineKnobs.ModelsRoot, Program.ServerSettings.Paths.ActualModelRoot);
        }
        IReadOnlyList<ModelAsset> missing = ModelDownloader.MissingAssets(entry);
        if (missing.Count > 0)
        {
            Logs.Info($"[LLMAssistant] Downloading {missing.Count} file(s) of '{model}' to {RepoPaths.ModelsRoot()}.");
            await ModelDownloader.DownloadAsync(missing, null, ct);
        }
        return Path.GetDirectoryName(ModelDownloader.PrimaryLocalPath(entry));
    }

    /// <summary>Answers a Jev / SystemOne request body (<c>/v1/systemone</c> shape) with Cloudflare Clef. The release
    /// directory (<c>model</c> in the body, e.g. <c>clef-flash</c>) must sit in the LLM model folder.</summary>
    public async Task<string> DecideAsync(string requestJson, CancellationToken ct)
    {
        string model = JObject.Parse(requestJson)["model"]?.ToString();
        string dir = ResolveDecisionRelease(model, ModelFolders()) ?? await EnsureDecisionReleaseAsync(model, ct);
        await MaybeEvictForLowMemory();
        ModelSpec spec = new() { Requested = model, Modality = Modality.Decision, LocalPath = dir };
        return await Engine.Decisions.DecideAsync(spec, requestJson, ct);
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
        TextRequest request = await BuildRequestAsync(input, deviceKey, path, ct);
        return (spec, request, deviceKey);
    }

    /// <inheritdoc/>
    public override async Task GenerateLive(ExtendedLLMInput input, string batchId, Func<JObject, Task> onChunk, CancellationToken ct)
    {
        (ModelSpec spec, TextRequest request, string deviceKey) = await ResolveSpecAndRequestAsync(input, ct);
        await ForwardEngineStreamAsync(Engine.Text.StreamAsync(spec, request, ct), onChunk, input.Model, deviceKey);
    }

    /// <summary>Translates the engine's plain text stream into this pack's wire events for <see cref="GenerateLive"/>, pulled out so
    /// it is unit-testable with scripted chunks. Reasoning that a structured parser reports apart from the answer (Muse Spark) goes out
    /// as inline <c>&lt;think&gt;</c> text, the shape a Qwen3 stream already carries it in, which the chat renders as its Reasoning block.
    /// Those events are flagged <c>reasoning</c> so the one-shot accumulator leaves them out of a reply that is spoken or used as a prompt.</summary>
    internal static async Task ForwardEngineStreamAsync(IAsyncEnumerable<TextChunk> chunks, Func<JObject, Task> onChunk, string model, string deviceKey)
    {
        StrongBox<bool> inThink = new(false);
        bool finished = false;
        try
        {
            await ForwardChunksAsync(chunks, onChunk, model, deviceKey, inThink);
            finished = true;
        }
        finally
        {
            if (inThink.Value)
            {
                // A stop or engine failure mid-reasoning still closes the block in the saved reply, without hiding the original error.
                try
                {
                    await onChunk(new JObject() { ["chunk"] = ThinkClose, ["reasoning"] = true });
                }
                catch (Exception ex) when (!finished)
                {
                    Logs.Debug($"[LLMAssistant] Could not close a reasoning block after the stream stopped: {ex.Message}");
                }
            }
        }
    }

    /// <summary>The chunk loop of <see cref="ForwardEngineStreamAsync"/>; <paramref name="inThink"/> tracks an open reasoning block so the
    /// caller can close it however the loop ends.</summary>
    private static async Task ForwardChunksAsync(IAsyncEnumerable<TextChunk> chunks, Func<JObject, Task> onChunk, string model, string deviceKey,
        StrongBox<bool> inThink)
    {
        await foreach (TextChunk chunk in chunks)
        {
            if (inThink.Value && (chunk.Kind is TextChunkKind.Chunk or TextChunkKind.NativeToolCall or TextChunkKind.StopReason))
            {
                inThink.Value = false;
                await onChunk(new JObject() { ["chunk"] = ThinkClose, ["reasoning"] = true });
            }
            switch (chunk.Kind)
            {
                case TextChunkKind.Reasoning:
                    if (!string.IsNullOrEmpty(chunk.Text))
                    {
                        await onChunk(new JObject() { ["chunk"] = inThink.Value ? chunk.Text : ThinkOpen + chunk.Text, ["reasoning"] = true });
                        inThink.Value = true;
                    }
                    break;
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
                    // Only surface truncation/cancellation/error. A normal finish (Stop) is the common case and
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
                        // anywhere else, so an engine-side failure was indistinguishable from "the model chose
                        // to say nothing", at any log level. Cost an hour on 2026-09-05 to a backend left on
                        // the default Device=cuda that could not actually run.
                        Logs.Warning($"[LLMAssistant] Local LLM '{model}' stopped with an engine error on "
                            + $"device '{deviceKey}', so no text was produced: {chunk.Text}. If this device is not usable, "
                            + "set the backend's Device to Auto in Server > Backends.");
                        // The engine's message rides along so the one-shot accumulator can fail the request with it.
                        await onChunk(new JObject() { ["stopReason"] = "error", ["error"] = chunk.Text ?? "" });
                    }
                    break;
                    // Result duplicates the text already streamed as Chunk events: LLMProviderBackend.Generate's
                    // non-streaming accumulator appends both "chunk" and "result", so forwarding Result here would
                    // double the text.
                    // ToolCallDelta/ToolCallAbort are dropped (the completed NativeToolCall is what consumers read), Usage has no wire field, and only ToolLoop emits ToolResult.
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
    /// <see cref="TextChunkKind.ToolResult"/> chunks, a <see cref="TextChunkKind.Status"/> round-limit chunk, one final
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
    /// from settings plus per-request overrides, the vision attachment (decoded only for the final user turn, as
    /// the engine's vision path only ever looks at the latest one), tool definitions when structured tool
    /// calling is enabled, and the conversation's prefix-cache key (see <see cref="BuildRequestCore"/>).</summary>
    private async Task<TextRequest> BuildRequestAsync(ExtendedLLMInput input, string deviceKey, string path, CancellationToken ct)
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
        bool splitReasoning = HasThinkReply(source) && TakesReasoningContentFromFile(path);
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
                    // the model can reference the path when chaining tools. That annotation must never reach a
                    // vision model's literal question (it badly degrades small VLMs), so strip it here.
                    content = StripImageAnnotations(content);
                }
            }
            messages.Add(ToTextMessage(m, content, images, splitReasoning));
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
                // Id, not display name: the model calls it by id and the executor resolves it by id.
                Name = t["id"]?.ToString() ?? t["name"]?.ToString() ?? "",
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
    /// between calls and prefills only the tokens past the longest prefix it already holds, equivalent to a fresh
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
    /// requests: a retained entry is live memory that a same-process pipeline's OOM recovery or a pool trim
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
            // keeps it in sync; see ApplyToolsToInput and ExtendedLLMInput.CreateFromMessages), so setting both
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
            // A request's own loop count wins; else the backend's default; else null leaves the checkpoint's default to the engine.
            Loops = input.Loops is > 0 ? input.Loops : BackendDefaultLoops(settings.RecurrentLoops),
            Device = deviceKey,
            Tools = tools,
            // Vulkan decides graph decode itself; forcing it there fails Gemma-2/3 instead of decoding eagerly.
            GraphDecode = settings.GraphDecode && BackendFactory.Kind(deviceKey) != "vulkan" ? true : null,
            SpeculativeDecode = settings.SpeculativeDecode ? true : null,
            LowVramQuant = settings.LowVramQuant ? "true" : null,
            Placement = PlacementFor(settings.Placement),
            AlwaysFreeMemory = settings.AlwaysFreeMemory,
            PrefixCacheKey = prefixCacheKey
        };
    }

    /// <summary>The RecurrentLoops setting as the engine takes it: null when unset (0), and null with a warning when above
    /// <see cref="LoopedTransformerConfig.DefaultMaxLoops"/>, the ceiling of every looped GGUF the engine loads, so a typo in the
    /// setting leaves the checkpoint's default in force rather than failing every request (as <see cref="PlacementFor"/> does).
    /// A request's own count is not bounded here: the engine rejects it with a message naming the model's maximum.</summary>
    internal static int? BackendDefaultLoops(int recurrentLoops)
    {
        if (recurrentLoops > LoopedTransformerConfig.DefaultMaxLoops)
        {
            Logs.Warning($"[LLMAssistant] RecurrentLoops {recurrentLoops} is above the {LoopedTransformerConfig.DefaultMaxLoops} passes a looped model runs; using the checkpoint's default.");
            return null;
        }
        return recurrentLoops > 0 ? recurrentLoops : null;
    }

    /// <summary>The engine's placement word for the Placement setting: auto, gpu, split or offload. Blank or unrecognized is
    /// null, which leaves the engine's own default (auto) in force rather than failing every request on a typo.</summary>
    internal static string PlacementFor(string placement)
    {
        string value = placement?.Trim().ToLowerInvariant();
        return value is "auto" or "gpu" or "split" or "offload" ? value : null;
    }

    /// <summary>Whether the VRAM Mode setting is Aggressive or Maximum, the two tiers a user picks to hold the least
    /// VRAM between requests, so <see cref="BuildRequestCore"/> keeps no conversation's KV cache between turns under
    /// either. Parsed exactly as <see cref="ParseVramMode"/> parses it, so a blank or unrecognized value (Auto
    /// there) keeps reuse available.</summary>
    internal static bool VramModeHoldsLeastBetweenRequests(string vramMode)
        => !string.IsNullOrWhiteSpace(vramMode) && Enum.TryParse(vramMode.Trim(), ignoreCase: true, out VramTier tier)
            && tier is VramTier.Aggressive or VramTier.Maximum;

    /// <summary>The engine prefix-cache key for one user's conversation on one model, or null unless all three are
    /// present, so a request with no conversation (or no user) never touches the engine's store. The user id comes
    /// from the server-side session, so a client choosing its own conversation id can only ever reach its own
    /// user's entries. Hashed (SHA-256, first 128 bits) rather than concatenated, so no raw id (on the voice
    /// routes that includes a SwarmUI session id, which is a credential) sits in the engine's key store or in
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
    /// message used to fall through to <see cref="TextRole.User"/> like any other unrecognized string: harmless
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
    /// role/id mapping is unit-testable without constructing a provider (which needs a live <c>SettingsRaw</c>).
    /// With <paramref name="splitReasoning"/> (<see cref="TakesReasoningContent(string)"/> for the model's template), an assistant
    /// reply's <c>&lt;think&gt;</c> blocks move to <see cref="TextMessage.ReasoningContent"/> (<see cref="TrySplitThinkBlocks"/>), unless
    /// that would leave an empty turn: a reply that is only reasoning, with no answer or tool call, stays as stored.</summary>
    internal static TextMessage ToTextMessage(LLMMessage m, string content, List<ImageData> images, bool splitReasoning = false)
    {
        bool isTool = m.Role == LLMRoles.Tool;
        string reasoning = null;
        if (splitReasoning && m.Role == LLMRoles.Assistant && TrySplitThinkBlocks(content, out string thought, out string answer)
            && (answer.Length > 0 || m.ToolCalls is { Count: > 0 }))
        {
            reasoning = thought;
            content = answer;
        }
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
                : null,
            ReasoningContent = reasoning
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

    /// <summary>Resolves each attachment (URL/base64/data-URI) to bytes and decodes to interleaved RGB, the
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
    /// tool-chaining). Those must never reach the VLM's question: the image is already delivered as pixels,
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
        // The engine reports each model it placed: the authoritative answer to "loaded, and where", not a shadow of it.
        Dictionary<string, LoadedModelPlacement> placed = new(StringComparer.Ordinal);
        foreach (LoadedModelPlacement loaded in Engine?.Text.LoadedPlacements ?? [])
        {
            placed[FullPath(loaded.ModelPath)] = loaded;
        }
        foreach (string folder in ModelFolders())
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string id = Path.GetFileName(file);
                // mmproj projectors are companions to a text model, not selectable chat models, so hide them.
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
                    // Known only for a model the engine's placement planner loaded (a GGUF on CUDA); anything else reports
                    // not loaded rather than a badge that could silently lie after an out-of-band free.
                    IsLoaded = placed.ContainsKey(FullPath(file)),
                    Metadata = { ["device"] = deviceLabel, ["devices"] = devices }
                };
                if (placed.TryGetValue(FullPath(file), out LoadedModelPlacement placement))
                {
                    info.Metadata["placement"] = PlacementLabel(placement.Placement);
                    info.Metadata["placement_reason"] = placement.Placement.Reason;
                }
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

    /// <summary>A short label for where a model was placed, for the model list and the chat header.</summary>
    internal static string PlacementLabel(TextPlacement placement) => placement.Mode switch
    {
        TextPlacementMode.Split => $"Split {string.Join(" + ", placement.Devices)}",
        TextPlacementMode.Offload => $"Offload on {placement.DeviceKey}",
        _ => $"GPU {placement.DeviceKey}",
    };

    private static string FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) { return path; }
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
