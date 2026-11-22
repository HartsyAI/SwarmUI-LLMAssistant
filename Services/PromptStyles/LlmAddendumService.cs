using System.Collections.Concurrent;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptStyles;

/// <summary>The user's switches for chat-model addenda, read from the <c>promptStyles</c> object of the merged settings; anything missing takes the default (on, nothing disabled).</summary>
/// <param name="Enabled">The master switch: whether any chat-model style is added to a system prompt. On unless the user turns it off (and with no <c>llm</c> style written, turning it on changes nothing).</param>
/// <param name="Disabled">Ids of styles the user switched off one by one; a disabled style is skipped as if it did not exist, so a less specific one can apply instead (but see <see cref="LlmAddendumService.Resolve"/> for a rule that names one).</param>
public sealed record AddendumSettings(bool Enabled, IReadOnlyCollection<string> Disabled)
{
    /// <summary>The settings key.</summary>
    public const string Key = "promptStyles";

    /// <summary>The most disabled ids read; the rest are ignored.</summary>
    public const int MaxDisabled = 200;

    /// <summary>The defaults: on, nothing disabled.</summary>
    public static AddendumSettings Default => new(true, []);

    /// <summary>Reads the switches from <paramref name="merged"/> (the merged settings), tolerating anything that is not the expected shape.</summary>
    public static AddendumSettings From(JObject merged)
    {
        if (merged?[Key] is not JObject o)
        {
            return Default;
        }
        bool enabled = o["llmAddenda"]?.Type != JTokenType.Boolean || (bool)o["llmAddenda"];
        List<string> disabled = [];
        if (o["disabled"] is JArray array)
        {
            foreach (JToken token in array.Take(MaxDisabled))
            {
                string id = token?.Type == JTokenType.String ? ((string)token).Trim().ToLowerInvariant() : "";
                if (id.Length > 0 && !disabled.Contains(id))
                {
                    disabled.Add(id);
                }
            }
        }
        return new AddendumSettings(enabled, disabled);
    }
}

/// <summary>What the chat-model style does to one request's system prompt, and why, for a person and for the dry run.</summary>
/// <param name="Applied">Whether text is added to the system prompt.</param>
/// <param name="Text">The text added (normalized: <c>\n</c> line ends, no outer whitespace), or empty.</param>
/// <param name="StyleId">The style that was chosen, or null when none was (or it was set to off).</param>
/// <param name="StyleScope">The scope that style came from, or null.</param>
/// <param name="Via">How it was chosen (<c>override</c>, <c>match</c>, <c>default</c>), or <c>none</c> / <c>off</c> when nothing was added for that reason, or null when the switches stopped it before a style was looked for.</param>
/// <param name="Reason">One line saying why this was added, or why nothing was.</param>
/// <param name="Chars">How many characters are added to the prompt: the text and its separator (when the prompt is already there). 0 when nothing is added.</param>
/// <param name="EstimatedTokens">A rough token count of <paramref name="Chars"/> (<see cref="LlmAddendumService.EstimateTokens"/>, the estimator the dry run uses everywhere), 0 when nothing is added.</param>
/// <param name="Resolution">How the style was chosen (every candidate), for the preview; null when the switches stopped it before a style was looked for.</param>
public sealed record LlmAddendum(bool Applied, string Text, string StyleId, string StyleScope, string Via, string Reason, int Chars, int EstimatedTokens, StyleResolution Resolution = null)
{
    /// <summary>A result that adds nothing, for <paramref name="reason"/>.</summary>
    public static LlmAddendum Nothing(string reason, string styleId = null, string scope = null, string via = null, StyleResolution resolution = null)
        => new(false, "", styleId, scope, via, reason, 0, 0, resolution);

    /// <summary>A warning for a person when the addendum is large (it is paid on every request to the model), or null.</summary>
    public string Warning => Applied && EstimatedTokens >= LlmAddendumService.WarnTokens
        ? $"This adds about {EstimatedTokens} tokens to every request on this model. Consider a shorter style."
        : null;

