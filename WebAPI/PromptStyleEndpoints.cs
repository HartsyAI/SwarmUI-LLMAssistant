using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.PromptAssets;
using Hartsy.Extensions.LLMAssistant.Services.PromptStyles;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Routes for prompt styles: the list for the settings screen, one style with its text, saving, deleting (which also resets a customized built-in style), the user's rules that pin a model to a
/// style, and a preview of which style a model gets and why. Nothing here names a host path. Reading needs <c>llm_chat</c>; changing needs <c>llm_settings</c>, and a shared style also
/// <c>llm_shared_write</c>.</summary>
public static class PromptStyleEndpoints
{
    /// <summary>The styles the caller can use, their rules, and the image and video models core lists (so a rule can pick one). Returns <c>{success, styles[], overrides[], imageModels[], canWriteShared}</c>.
    /// A style is <c>{id, kind, name, description, scope, scopes[], matches[{kind, pattern}], format, formatter, negative, supports[], tasks[], guide}</c> (<c>scopes</c> lists every scope holding a copy of it,
    /// so a built-in one the user has customized shows <c>["bundled","personal"]</c>); a rule is <c>{axis, match, style}</c>; an image model is <c>{name, classId, className, compatId, isVideo}</c>.
    /// The text is fetched with <see cref="LLMAssistantGetPromptStyle"/>.</summary>
    public static Task<JObject> LLMAssistantGetPromptStyles(Session session)
    {
        PromptStyleSet set = PromptStyleService.For(session.User);
        JArray styles = [];
        foreach (PromptStyle style in set.Styles)
        {
            JObject json = Describe(style);
            json["scopes"] = new JArray(set.ScopesOf(style.Id));
            styles.Add(json);
        }
        JArray rules = [];
        foreach (StyleOverride rule in PromptStyleService.GetOverrides(session.User))
        {
            rules.Add(new JObject { ["axis"] = rule.Axis, ["match"] = rule.Pattern, ["style"] = rule.Style });
        }
        JArray models = [];
        foreach (ImageModelFacts model in CoreModelFacts.ListAll(session.User))
        {
            models.Add(new JObject { ["name"] = model.Name, ["classId"] = model.ClassId, ["className"] = model.ClassName, ["compatId"] = model.CompatId, ["isVideo"] = model.IsVideo });
        }
        return Task.FromResult(new JObject
        {
            ["success"] = true,
            ["styles"] = styles,
            ["overrides"] = rules,
            ["imageModels"] = models,
            ["canWriteShared"] = session.User?.HasPermission(LLMAssistantAPI.PermSharedWrite) ?? false,
        });
    }

