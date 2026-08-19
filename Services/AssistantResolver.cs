using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using Hartsy.Extensions.LLMAssistant.LLMs;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>An assistant fully flattened through its <c>extends</c> inheritance chain.
/// Identity fields use child-wins; instructions union variants per id with child overrides;
/// <c>enabledToolIds</c> is replace-if-present (an explicit list fully defines the set, an absent
/// field inherits); <c>toolConfig</c> deep-merges with child winning per-key.</summary>
public class ResolvedAssistant
{
    public string Id;
    public string Name;
    public string Description;
    public string Icon;
    public string Color;
    public string Avatar;
    public bool IsBuiltIn;
    public string Scope;

    /// <summary>Parameter overrides (temperature, maxTokens, topP). Merged across the chain.</summary>
    public JObject Parameters = [];

    /// <summary>Tools available to this assistant. The nearest layer in the chain that declares an
    /// explicit <c>enabledToolIds</c> list fully defines this set; layers without one inherit.</summary>
    public HashSet<string> EnabledToolIds = [];

    /// <summary>Master switch for tool-calling on this assistant — separate from
    /// <see cref="EnabledToolIds"/> (which tools) — this is whether ANY tool system prompt is injected at
    /// all. Off by default is not the default here (true) since most assistants want tools; small local
    /// GGUF models are the case this exists for (see <c>HartsyLocalLLMProviderSettings</c> doc notes on
    /// tool-prompt confusion). Child wins when explicitly set, like the identity fields above — not unioned
    /// like <see cref="EnabledToolIds"/>, since this is a single on/off switch, not a set to grow.</summary>
    public bool ToolsEnabled = true;

    /// <summary>Per-tool config blobs, deep-merged across the chain. Read by tools at execution
    /// time via <see cref="ToolConfigService"/>.</summary>
    public Dictionary<string, JObject> ToolConfig = [];

    /// <summary>For each instruction id, the resolved candidates: <c>default</c> first, then
    /// declared variants. Picked at request time via <see cref="LLMModelMatcher.PickBest"/>
    /// against the active model.</summary>
    public Dictionary<string, List<InstructionVariant>> Instructions = [];

    /// <summary>Walked-through-this-order list of assistant ids for debugging
    /// (eg <c>["yuki", "anime-base", "default"]</c>).</summary>
    public List<string> Lineage = [];

    /// <summary>Returns the best instruction text for a given id and model. Empty string if
    /// no candidate matches and no default exists.</summary>
    public string ResolveInstruction(string instructionId, LLMModelInfo model)
    {
        if (!Instructions.TryGetValue(instructionId, out List<InstructionVariant> variants) || variants.Count == 0)
        {
            return "";
        }
        IEnumerable<(LLMModelMatcher, string)> candidates = variants.Select(v => (v.Matcher, v.Text));
        return LLMModelMatcher.PickBest(candidates, model, defaultValue: "");
    }
}

/// <summary>One candidate text for an instruction id, plus the matcher that decides when it applies.</summary>
public class InstructionVariant
{
    public LLMModelMatcher Matcher = new() { Kind = ModelMatchKind.Default };
    public string Text = "";
}

/// <summary>Walks an assistant's <c>extends</c> chain (id of another assistant in the same
/// merged settings; cycles detected, capped at <see cref="MaxDepth"/>) and produces a flattened
/// <see cref="ResolvedAssistant"/> with child-wins merge rules (see per-field comments in
/// <see cref="ApplyLayer"/>). Result is cached per-user with TTL; invalidate on assistant or
/// settings save. The persisted assistant shape is unchanged — inheritance is read-time only.</summary>
public static class AssistantResolver
{
    public const int MaxDepth = 5;

    private static readonly TimeSpan TTL = TimeSpan.FromMinutes(5);
    private record CacheEntry(DateTime Expires, ResolvedAssistant Resolved);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    /// <summary>Drops the cached entry for one user. Call on any save that could affect what
    /// the user sees (assistant write, shared settings write, etc.).</summary>
    public static void Invalidate(User user)
    {
        if (user is null)
        {
            return;
        }
        string prefix = user.UserID + "///";
        foreach (string key in Cache.Keys.Where(k => k.StartsWith(prefix)).ToArray())
        {
            Cache.TryRemove(key, out _);
        }
    }

