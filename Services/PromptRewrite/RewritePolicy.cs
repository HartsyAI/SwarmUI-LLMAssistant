using Hartsy.Extensions.LLMAssistant.Services.ImageTools;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Text2Image;

namespace Hartsy.Extensions.LLMAssistant.Services.PromptRewrite;

/// <summary>The ways a generation can start, each with its own switch under <c>promptRewrite.entryPoints</c>.</summary>
public static class RewriteEntryPoints
{
    /// <summary>The assistant's image tool, in a chat, a headless turn, a session or a scheduled run.</summary>
    public const string Chat = "chat";

    /// <summary>The Generate tab and the API.</summary>
    public const string Generate = "generate";
}

/// <summary>The names of the levels that can switch automatic rewriting, highest first.</summary>
public static class RewriteLevels
{
    /// <summary>The names, as the API reports them.</summary>
    public const string Call = "call", Chat = "chat", Assistant = "assistant", Model = "model", EntryPoint = "entryPoint", Master = "master";
}

/// <summary>What every level says about one generation: true is on, false is off, null leaves it to the levels below.</summary>
/// <param name="Call">The generation's own parameter (or the tool's <c>rewrite</c> argument).</param>
/// <param name="Chat">The chat's override.</param>
/// <param name="Assistant">The assistant's override.</param>
/// <param name="Model">The user's rule for the image model; only <em>no style</em> says anything (off).</param>
/// <param name="EntryPoint">The switch for the way the generation started.</param>
/// <param name="Master">The main switch, which always has an answer.</param>
public sealed record RewriteLayers(bool? Call, bool? Chat, bool? Assistant, bool? Model, bool? EntryPoint, bool Master);

/// <summary>Whether to rewrite, and the level (see <see cref="RewriteLevels"/>) that decided it.</summary>
public sealed record RewriteDecision(bool Run, string Level);

/// <summary>Where a generation came from, and what the chat and the assistant behind it say. It travels with the generation in its extra metadata, because the generation hook sees only the generation.</summary>
/// <param name="EntryPoint">One of <see cref="RewriteEntryPoints"/>.</param>
/// <param name="Chat">The chat's override, only for the chat entry point.</param>
/// <param name="Assistant">The assistant's override, only for the chat entry point.</param>
public sealed record RewriteScope(string EntryPoint, bool? Chat = null, bool? Assistant = null)
{
    /// <summary>The Generate tab and the API: no chat and no assistant.</summary>
    public static RewriteScope Generate => new(RewriteEntryPoints.Generate);

    /// <summary>The compact text written to the generation's extra metadata: entry point, chat switch, assistant switch.</summary>
    public string ToMeta() => $"{EntryPoint} {RewritePolicy.ToSwitch(Chat)} {RewritePolicy.ToSwitch(Assistant)}";

    /// <summary>Reads what <see cref="ToMeta"/> wrote; anything else (nothing, which is every generation the image tool did not start) is <see cref="Generate"/>.</summary>
    public static RewriteScope FromMeta(object value)
    {
        string[] parts = value is string text ? text.Split(' ') : [];
        return parts.Length == 3 && parts[0] == RewriteEntryPoints.Chat
            ? new RewriteScope(RewriteEntryPoints.Chat, RewritePolicy.ReadSwitch(parts[1]), RewritePolicy.ReadSwitch(parts[2]))
            : Generate;
    }
}

/// <summary>The one place that decides whether a generation's prompt is rewritten automatically: per call, then per chat, then per assistant, then the model, then the entry point, then the main switch. The first level that
/// says on or off wins.
/// <para>An explicit rewrite (the preview, <em>Try a rewrite</em>, <em>Enhance Prompt</em>) is the user asking, so no switch stops it.</para></summary>
public static class RewritePolicy
{
    /// <summary>The chat's own switch on its thread; absent means it has none.</summary>
    public const string ThreadKey = "autoEnhance";

    /// <summary>The switch values as text.</summary>
    public const string SwitchOn = "on", SwitchOff = "off", SwitchDefault = "default";

    /// <summary>The first level that says on or off, else the main switch.</summary>
    public static RewriteDecision Resolve(RewriteLayers layers)
    {
        (bool? Value, string Level)[] ordered =
        [
            (layers.Call, RewriteLevels.Call),
            (layers.Chat, RewriteLevels.Chat),
            (layers.Assistant, RewriteLevels.Assistant),
            (layers.Model, RewriteLevels.Model),
            (layers.EntryPoint, RewriteLevels.EntryPoint),
        ];
        foreach ((bool? value, string level) in ordered)
        {
            if (value is bool run)
            {
                return new RewriteDecision(run, level);
            }
        }
        return new RewriteDecision(layers.Master, RewriteLevels.Master);
    }