    /// <summary>The result as the browser and the dry run see it, without the text.</summary>
    public JObject ToJson() => new()
    {
        ["applied"] = Applied,
        ["styleId"] = StyleId,
        ["scope"] = StyleScope,
        ["via"] = Via,
        ["reason"] = Reason,
        ["chars"] = Chars,
        ["estimatedTokens"] = EstimatedTokens,
        ["warning"] = Warning,
    };
}

/// <summary>Adds the addendum of a chat-model (<c>llm</c>) prompt style to a system prompt: a short, model-specific note on how to instruct the model that is actually answering, such as tighter tool-call rules for a
/// small local model. Which style applies is decided by <see cref="PromptStyleResolver.ResolveLlm"/> (the user's own rule first, then the most specific condition, then a style with none); this adds the switches and
/// the layering around it.
/// <para><b>Layering.</b> The addendum goes right after the assistant's instruction for the turn and before everything else that is appended (session note, skill index, project instructions). An assistant that wrote an
/// instruction variant for this model that is <i>more specific</i> than the style's condition keeps its own (the user wrote it for this very model); a rule the user made for the model always applies, unless the style it
/// names is switched off (then nothing is added).</para>
/// <para><b>Determinism.</b> The addendum's own bytes depend only on the style file: no timestamps, no user variables, no ordering that varies, so the same model and settings give the same text every turn. Where it sits in the
/// prompt depends on the instruction before it (which may itself change from turn to turn, for example when the instruction holds per-turn recall), so how much of the prompt a backend's prefix cache can reuse depends on that
/// text too. With no matching style (and no style ships for any model) nothing is appended and the prompt is exactly what it was before this existed.</para>
/// <para><b>Which model, which tools.</b> The style is chosen for the model that will really answer: the one named, else the provider's default (<see cref="LLMModelLookup.ResolveEffectiveModelIdAsync"/>). Its <c>tools</c> task
/// is on only when the turn really sends tools. When no backend lists the model (a listing that failed or timed out is cached as empty for a few minutes), what was last known about that model is used, so a passing outage
/// does not make a family, provider or tag condition stop matching; failing that, only the id and name globs can match.</para></summary>
public static class LlmAddendumService
{
    /// <summary>What separates the instruction from the addendum.</summary>
    public const string Separator = "\n\n";

    /// <summary>The longest style text that is added; a longer one is refused when saved (<see cref="PromptStyleService.Validate"/>) and never added if it is read from a file all the same.</summary>
    public const int MaxChars = 6144;

    /// <summary>From this many estimated tokens the preview warns that the addendum is large.</summary>
    public const int WarnTokens = 500;

    /// <summary>The one token estimator (characters over four, as the dry run's own totals use).</summary>
    public static int EstimateTokens(long chars) => (int)(chars / 4);

    /// <summary>The tasks a chat turn is made of: always <c>chat</c>, and <c>tools</c> too when tools are offered.</summary>
    public static IReadOnlyCollection<string> TasksFor(bool toolsOn) => toolsOn ? ["chat", "tools"] : ["chat"];

    /// <summary>What was last known about each model a backend listed, kept so a listing that fails for a while does not change which style a model gets. Bounded.</summary>
    private static readonly ConcurrentDictionary<string, LLMModelInfo> LastKnown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The most models remembered.</summary>
    private const int MaxLastKnown = 256;

    // ===== The decision (a pure function, so every branch is tested without a server) =====