    /// <summary>Drops every cached entry. Call after a shared-layer write so all users see the change.</summary>
    public static void InvalidateAll()
    {
        Cache.Clear();
    }

    /// <summary>Resolves the given assistant id (defaults to the user's active one) into its
    /// fully-flattened form. Cached per-user with TTL; the resolver is pure given the merged
    /// settings, so callers can rely on the result being consistent within one request.</summary>
    public static ResolvedAssistant Resolve(string assistantId, User user, JObject settings = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        assistantId ??= AssistantService.GetActiveAssistantId(settings, user);
        string cacheKey = $"{user?.UserID ?? "<anon>"}///{assistantId}";
        if (Cache.TryGetValue(cacheKey, out CacheEntry entry) && entry.Expires > DateTime.UtcNow)
        {
            return entry.Resolved;
        }
        ResolvedAssistant resolved = BuildFromChain(assistantId, settings, user);
        Cache[cacheKey] = new CacheEntry(DateTime.UtcNow + TTL, resolved);
        return resolved;
    }

    private static ResolvedAssistant BuildFromChain(string assistantId, JObject settings, User user)
    {
        JObject assistants = settings["assistants"] as JObject ?? [];
        // Walk parent-first so child layers can override on top.
        List<JObject> chain = [];
        List<string> lineage = [];
        HashSet<string> visited = [];
        string current = assistantId;
        for (int depth = 0; depth < MaxDepth && !string.IsNullOrEmpty(current); depth++)
        {
            if (!visited.Add(current))
            {
                Logs.Warning($"[LLMAssistant] Inheritance cycle detected resolving '{assistantId}' at '{current}'. Stopping walk.");
                break;
            }
            JObject node = assistants[current] as JObject;
            if (node is null)
            {
                // Missing parent — log once and stop. (The default assistant is always seeded.)
                if (depth > 0)
                {
                    Logs.Debug($"[LLMAssistant] Assistant '{assistantId}' extends missing parent '{current}'. Stopping walk.");
                }
                break;
            }
            chain.Add(node);
            lineage.Add(current);
            current = node["extends"]?.ToString();
        }
        // If the assistant id wasn't found at all, fall back to the default assistant so callers
        // always get a usable result (matches AssistantService.GetAssistant's behavior).
        if (chain.Count == 0)
        {
            JObject fallback = assistants[AssistantConstants.DefaultId] as JObject ?? SettingsService.BuildDefaultAssistant();
            chain.Add(fallback);
            lineage.Add(AssistantConstants.DefaultId);
        }
        // The default assistant is NOT implicitly appended (that unioned Swarmie's tools into everything);
        // missing instruction text falls back at read time in AssistantService.ResolveInstruction.
        // Merge parent → child (so child wins). chain[0] is the requested id (most-child).
        chain.Reverse();
        ResolvedAssistant resolved = new() { Id = assistantId, Lineage = lineage };
        foreach (JObject node in chain)
        {
            ApplyLayer(resolved, node);
        }
        return resolved;
    }

    private static readonly JsonMergeSettings DeepMerge = new()
    {
        MergeArrayHandling = MergeArrayHandling.Replace,
        MergeNullValueHandling = MergeNullValueHandling.Merge
    };

