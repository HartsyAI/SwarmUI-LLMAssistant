using Hartsy.Extensions.LLMAssistant.LLMs;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptStyles;

/// <summary>The user's switches for chat-model addenda, read from the <c>promptStyles</c> object of the merged settings; anything missing takes the default (on, nothing disabled).</summary>
/// <param name="Enabled">The master switch: whether any chat-model style is added to a system prompt. On unless the user turns it off (and with no <c>llm</c> style written, turning it on changes nothing).</param>
/// <param name="Disabled">Ids of styles the user switched off one by one; a disabled style is skipped as if it did not exist, so a less specific one can apply instead.</param>
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
/// <param name="Chars">The length of <see cref="Text"/> plus the separator it is added with; 0 when nothing is added.</param>
/// <param name="EstimatedTokens">A rough token count of what is added (characters over four, rounded up, the same estimate as the dry run's), 0 when nothing is.</param>
public sealed record LlmAddendum(bool Applied, string Text, string StyleId, string StyleScope, string Via, string Reason, int Chars, int EstimatedTokens)
{
    /// <summary>A result that adds nothing, for <paramref name="reason"/>.</summary>
    public static LlmAddendum Nothing(string reason, string styleId = null, string scope = null, string via = null) => new(false, "", styleId, scope, via, reason, 0, 0);

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
    };
}

/// <summary>Adds the addendum of a chat-model (<c>llm</c>) prompt style to a system prompt: a short, model-specific note on how to instruct the model that is actually answering, such as tighter tool-call rules for a
/// small local model. Which style applies is decided by <see cref="PromptStyleResolver.ResolveLlm"/> (the user's own rule first, then the most specific condition, then a style with none); this adds the switches and
/// the layering around it.
/// <para><b>Layering.</b> The addendum goes right after the assistant's instruction for the turn and before everything else that is appended (session note, skill index, project instructions), so the part of the prompt
/// that depends on the model is one fixed block. An assistant that wrote an instruction variant for this model that is <i>more specific</i> than the style's condition keeps its own (the user wrote it for this very model); a
/// rule the user made for the model always applies.</para>
/// <para><b>Determinism.</b> The text depends only on the style file: no timestamps, no user variables, no ordering that varies, so the same model and settings give the same bytes every turn and a backend's prefix cache
/// stays warm. With no matching style (and no style ships for any model) nothing is appended and the prompt is exactly what it was before this existed.</para></summary>
public static class LlmAddendumService
{
    /// <summary>What separates the instruction from the addendum.</summary>
    public const string Separator = "\n\n";

    /// <summary>The tasks a chat turn is made of: always <c>chat</c>, and <c>tools</c> too when tools are offered.</summary>
    public static IReadOnlyCollection<string> TasksFor(bool toolsOn) => toolsOn ? ["chat", "tools"] : ["chat"];

    // ===== The decision (a pure function, so every branch is tested without a server) =====

    /// <summary>Decides what to add for <paramref name="model"/>.</summary>
    /// <param name="styles">The styles the user can use.</param>
    /// <param name="rules">The user's rules that pin a model to a style or to none.</param>
    /// <param name="model">The model that answers (only its id is known when no backend lists it).</param>
    /// <param name="tasks">What the turn is (see <see cref="TasksFor"/>).</param>
    /// <param name="settings">The user's switches.</param>
    /// <param name="assistantAllows">Whether the assistant has not opted out.</param>
    /// <param name="assistantVariantRank">How specific the assistant's own instruction variant for this model is (<see cref="ResolvedAssistant.MatchedVariantSpecificity"/>), or -1 for none.</param>
    public static LlmAddendum Resolve(IEnumerable<PromptStyle> styles, IEnumerable<StyleOverride> rules, LLMModelInfo model, IReadOnlyCollection<string> tasks, AddendumSettings settings,
        bool assistantAllows = true, int assistantVariantRank = -1)
    {
        settings ??= AddendumSettings.Default;
        if (!settings.Enabled)
        {
            return LlmAddendum.Nothing("Chat-model prompt tuning is switched off in Settings.");
        }
        if (!assistantAllows)
        {
            return LlmAddendum.Nothing("This assistant has chat-model prompt tuning turned off.");
        }
        List<PromptStyle> usable = [.. styles.Where(s => s.Kind == PromptStyle.KindLlm && !settings.Disabled.Contains(s.Id))];
        StyleResolution resolution = PromptStyleResolver.ResolveLlm(usable, rules, model, tasks);
        if (resolution.Style is null)
        {
            return LlmAddendum.Nothing(resolution.Reason, via: resolution.Off ? "off" : "none");
        }
        PromptStyle style = resolution.Style;
        int rank = resolution.Candidates.Count > 0 ? resolution.Candidates[0].Rank : 0;
        if (resolution.Via != "override" && assistantVariantRank > rank)
        {
            return LlmAddendum.Nothing("The assistant's own instruction for this model is more specific than the style's condition, so it is used alone.", style.Id, style.Scope, resolution.Via);
        }
        string text = Normalize(style.Body);
        if (text.Length == 0)
        {
            return LlmAddendum.Nothing("The style has no text.", style.Id, style.Scope, resolution.Via);
        }
        int chars = Separator.Length + text.Length;
        return new LlmAddendum(true, text, style.Id, style.Scope, resolution.Via, resolution.Reason, chars, (chars + 3) / 4);
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

    // ===== Reading the user's state (thin; the decision above does the work) =====

    /// <summary>What would be added for <paramref name="modelId"/> for this user and assistant. <paramref name="instructionId"/> is the instruction the turn uses (empty: the chat one), so a more specific assistant
    /// variant of it is seen; <paramref name="toolsOn"/> is whether the turn offers tools.</summary>
    public static LlmAddendum Describe(User user, JObject mergedSettings, string assistantId, string instructionId, string modelId, LLMModelInfo modelInfo, bool toolsOn)
    {
        AddendumSettings settings = AddendumSettings.From(mergedSettings);
        if (!settings.Enabled)
        {
            return LlmAddendum.Nothing("Chat-model prompt tuning is switched off in Settings.");
        }
        LLMModelInfo model = modelInfo ?? new LLMModelInfo { Id = modelId ?? "" };
        ResolvedAssistant assistant = AssistantResolver.Resolve(assistantId, user, mergedSettings);
        string instruction = string.IsNullOrEmpty(instructionId) ? InstructionIds.Chat : instructionId;
        // Only a canonical instruction id is resolved from the assistant's variants; a custom one is not.
        int variantRank = InstructionIds.All.Contains(instruction) ? assistant.MatchedVariantSpecificity(instruction, model) : -1;
        return Resolve(PromptStyleService.For(user).Styles, PromptStyleService.GetOverrides(user), model, TasksFor(toolsOn), settings, assistant.PromptAddendum, variantRank);
    }

    /// <summary>The one call a turn makes: <paramref name="systemPrompt"/> with the addendum for the model that answers added (the same text back when there is none). <paramref name="addendum"/> says what was done.</summary>
    public static string Apply(string systemPrompt, User user, JObject mergedSettings, string assistantId, string instructionId, string modelId, LLMModelInfo modelInfo, bool toolsOn, out LlmAddendum addendum)
    {
        addendum = Describe(user, mergedSettings, assistantId, instructionId, modelId, modelInfo, toolsOn);
        return Append(systemPrompt, addendum);
    }
}