    /// <summary>One style with its text, for the editor. Request: <c>{ id, scope? }</c>: a <c>scope</c> of <c>bundled</c>, <c>shared</c> or <c>personal</c> reads that scope's copy even when a more
    /// personal one takes precedence; without it the one in force is read. Returns <c>{success, style}</c> with <c>body</c> added.</summary>
    public static Task<JObject> LLMAssistantGetPromptStyle(Session session, string id, string scope = "")
    {
        PromptStyle style = PromptStyleService.Find(session.User, id, scope);
        if (style is null)
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "There is no such style." });
        }
        JObject json = Describe(style);
        json["body"] = style.Body;
        return Task.FromResult(new JObject { ["success"] = true, ["style"] = json });
    }

    /// <summary>Creates or replaces a personal style, or a shared one with <c>scope</c> <c>shared</c>. Saving a built-in style's id as a personal style customizes it; deleting that copy resets it.
    /// Request: <c>{ id, kind (image | video | llm), name?, description?, matches? (one line of <c>kind:pattern</c> conditions separated by commas), format? (text | tags | json), formatter?,
    /// negative? (none | separate | inline), supports? (a list or comma line), tasks?, guide?, body, scope? }</c>. A condition the kind cannot use is refused, not dropped.</summary>
    public static Task<JObject> LLMAssistantSavePromptStyle(Session session, JObject rawInput)
    {
        string id = (rawInput["id"]?.ToString() ?? "").Trim().ToLowerInvariant();
        string kind = (rawInput["kind"]?.ToString() ?? PromptStyle.KindImage).Trim().ToLowerInvariant();
        string matchesLine = rawInput["matches"]?.ToString() ?? "";
        PromptStyle style = new(id, kind, (rawInput["name"]?.ToString() ?? "").Trim(), (rawInput["description"]?.ToString() ?? "").Trim(),
            PromptStyleFile.ParseMatches(matchesLine, kind), (rawInput["format"]?.ToString() ?? PromptStyle.FormatText).Trim().ToLowerInvariant(),
            (rawInput["formatter"]?.ToString() ?? "").Trim().ToLowerInvariant(), (rawInput["negative"]?.ToString() ?? PromptStyle.NegativeNone).Trim().ToLowerInvariant(),
            ReadList(rawInput["supports"]), ReadList(rawInput["tasks"]), (rawInput["guide"]?.ToString() ?? "").Trim(), (rawInput["body"]?.ToString() ?? "").Trim(), "");
        if (style.Name.Length == 0)
        {
            style = style with { Name = id };
        }
        // A condition the style's kind cannot use is dropped when it is read, so say so rather than saving fewer conditions than were typed.
        int typed = matchesLine.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        if (typed > PromptStyle.MaxMatches)
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = $"A style can have up to {PromptStyle.MaxMatches} conditions." });
        }
        if (PromptStyle.IsValidKind(kind) && style.Matches.Count < typed)
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = $"Some conditions were not understood. Write them as kind:pattern, separated by commas. A {kind} style can use: {string.Join(", ", PromptStyle.MatchKindsFor(kind))}." });
        }
        bool saved = PromptStyleService.Save(session.User, style, rawInput["scope"]?.ToString() ?? "personal", out string error);
        return Task.FromResult(saved ? new JObject { ["success"] = true } : new JObject { ["success"] = false, ["error"] = error });
    }

    /// <summary>Deletes a personal style, or a shared one with <c>scope</c> <c>shared</c>. A built-in style cannot be deleted, but deleting the personal copy of one resets it. Request: <c>{ id, scope? }</c>.</summary>
    public static Task<JObject> LLMAssistantDeletePromptStyle(Session session, string id, string scope = "personal")
        => Task.FromResult(PromptStyleService.Delete(session.User, id, scope)
            ? new JObject { ["success"] = true }
            : new JObject { ["success"] = false, ["error"] = "There is no such style, or you may not change that one." });

    /// <summary>Replaces the caller's rules that pin a model to a style. Request: <c>{ overrides: [{axis (image | llm), match (a model name or a pattern with * and ?), style (a style id, or off)}] }</c>.</summary>
    public static Task<JObject> LLMAssistantSetStyleOverrides(Session session, JObject rawInput)
    {
        List<StyleOverride> rules = [];
        foreach (JToken token in rawInput["overrides"] as JArray ?? [])
        {
            rules.Add(new StyleOverride(token["axis"]?.ToString() ?? "", token["match"]?.ToString() ?? "", token["style"]?.ToString() ?? ""));
        }
        bool saved = PromptStyleService.SetOverrides(session.User, rules, out string error);
        return Task.FromResult(saved ? new JObject { ["success"] = true } : new JObject { ["success"] = false, ["error"] = error });
    }

    /// <summary>Which style a model gets, and why. Request: <c>{ axis (image | llm), model, task? }</c> (<c>model</c> is an image or video model's name, or a chat model's id). Returns <c>{success, found, style?, off, via,
    /// reason, candidates[{id, scope, matched, rank}], model?}</c>. <c>found</c> is false when core (or the chat backends) has no such model: the answer then uses only what the name alone can match.</summary>
    public static async Task<JObject> LLMAssistantResolvePromptStyle(Session session, string axis, string model, string task = "")
    {
        if (!StyleOverride.IsValidAxis(axis))
        {
            return new JObject { ["success"] = false, ["error"] = "Choose image or llm." };
        }
        model = (model ?? "").Trim();
        if (model.Length == 0)
        {
            return new JObject { ["success"] = false, ["error"] = "Choose a model." };
        }
        PromptStyleSet set = PromptStyleService.For(session.User);
        List<StyleOverride> rules = PromptStyleService.GetOverrides(session.User);
        StyleResolution resolution;
        JObject modelJson;
        bool found;
        if (axis == StyleOverride.AxisImage)
        {
            ImageModelFacts facts = CoreModelFacts.For(model, session.User);
            found = facts is not null;
            facts ??= new ImageModelFacts(model, null, null, null, false);
            resolution = PromptStyleResolver.ResolveImage(set.Styles, rules, facts);
            modelJson = new JObject { ["name"] = facts.Name, ["classId"] = facts.ClassId, ["className"] = facts.ClassName, ["compatId"] = facts.CompatId, ["isVideo"] = facts.IsVideo, ["prompt"] = DescribePrompt(facts.Prompt) };
        }
        else
        {
            LLMModelInfo info = await LLMModelLookup.GetByIdAsync(model, session);
            found = info is not null;
            info ??= new LLMModelInfo { Id = model };
            resolution = PromptStyleResolver.ResolveLlm(set.Styles, rules, info, task);
            modelJson = new JObject { ["id"] = info.Id, ["family"] = info.Family, ["provider"] = info.Provider };
        }
        return new JObject
        {
            ["success"] = true,
            ["found"] = found,
            ["model"] = modelJson,
            ["style"] = resolution.Style is null ? null : Describe(resolution.Style),
            ["off"] = resolution.Off,
            ["via"] = resolution.Via,
            ["reason"] = resolution.Reason,
            ["candidates"] = new JArray(resolution.Candidates.Select(c => new JObject { ["id"] = c.Id, ["scope"] = c.Scope, ["matched"] = c.Matched, ["rank"] = c.Rank == int.MaxValue ? 1000 : c.Rank })),
        };
    }

    /// <summary>What a model's header says about prompting, for the browser; null when it says nothing.</summary>
    private static JObject DescribePrompt(ModelPromptFacts prompt) => prompt is null ? null : new JObject
    {
        ["format"] = prompt.Format,
        ["negative"] = prompt.Negative,
        ["supports"] = prompt.Supports is null ? null : new JArray(prompt.Supports),
        ["boxes"] = prompt.Boxes,
        ["encoders"] = prompt.Encoders is null ? null : new JArray(prompt.Encoders),
    };

    /// <summary>A style as the browser sees it, without its text.</summary>
    private static JObject Describe(PromptStyle style) => new()
    {
        ["id"] = style.Id,
        ["kind"] = style.Kind,
        ["name"] = style.Name,
        ["description"] = style.Description,
        ["scope"] = style.Scope,
        ["matches"] = new JArray(style.Matches.Select(m => new JObject { ["kind"] = m.Kind, ["pattern"] = m.Pattern })),
        ["format"] = style.Format,
        ["formatter"] = style.Formatter,
        ["negative"] = style.Negative,
        ["supports"] = new JArray(style.Supports),
        ["tasks"] = new JArray(style.Tasks),
        ["guide"] = style.Guide,
    };

    /// <summary>A list given as a JSON array or as a comma-separated line, lowercased, without empties.</summary>
    private static List<string> ReadList(JToken token)
    {
        IEnumerable<string> items = token is JArray array ? array.Select(t => t.ToString()) : (token?.ToString() ?? "").Split(',');
        return [.. items.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0)];
    }
}