    /// <summary>Decides what to add for <paramref name="model"/>.</summary>
    /// <param name="styles">The styles the user can use.</param>
    /// <param name="rules">The user's rules that pin a model to a style or to none.</param>
    /// <param name="model">The model that answers (only its id is known when no backend lists it).</param>
    /// <param name="tasks">What the turn is (see <see cref="TasksFor"/>).</param>
    /// <param name="settings">The user's switches.</param>
    /// <param name="assistantAllows">Whether the assistant has not opted out.</param>
    /// <param name="assistantVariantRank">How specific the assistant's own instruction variant for this model is (<see cref="ResolvedAssistant.MatchedVariantSpecificity"/>), or -1 for none.</param>
    /// <param name="resolveWhenOff">True for a preview: the style is looked for (and reported) even when a switch stops it, so the answer to "which style does this model get" does not depend on the switches.</param>
    public static LlmAddendum Resolve(IEnumerable<PromptStyle> styles, IEnumerable<StyleOverride> rules, LLMModelInfo model, IReadOnlyCollection<string> tasks, AddendumSettings settings,
        bool assistantAllows = true, int assistantVariantRank = -1, bool resolveWhenOff = false)
    {
        settings ??= AddendumSettings.Default;
        string gate = !settings.Enabled ? "Chat-model prompt tuning is switched off in Settings."
            : !assistantAllows ? "This assistant has chat-model prompt tuning turned off." : null;
        if (gate is not null && !resolveWhenOff)
        {
            return LlmAddendum.Nothing(gate);
        }
        List<PromptStyle> chatStyles = [.. styles.Where(s => s.Kind == PromptStyle.KindLlm)];
        List<StyleOverride> ruleList = rules is null ? [] : [.. rules];
        StyleResolution resolution = PromptStyleResolver.ResolveLlm(chatStyles, ruleList, model, tasks);
        if (settings.Disabled.Count > 0)
        {
            if (resolution.Via == "override" && resolution.Style is not null && settings.Disabled.Contains(resolution.Style.Id))
            {
                // The user pinned this model to a style and then switched that style off: nothing is added, rather than a different style they did not choose.
                return LlmAddendum.Nothing($"A rule of yours picks the style '{resolution.Style.Id}' for this model, but that style is switched off, so nothing is added.",
                    resolution.Style.Id, resolution.Style.Scope, "override", resolution);
            }
            // A style switched off is skipped as if it did not exist, so a less specific one can apply.
            resolution = PromptStyleResolver.ResolveLlm([.. chatStyles.Where(s => !settings.Disabled.Contains(s.Id))], ruleList, model, tasks);
        }
        if (gate is not null)
        {
            return LlmAddendum.Nothing(gate, resolution.Style?.Id, resolution.Style?.Scope, resolution.Via, resolution);
        }
        if (resolution.Style is null)
        {
            return LlmAddendum.Nothing(resolution.Reason, via: resolution.Off ? "off" : "none", resolution: resolution);
        }
        PromptStyle style = resolution.Style;
        int rank = resolution.Candidates.Count > 0 ? resolution.Candidates[0].Rank : 0;
        if (resolution.Via != "override" && assistantVariantRank > rank)
        {
            return LlmAddendum.Nothing("The assistant's own instruction for this model is more specific than the style's condition, so it is used alone.", style.Id, style.Scope, resolution.Via, resolution);
        }
        string text = Normalize(style.Body);
        if (text.Length == 0)
        {
            return LlmAddendum.Nothing("The style has no text.", style.Id, style.Scope, resolution.Via, resolution);
        }
        if (text.Length > MaxChars)
        {
            return LlmAddendum.Nothing($"The style is longer than {MaxChars} characters, so it is not added.", style.Id, style.Scope, resolution.Via, resolution);
        }
        int chars = Separator.Length + text.Length;
        return new LlmAddendum(true, text, style.Id, style.Scope, resolution.Via, resolution.Reason, chars, EstimateTokens(chars), resolution);
    }