    private static void ApplyLayer(ResolvedAssistant target, JObject layer)
    {
        // Identity fields — child wins (last layer applied).
        target.Name        = layer["name"]?.ToString()        ?? target.Name;
        target.Description = layer["description"]?.ToString() ?? target.Description;
        target.Icon        = layer["icon"]?.ToString()        ?? target.Icon;
        target.Color       = layer["color"]?.ToString()       ?? target.Color;
        target.Avatar      = layer["avatar"]?.ToString()      ?? target.Avatar;
        target.Scope       = layer["_scope"]?.ToString()      ?? target.Scope;
        if (layer["isBuiltIn"]?.Type == JTokenType.Boolean)
        {
            target.IsBuiltIn = layer["isBuiltIn"].Value<bool>();
        }
        // Parameters — shallow JObject merge, child keys override.
        if (layer["parameters"] is JObject layerParams && layerParams.Count > 0)
        {
            target.Parameters.Merge(layerParams, DeepMerge);
        }
        // ToolsEnabled — child wins iff explicitly present (identity-field semantics, not a union).
        if (layer["toolsEnabled"]?.Type == JTokenType.Boolean)
        {
            target.ToolsEnabled = layer["toolsEnabled"].Value<bool>();
        }
        // EnabledToolIds — replace-if-present (explicit list, even empty, IS the set; absent inherits).
        if (layer["enabledToolIds"] is JArray ids)
        {
            target.EnabledToolIds.Clear();
            foreach (JToken t in ids)
            {
                string id = t?.ToString();
                if (!string.IsNullOrEmpty(id))
                {
                    target.EnabledToolIds.Add(id);
                }
            }
        }
        // ToolConfig — per-tool deep merge.
        if (layer["toolConfig"] is JObject layerToolConfig)
        {
            foreach (KeyValuePair<string, JToken> kv in layerToolConfig)
            {
                if (kv.Value is not JObject cfg)
                {
                    continue;
                }
                if (!target.ToolConfig.TryGetValue(kv.Key, out JObject existing))
                {
                    target.ToolConfig[kv.Key] = (JObject)cfg.DeepClone();
                }
                else
                {
                    existing.Merge(cfg.DeepClone(), DeepMerge);
                }
            }
        }
        // Instructions — per-id; default text last-wins, variants unioned (child overrides on
        // matcher equality).
        if (layer["instructions"] is JObject layerInstructions)
        {
            foreach (KeyValuePair<string, JToken> kv in layerInstructions)
            {
                MergeInstruction(target, kv.Key, kv.Value);
            }
        }
    }

    /// <summary>Merges a single instruction node from a layer into the resolved set.
    /// Accepts either the new shape (<c>{ default, variants }</c>) or a plain string (legacy/short form).</summary>
    private static void MergeInstruction(ResolvedAssistant target, string instructionId, JToken node)
    {
        if (node is null || node.Type == JTokenType.Null)
        {
            return;
        }
        // Normalize.
        string defaultText;
        JArray variants;
        if (node is JObject obj)
        {
            defaultText = obj["default"]?.ToString();
            variants = obj["variants"] as JArray ?? [];
        }
        else
        {
            // Plain string is treated as the default text.
            defaultText = node.ToString();
            variants = [];
        }
        if (!target.Instructions.TryGetValue(instructionId, out List<InstructionVariant> existing))
        {
            existing = [];
            target.Instructions[instructionId] = existing;
        }
        // Default: child wins iff non-empty. The default is always at index 0 in the list.
        if (!string.IsNullOrEmpty(defaultText))
        {
            InstructionVariant defaultVariant = existing.FirstOrDefault(v => v.Matcher.Kind == ModelMatchKind.Default);
            if (defaultVariant is null)
            {
                existing.Insert(0, new InstructionVariant { Text = defaultText });
            }
            else
            {
                defaultVariant.Text = defaultText;
            }
        }
        // Variants: union; child overrides parent on (kind, pattern) equality.
        foreach (JToken vt in variants)
        {
            if (vt is not JObject vobj)
            {
                continue;
            }
            LLMModelMatcher matcher = LLMModelMatcher.FromJson(vobj);
            if (matcher.Kind == ModelMatchKind.Default)
            {
                continue; // Use the explicit `default` field for that.
            }
            string text = vobj["text"]?.ToString() ?? "";
            InstructionVariant existingMatch = existing.FirstOrDefault(v =>
                v.Matcher.Kind == matcher.Kind &&
                string.Equals(v.Matcher.Pattern, matcher.Pattern, StringComparison.OrdinalIgnoreCase));
            if (existingMatch is null)
            {
                existing.Add(new InstructionVariant { Matcher = matcher, Text = text });
            }
            else
            {
                existingMatch.Text = text;
            }
        }
    }
}
