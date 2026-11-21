using Hartsy.Extensions.LLMAssistant.T2I;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;

/// <summary>The generation hook: when automatic enhancement is on, rewrites a generation's prompt for the model it is for, right before core parses prompt tags (so every way of generating, the Generate tab, the
/// <c>generate_image</c> tool and the API, gets the same treatment). Runs once per image task; the cache inside <see cref="PromptRewriteService"/> makes a batch cost one request.
/// <para>It never blocks a generation on failure: any problem leaves the prompt as it was. It does nothing for a generation with no signed-in session or no model (core's own tag test route is one), for an empty prompt, or for
/// a prompt this extension already produced.</para></summary>
public static class PromptAutoEnhance
{
    /// <summary>The values of the per-generation parameter: follow the setting, force on, force off.</summary>
    public const string ModeDefault = "default", ModeOn = "on", ModeOff = "off";

    /// <summary>The style parameter's value that means "choose by model".</summary>
    public const string StyleAuto = "auto";

    /// <summary>The metadata keys written to the image (<see cref="T2IParamInput.ExtraMeta"/>, core's own place for extra facts about a generation).</summary>
    public const string MetaOriginal = "llm_original_prompt", MetaStyle = "llm_prompt_style", MetaModel = "llm_rewrite_model", MetaStatus = "llm_rewrite_status", MetaWarnings = "llm_rewrite_warnings";

    /// <summary>The extra-metadata key a caller sets to get the outcome back through <see cref="RewriteReports"/>.</summary>
    public const string MetaReportId = "llma_rewrite_id";

    /// <summary>Whether the rewrite runs for a generation whose per-generation mode is <paramref name="mode"/> when the master setting is <paramref name="settingOn"/>. A generation's own choice wins.</summary>
    public static bool ShouldRun(string mode, bool settingOn)
    {
        string m = (mode ?? ModeDefault).Trim().ToLowerInvariant();
        return m == ModeOn || (m != ModeOff && settingOn);
    }

    /// <summary>The facts written to a generation's metadata for a rewrite result: the original prompt, the style, the writing model, what happened and any warnings.</summary>
    public static Dictionary<string, string> BuildMeta(RewriteResult result)
    {
        Dictionary<string, string> meta = new() { [MetaOriginal] = result.Original, [MetaStatus] = result.Rewritten ? "rewritten" : result.FellBack ? "fallback" : "unchanged" };
        if (result.StyleId is not null)
        {
            meta[MetaStyle] = result.StyleScope is null ? result.StyleId : $"{result.StyleId} ({result.StyleScope})";
        }
        if (!string.IsNullOrEmpty(result.Model))
        {
            meta[MetaModel] = result.Model;
        }
        if (result.Warnings.Count > 0)
        {
            meta[MetaWarnings] = string.Join(" | ", result.Warnings);
        }
        return meta;
    }

    /// <summary>The late handler: rewrites the prompt of <paramref name="input"/> when the setting or the generation's own mode says to.</summary>
    public static void Apply(T2IParamInput input)
    {
        try
        {
            SwarmUI.Accounts.Session session = input.SourceSession;
            if (session?.User is null || PromptTagHandler.ParamAutoEnhance is null)
            {
                return;
            }
            string mode = input.TryGet(PromptTagHandler.ParamAutoEnhance, out string m) ? m : ModeDefault;
            RewriteSettings settings = RewriteSettings.From(SettingsService.GetMergedSettings(session.User));
            if (!ShouldRun(mode, settings.Auto))
            {
                return;
            }
            string prompt = input.Get(T2IParamTypes.Prompt);
            if (string.IsNullOrWhiteSpace(prompt) || !input.TryGet(T2IParamTypes.Model, out T2IModel model) || model is null || RewriteGuard.WasProduced(session.User.UserID, prompt))
            {
                return;
            }
            string negative = input.TryGet(T2IParamTypes.NegativePrompt, out string neg) ? neg : "";
            string styleId = input.TryGet(PromptTagHandler.ParamPromptStyle, out string s) && !string.Equals(s, StyleAuto, StringComparison.OrdinalIgnoreCase) ? s : "";
            int width = input.TryGet(T2IParamTypes.Width, out int w) ? w : 0, height = input.TryGet(T2IParamTypes.Height, out int h) ? h : 0;
            RewriteRequest request = new(prompt, negative, model.Name, width, height, "", styleId, Fresh: false);
            // Core's hook is synchronous, so this blocks the engine thread that prepares the image; the call is bounded by the settings' timeout for each of its requests and by the generation being interrupted.
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(input.InterruptToken);
            limit.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds * 3 + 15));
            RewriteResult result = PromptRewriteService.RewriteAsync(request, session.User, session, limit.Token).GetAwaiter().GetResult();
            if (result.StyleId is null)
            {
                return;
            }
            foreach (KeyValuePair<string, string> pair in BuildMeta(result))
            {
                input.ExtraMeta[pair.Key] = pair.Value;
            }
            if (result.Text != prompt)
            {
                input.Set(T2IParamTypes.Prompt, result.Text);
            }
            if (result.Rewritten)
            {
                RewriteGuard.Remember(session.User.UserID, result.Text);
                if (!string.IsNullOrWhiteSpace(negative) && result.Negative != negative)
                {
                    input.Set(T2IParamTypes.NegativePrompt, result.Negative);
                }
            }
            if (input.ExtraMeta.TryGetValue(MetaReportId, out object reportId))
            {
                RewriteReports.Put(reportId?.ToString(), result);
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] Prompt auto-enhance skipped: {ex.Message}");
        }
    }
}
