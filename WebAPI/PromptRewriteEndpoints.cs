using Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>The route that rewrites a prompt for an image or video model, for the test box in Settings and for anything else that wants the rewritten text (the same engine the generation hook will use). Needs <c>llm_chat</c>,
/// since it asks the chat model.</summary>
public static class PromptRewriteEndpoints
{
    /// <summary>Rewrites a prompt for a model and says exactly what happened. Request: <c>{ prompt, imageModel, negative?, rewriteModel?, styleId?, width?, height?, fresh? }</c>; <c>rewriteModel</c> is the chat model that
    /// writes (empty for the one in the settings, or the default), <c>styleId</c> forces a style, and <c>fresh</c> (default true here) skips the cache. Returns <c>{success, original, text, negative, rewritten, skipped, retried,
    /// fellBack, style: {id, scope}, via, reason, model, warnings[], usage: {inputTokens, outputTokens}}</c>. A model that fails or an answer that cannot be used is not an error: <c>fellBack</c> is true and <c>text</c> is
    /// what to generate from (the original, or for a JSON-only model the original wrapped). Only a missing prompt is refused.</summary>
    public static async Task<JObject> LLMAssistantRewritePrompt(Session session, string prompt, string imageModel = "", string negative = "", string rewriteModel = "", string styleId = "", int width = 0, int height = 0,
        bool fresh = true)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new JObject { ["success"] = false, ["error"] = "Write a prompt to rewrite." };
        }
        RewriteRequest request = new(prompt, negative ?? "", (imageModel ?? "").Trim(), Math.Max(0, width), Math.Max(0, height), rewriteModel ?? "", styleId ?? "", fresh);
        RewriteResult result = await PromptRewriteService.RewriteAsync(request, session.User, session);
        return new JObject
        {
            ["success"] = true,
            ["original"] = result.Original,
            ["text"] = result.Text,
            ["negative"] = result.Negative,
            ["rewritten"] = result.Rewritten,
            ["skipped"] = result.Skipped,
            ["retried"] = result.Retried,
            ["fellBack"] = result.FellBack,
            ["style"] = result.StyleId is null ? null : new JObject { ["id"] = result.StyleId, ["scope"] = result.StyleScope },
            ["via"] = result.Via,
            ["reason"] = result.Reason,
            ["model"] = result.Model,
            ["warnings"] = new JArray(result.Warnings),
            ["usage"] = result.Usage is null ? null : new JObject { ["inputTokens"] = result.Usage.InputTokens, ["outputTokens"] = result.Usage.OutputTokens },
        };
    }
}
