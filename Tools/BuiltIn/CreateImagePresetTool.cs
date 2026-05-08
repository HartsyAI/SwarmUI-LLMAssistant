using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>Built-in tool: lets the LLM design and save a new T2I preset for the calling user.
/// <para>Delegates the actual save to SwarmUI's core <see cref="BasicAPIFeatures.AddNewPreset"/>
/// API — no parallel persistence layer. The preset becomes immediately available for the next
/// <c>generate_image</c> call (and for the user to use on the Generate tab).</para>
///
/// <para><b>Two perms apply:</b> the LLM-side <c>llm_tool_create_image_preset</c> gate (checked
/// by <see cref="ToolExecutorService"/>) plus SwarmUI's core <c>manage_presets</c> permission
/// (checked at the AddNewPreset endpoint level). A user without <c>manage_presets</c> will get a
/// graceful error from this tool — they can still generate images, just not save presets.</para>
///
/// <para><b>Use case:</b> a user says "make me an anime preset that uses Pony XL with 30 steps
/// and detailed sampler". The LLM picks reasonable values, saves the preset, and on the next
/// "make me an anime image" the new preset is in the list.</para></summary>
public class CreateImagePresetTool : ToolHandler
{
    public override string HandlerId => ToolConstants.CreateImagePreset;

    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        JObject args = ctx.Args;
        Session session = ctx.Session;
        if (session?.User is null)
        {
            return new JObject { ["success"] = false, ["error"] = "create_image_preset requires an authenticated user." };
        }
        // Core perm check: AddNewPreset is gated on Permissions.ManagePresets. Mirror it here so
        // we return a friendly error before invoking the API (which would also reject, less nicely).
        if (!session.User.HasPermission(Permissions.ManagePresets))
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = "You don't have SwarmUI's 'Manage Presets' permission, so the LLM cannot save presets on your behalf. Ask an admin to grant 'manage_presets'."
            };
        }
        string title = args["title"]?.ToString();
        string description = args["description"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(title))
        {
            return new JObject { ["success"] = false, ["error"] = "title is required" };
        }
        JObject paramMap = args["params"] as JObject;
        if (paramMap is null || paramMap.Count == 0)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = "params is required and must be a non-empty object of T2I parameters (eg { model: \"...\", steps: 25, sampler: \"euler\" })."
            };
        }
        // Defensive: prevent the LLM from silently overwriting an existing preset. The user
        // can explicitly request overwrite by setting `overwrite: true`.
        bool overwrite = args["overwrite"]?.Value<bool>() ?? false;
        T2IPresetExists existing = CheckExisting(session, title);
        if (existing == T2IPresetExists.Yes && !overwrite)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = $"A preset named '{title}' already exists. Pass `overwrite: true` to replace it, or pick a different name."
            };
        }
        // Hand off to the core endpoint. We construct the JObject shape AddNewPreset expects:
        // { param_map: { ... } } wrapped in a "raw" outer container.
        JObject raw = new() { ["param_map"] = paramMap };
        try
        {
            JObject result = await BasicAPIFeatures.AddNewPreset(
                session,
                title: title,
                description: description,
                raw: raw,
                preview_image: null,
                preview_image_metadata: null,
                is_edit: existing == T2IPresetExists.Yes && overwrite,
                editing: existing == T2IPresetExists.Yes && overwrite ? title : null,
                is_starred: false
            );
            if (result.TryGetValue("preset_fail", out JToken fail))
            {
                return new JObject { ["success"] = false, ["error"] = fail.ToString() };
            }
            // Preset list changed — invalidate the cache so the next generate_image enrichment
            // sees the new preset.
            UserPresetCache.Invalidate(session.User);
            // Build a compact summary of what was saved, so the LLM (and the asset bubble in chat)
            // can show "Saved preset 'Anime Style XL' (model: ponyXL, 30 steps, ...)".
            string summary = SummarizeSavedPreset(title, description, paramMap);
            return new JObject
            {
                ["success"] = true,
                ["title"] = title,
                ["description"] = description,
                ["overwritten"] = existing == T2IPresetExists.Yes,
                ["summary"] = summary
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] CreateImagePresetTool failed: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    private enum T2IPresetExists { No, Yes }

    /// <summary>Cheap existence check that uses the cached preset list — avoids a DB hit when
    /// the LLM is just creating a brand-new preset.</summary>
    private static T2IPresetExists CheckExisting(Session session, string title)
    {
        return UserPresetCache.GetPreset(session.User, title) is null ? T2IPresetExists.No : T2IPresetExists.Yes;
    }

    /// <summary>One-line human-readable summary of the saved preset. Mirrors what
    /// <c>GenerateImageTool.FormatPresetSummary</c> produces for the LLM-facing description so
    /// the user sees consistent shorthand wherever a preset is described.</summary>
    private static string SummarizeSavedPreset(string title, string description, JObject paramMap)
    {
        StringBuilder sb = new();
        sb.Append(title);
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append(" — ").Append(description);
        }
        List<string> facts = [];
        if (paramMap["model"]?.ToString() is string m && !string.IsNullOrWhiteSpace(m)) facts.Add($"model: {m}");
        if (paramMap["steps"]?.ToString() is string s && !string.IsNullOrWhiteSpace(s)) facts.Add($"{s} steps");
        if (paramMap["sampler"]?.ToString() is string sm && !string.IsNullOrWhiteSpace(sm)) facts.Add($"sampler: {sm}");
        if (paramMap["width"]?.ToString() is string w && paramMap["height"]?.ToString() is string h
            && !string.IsNullOrWhiteSpace(w) && !string.IsNullOrWhiteSpace(h))
        {
            facts.Add($"{w}×{h}");
        }
        if (facts.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", facts)).Append(')');
        }
        return sb.ToString();
    }
}
