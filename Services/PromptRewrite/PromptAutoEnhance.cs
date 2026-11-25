using System.Text.RegularExpressions;
using Hartsy.Extensions.LLMAssistant.T2I;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;

/// <summary>The generation hook: when <see cref="RewritePolicy"/> says so, rewrites a generation's prompt for the model it is for, right before core parses prompt tags (so every way of generating, the Generate tab, the
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

    /// <summary>A report id the tool makes (a GUID without dashes); anything else is not stored, so a request cannot put an unbounded key into the report store.</summary>
    private static readonly Regex ReportId = new("^[0-9a-f]{32}$", RegexOptions.Compiled);

    /// <summary>The extra-metadata key the image tool writes its <see cref="RewriteScope"/> to, so the chat's and the assistant's switches reach this hook.</summary>
    public const string MetaScope = "llma_rewrite_scope";

    /// <summary>The longest warning, and the longest of all warnings together, written to the image's metadata.</summary>
    public const int MaxWarningChars = 300, MaxWarningsTotalChars = 1500;

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
            string warnings = string.Join(" | ", result.Warnings.Select(w => w.Length <= MaxWarningChars ? w : w[..MaxWarningChars] + "..."));
            meta[MetaWarnings] = warnings.Length <= MaxWarningsTotalChars ? warnings : warnings[..MaxWarningsTotalChars] + "...";
        }
        return meta;
    }

    /// <summary>The late handler: rewrites the prompt of <paramref name="input"/> when the switches say to (see <see cref="RewritePolicy"/>).</summary>
    public static void Apply(T2IParamInput input)
    {
        try
        {
            SwarmUI.Accounts.Session session = input.SourceSession;
            // A second pass over the same input (core re-prepares a task it hands to another Swarm, and the Comfy workflow routes call this too) must not rewrite a rewritten prompt or lose the original.
            if (session?.User is null || PromptTagHandler.ParamAutoEnhance is null || input.ExtraMeta.ContainsKey(MetaStatus))
            {
                return;
            }
            string mode = input.TryGet(PromptTagHandler.ParamAutoEnhance, out string m) ? m : ModeDefault;
            if (RewritePolicy.ReadSwitch(mode) == false || !session.User.HasPermission(LLMAssistantAPI.PermChat))
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
            RewriteSettings settings = RewriteSettings.ForUser(session.User);
            RewriteScope scope = RewriteScope.FromMeta(input.ExtraMeta.TryGetValue(MetaScope, out object from) ? from : null);
            if (!RewritePolicy.ShouldRewrite(session.User, scope, mode, model.Name, styleId, settings))
            {
                return;
            }
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
            if (input.ExtraMeta.TryGetValue(MetaReportId, out object reportId) && reportId is string id && ReportId.IsMatch(id))
            {
                RewriteReports.Put(id, result);
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] Prompt auto-enhance skipped: {ex.Message}");
        }
    }
}