    /// <summary><c>on</c> is true and <c>off</c> is false; anything else (<c>default</c>, empty, unknown) says nothing.</summary>
    public static bool? ReadSwitch(string value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        SwitchOn => true,
        SwitchOff => false,
        _ => null,
    };

    /// <summary>A switch stored as a boolean or as <c>on</c> / <c>off</c>; anything else says nothing.</summary>
    public static bool? ReadSwitch(JToken value) => value?.Type switch
    {
        JTokenType.Boolean => (bool)value,
        JTokenType.String => ReadSwitch((string)value),
        _ => null,
    };

    /// <summary>The text for a switch.</summary>
    public static string ToSwitch(bool? value) => value is null ? SwitchDefault : value.Value ? SwitchOn : SwitchOff;

    /// <summary>The layers for a generation from <paramref name="scope"/> whose own mode is <paramref name="callMode"/>.</summary>
    public static RewriteLayers Layers(RewriteScope scope, string callMode, bool? model, RewriteSettings settings)
    {
        bool chat = scope.EntryPoint == RewriteEntryPoints.Chat;
        return new RewriteLayers(ReadSwitch(callMode), chat ? scope.Chat : null, chat ? scope.Assistant : null, model, chat ? settings.ChatEntry : settings.GenerateEntry, settings.Auto);
    }

    /// <summary>Whether a generation is rewritten. The model's rule is only looked up when the switches would otherwise run, so a generation with everything off reads no model file.</summary>
    public static bool ShouldRewrite(User user, RewriteScope scope, string callMode, string imageModel, string styleId, RewriteSettings settings)
    {
        RewriteLayers layers = Layers(scope, callMode, null, settings);
        return Resolve(layers).Run && Resolve(layers with { Model = ModelSwitch(user, imageModel, styleId) }).Run;
    }

    /// <summary>What the user's rules say about an image model: off for a rule of <em>no style</em>, else nothing. A style named for the generation itself beats the rule.</summary>
    public static bool? ModelSwitch(User user, string imageModel, string styleId)
    {
        if (!string.IsNullOrWhiteSpace(styleId) || string.IsNullOrWhiteSpace(imageModel))
        {
            return null;
        }
        ImageModelFacts facts = CoreModelFacts.For(imageModel, user) ?? new ImageModelFacts(imageModel, null, null, null, false);
        return PromptStyleResolver.ResolveImage(PromptStyleService.For(user).Styles, PromptStyleService.GetOverrides(user), facts).Off ? false : null;
    }

    /// <summary>The scope of a call to the assistant's image tool: the chat's switch and the answering assistant's (the chat's assistant when the call names none).</summary>
    public static RewriteScope ForChat(User user, string threadId, string assistantId)
    {
        JObject thread = string.IsNullOrWhiteSpace(threadId) ? null : ThreadStorageService.GetThread(user, threadId);
        if (string.IsNullOrWhiteSpace(assistantId))
        {
            assistantId = thread?["assistantId"]?.ToString();
        }
        ResolvedAssistant assistant = AssistantResolver.Resolve(string.IsNullOrWhiteSpace(assistantId) ? null : assistantId, user);
        return new RewriteScope(RewriteEntryPoints.Chat, ReadSwitch(thread?[ThreadKey]), assistant.AutoEnhance);
    }

    /// <summary>Sets or clears (null) a chat's own switch. Not activity in the chat, so it keeps its date and place in the list. Returns the chat, or null if not found.</summary>
    public static JObject SetChatSwitch(User user, string threadId, bool? value)
    {
        return ThreadStorageService.Mutate(user, threadId, thread =>
        {
            if (value is bool on)
            {
                thread[ThreadKey] = on;
            }
            else
            {
                thread.Remove(ThreadKey);
            }
            return true;
        }, touch: false);
    }

    /// <summary>The image model a call to the image tool would use when the call names none: the assistant's default preset's, else the user's default image model; empty when neither is set.</summary>
    public static string DefaultChatImageModel(User user, string assistantId)
    {
        string presetName = ToolConfigService.GetConfig(ToolConstants.GenerateImage, user, assistantId)[GenerateImageTool.ConfigDefaultPreset]?.ToString();
        T2IPreset preset = string.IsNullOrWhiteSpace(presetName) ? null : UserPresetCache.GetPreset(user, presetName);
        JObject layer = [];
        if (preset is not null)
        {
            ImageParamKeys.MergeLayer(layer, preset.ParamMap.Select(kv => new KeyValuePair<string, JToken>(kv.Key, kv.Value)));
        }
        string presetModel = ImageParamKeys.Get(layer, ImageParamKeys.Model)?.ToString()?.Trim() ?? "";
        return ImageParamKeys.PickModel("", "", presetModel, ImageToolSettings.ForUser(user).DefaultModel).Requested;
    }
}
