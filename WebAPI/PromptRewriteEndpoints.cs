using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;
using Hartsy.Extensions.LLMAssistant.T2I;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Text2Image;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>The routes for prompt rewriting: rewrite a prompt for an image or video model (the test box in Settings, the composer's preview, and anything else that wants the rewritten text), say whether a generation
/// would be rewritten and which switch decides, and set a chat's own switch.</summary>
public static class PromptRewriteEndpoints
{
    /// <summary>Whether an automatic rewrite would run, and why; nothing is rewritten and the chat model is not asked. Request: <c>{ threadId?, assistantId?, entryPoint? (chat, the default, or generate), imageModel?,
    /// styleId?, autoEnhance? (the generation's own on/off), chatOverride? (on/off for a chat not created yet) }</c>. <c>assistantId</c> is for a chat not created yet: a chat's own assistant wins, as in its turns. With <c>entryPoint</c> chat and no <c>imageModel</c>, the model the image tool would default to is
    /// used, and when neither the request nor the chat has a switch of its own the default preset's rewrite setting counts as the call's (as in the tool). Returns <c>{success, run, decidedBy (call, chat, assistant,
    /// model, entryPoint or master), preset (the default preset the call level came from, or null), entryPoint, imageModel, layers: {call, chat, assistant, model, entryPoint, master}, settings: {auto, chat,
    /// generate}}</c>, a layer being true, false or null (says nothing), and <c>settings.chat</c> / <c>settings.generate</c> being <c>on</c>, <c>off</c> or <c>default</c>. <c>run</c> is false, decided by the model,
    /// whenever the model's rule is <em>no style</em>.</summary>
    public static async Task<JObject> LLMAssistantGetRewritePolicy(Session session, string threadId = "", string assistantId = "", string entryPoint = RewriteEntryPoints.Chat, string imageModel = "", string styleId = "",
        string autoEnhance = "", string chatOverride = "")
    {
        await Task.CompletedTask;
        string entry = (entryPoint ?? "").Trim();
        if (entry != RewriteEntryPoints.Chat && entry != RewriteEntryPoints.Generate)
        {
            return new JObject { ["success"] = false, ["error"] = "entryPoint must be chat or generate." };
        }
        JObject thread = string.IsNullOrWhiteSpace(threadId) ? null : ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null && !string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
        // A chat's turns always use the chat's own assistant, so it wins over one the request names; the name is for a chat not created yet.
        if (thread?["assistantId"]?.ToString() is { Length: > 0 } own)
        {
            assistantId = own;
        }
        string callMode = autoEnhance, style = styleId, presetName = null;
        RewriteScope scope = RewriteScope.Generate;
        string model = (imageModel ?? "").Trim();
        if (entry == RewriteEntryPoints.Chat)
        {
            scope = RewritePolicy.ForChat(session.User, threadId, assistantId);
            if (string.IsNullOrWhiteSpace(threadId))
            {
                scope = scope with { Chat = RewritePolicy.ReadSwitch(chatOverride) };
            }
            if (model.Length == 0)
            {
                model = RewritePolicy.DefaultChatImageModel(session.User, assistantId);
            }
            T2IPreset preset = RewritePolicy.DefaultPreset(session.User, assistantId);
            if (string.IsNullOrWhiteSpace(style))
            {
                string presetStyle = RewritePolicy.PresetValue(preset, PromptTagHandler.ParamPromptStyle?.Type.ID);
                style = string.Equals(presetStyle, PromptAutoEnhance.StyleAuto, StringComparison.OrdinalIgnoreCase) ? "" : presetStyle;
            }
            string presetMode = RewritePolicy.PresetValue(preset, PromptTagHandler.ParamAutoEnhance?.Type.ID);
            if (scope.Chat is null && RewritePolicy.ReadSwitch(callMode) is null && RewritePolicy.ReadSwitch(presetMode) is not null)
            {
                callMode = presetMode;
                presetName = preset.Title;
            }
        }
        RewriteSettings settings = RewriteSettings.ForUser(session.User);
        RewriteLayers layers = RewritePolicy.Layers(scope, callMode, RewritePolicy.ModelSwitch(session.User, model, style), settings);
        RewriteDecision decision = RewritePolicy.Outcome(layers);
        return new JObject
        {
            ["success"] = true,
            ["run"] = decision.Run,
            ["decidedBy"] = decision.Level,
            ["preset"] = decision.Level == RewriteLevels.Call ? presetName : null,
            ["entryPoint"] = entry,
            ["imageModel"] = model,
            ["layers"] = new JObject
            {
                [RewriteLevels.Call] = layers.Call,
                [RewriteLevels.Chat] = layers.Chat,
                [RewriteLevels.Assistant] = layers.Assistant,
                [RewriteLevels.Model] = layers.Model,
                [RewriteLevels.EntryPoint] = layers.EntryPoint,
                [RewriteLevels.Master] = layers.Master,
            },
            ["settings"] = new JObject { ["auto"] = settings.Auto, ["chat"] = RewritePolicy.ToSwitch(settings.ChatEntry), ["generate"] = RewritePolicy.ToSwitch(settings.GenerateEntry) },
        };
    }

    /// <summary>Sets or clears a chat's own prompt rewrite switch. Request: <c>{ threadId, enabled? }</c>: a boolean <c>enabled</c> pins this chat on or off, whatever the assistant and the settings say; absent or null
    /// clears it. Returns <c>{success, threadId, autoEnhance}</c>.</summary>
    public static async Task<JObject> LLMAssistantSetThreadAutoEnhance(Session session, JObject rawInput)
    {
        await Task.CompletedTask;
        string threadId = rawInput["threadId"]?.ToString();
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        JToken enabled = rawInput["enabled"];
        if (enabled is not null && enabled.Type != JTokenType.Null && enabled.Type != JTokenType.Boolean)
        {
            return new JObject { ["success"] = false, ["error"] = "enabled must be true, false or null." };
        }
        JObject thread = RewritePolicy.SetChatSwitch(session.User, threadId, enabled?.Type == JTokenType.Boolean ? enabled.Value<bool>() : null);
        if (thread is null)
        {
            return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
        }
        return new JObject { ["success"] = true, ["threadId"] = threadId, [RewritePolicy.ThreadKey] = thread[RewritePolicy.ThreadKey] ?? JValue.CreateNull() };
    }

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
        if (result.Rewritten)
        {
            // The text goes back into the prompt box; remember it so generating from it does not rewrite it again.
            RewriteGuard.Remember(session.User?.UserID, result.Text);
        }
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