    /// <summary>A style's text as it is added: <c>\n</c> line ends and no outer whitespace, so the same file gives the same bytes on every platform.</summary>
    public static string Normalize(string body) => (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>Adds the addendum to <paramref name="systemPrompt"/> (unchanged when there is none), after the instruction's trailing line breaks.</summary>
    public static string Append(string systemPrompt, LlmAddendum addendum)
    {
        if (addendum is null || !addendum.Applied)
        {
            return systemPrompt;
        }
        string trimmed = (systemPrompt ?? "").TrimEnd('\r', '\n');
        return trimmed.Length == 0 ? addendum.Text : trimmed + Separator + addendum.Text;
    }

    /// <summary><paramref name="addendum"/> with its sizes set to what <see cref="Append"/> really did to <paramref name="before"/> (giving <paramref name="after"/>).</summary>
    public static LlmAddendum Measured(LlmAddendum addendum, string before, string after)
    {
        if (addendum is null || !addendum.Applied)
        {
            return addendum;
        }
        int chars = Math.Max(0, (after?.Length ?? 0) - (before?.Length ?? 0));
        return addendum with { Chars = chars, EstimatedTokens = EstimateTokens(chars) };
    }

    // ===== Reading the user's state (thin; the decision above does the work) =====

    /// <summary>What a model's facts are for choosing a style: what a backend lists, else what it last listed, else only the id.</summary>
    public static LLMModelInfo FactsFor(string modelId, LLMModelInfo listed)
    {
        string id = modelId ?? "";
        if (listed is not null)
        {
            if (id.Length > 0)
            {
                if (LastKnown.Count >= MaxLastKnown && !LastKnown.ContainsKey(id))
                {
                    LastKnown.Clear();
                }
                LastKnown[id] = listed;
            }
            return listed;
        }
        return id.Length > 0 && LastKnown.TryGetValue(id, out LLMModelInfo known) ? known : new LLMModelInfo { Id = id };
    }

    /// <summary>What would be added for a request that names <paramref name="model"/> (empty: the provider's default) for this user and assistant. <paramref name="instructionId"/> is the instruction the turn uses (empty: the
    /// chat one), and <paramref name="instructionModel"/> the model info it was resolved with, so a more specific assistant variant of it is seen as the turn sees it; <paramref name="toolsOn"/> is whether the turn really sends
    /// tools. With <paramref name="preview"/> the style is looked for even when a switch stops it (see <see cref="Resolve"/>).</summary>
    public static async Task<LlmAddendum> DescribeAsync(Session session, JObject mergedSettings, string assistantId, string instructionId, string model, LLMModelInfo instructionModel, bool toolsOn, bool preview = false)
    {
        User user = session?.User;
        AddendumSettings settings = AddendumSettings.From(mergedSettings);
        if (!settings.Enabled && !preview)
        {
            return LlmAddendum.Nothing("Chat-model prompt tuning is switched off in Settings.");
        }
        string effective = await LLMModelLookup.ResolveEffectiveModelIdAsync(model, session);
        bool named = !string.IsNullOrWhiteSpace(model);
        LLMModelInfo listed = named ? instructionModel : await LLMModelLookup.GetByIdAsync(effective, session);
        LLMModelInfo facts = FactsFor(effective, listed);
        ResolvedAssistant assistant = AssistantResolver.Resolve(assistantId, user, mergedSettings);
        string instruction = string.IsNullOrEmpty(instructionId) ? InstructionIds.Chat : instructionId;
        // Only a canonical instruction id is resolved from the assistant's variants; a custom one is not.
        int variantRank = InstructionIds.All.Contains(instruction) ? assistant.MatchedVariantSpecificity(instruction, instructionModel) : -1;
        return Resolve(PromptStyleService.For(user).Styles, PromptStyleService.GetOverrides(user), facts, TasksFor(toolsOn), settings, assistant.PromptAddendum, variantRank, preview);
    }

    /// <summary>The one call a turn makes: <paramref name="systemPrompt"/> with the addendum for the model that answers added (the same text back when there is none), and what was done. <paramref name="toolsOn"/> is
    /// whether the turn really sends tools.</summary>
    public static async Task<(string Prompt, LlmAddendum Addendum)> ApplyAsync(string systemPrompt, Session session, JObject mergedSettings, string assistantId, string instructionId, string model,
        LLMModelInfo instructionModel, bool toolsOn)
    {
        LlmAddendum addendum = await DescribeAsync(session, mergedSettings, assistantId, instructionId, model, instructionModel, toolsOn);
        string result = Append(systemPrompt, addendum);
        return (result, Measured(addendum, systemPrompt, result));
    }
}
