using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Per-user profile and long-term memory storage. The LLM manages this blob via the
/// <c>memory_read</c> / <c>memory_write</c> tools, and users can view/clear it via the settings
/// UI. Strict user isolation: no shared/admin layer, no cross-user addressing — a user's profile
/// is only ever accessible by that same user's session. See <see cref="EmptyProfile"/> for the
/// schema shape.</summary>
public static class UserProfileService
{
    public const string DataName = "llmassistant";
    public const string ProfileKey = "user_profile";

    // Growth caps to keep the profile prompt-injectable without blowing context budget.
    private const int MaxListEntries = 50;
    private const int MaxFactEntries = 100;
    private const int MaxFieldLength = 2000;

    /// <summary>Loads the calling user's profile. Returns an empty template if none exists.</summary>
    public static JObject GetProfile(User user)
    {
        if (user is null)
        {
            return EmptyProfile();
        }
        string raw = user.GetGenericData(DataName, ProfileKey);
        if (string.IsNullOrEmpty(raw))
        {
            return EmptyProfile();
        }
        try
        {
            JObject parsed = JObject.Parse(raw);
            // Forwards-compat: ensure every expected key exists even if the on-disk blob is older.
            JObject empty = EmptyProfile();
            foreach (KeyValuePair<string, JToken> kvp in empty)
            {
                if (parsed[kvp.Key] is null)
                {
                    parsed[kvp.Key] = kvp.Value;
                }
            }
            return parsed;
        }
        catch
        {
            return EmptyProfile();
        }
    }

    /// <summary>Replaces a user's profile entirely.</summary>
    public static void SaveProfile(User user, JObject profile)
    {
        if (user is null || profile is null)
        {
            return;
        }
        profile["updated"] = DateTime.UtcNow.ToString("o");
        user.SaveGenericData(DataName, ProfileKey, profile.ToString(Formatting.None));
    }

    /// <summary>Clears a user's entire profile (privacy reset). Irreversible.</summary>
    public static void ClearProfile(User user)
    {
        user?.DeleteGenericData(DataName, ProfileKey);
    }

    /// <summary>Writes a single piece of memory for the calling user. <paramref name="category"/>
    /// determines whether the value replaces a scalar field or appends to a list. Returns a
    /// short human-readable summary for the LLM to include in its response.</summary>
    public static string WriteMemory(User user, string category, string content)
    {
        if (user is null)
        {
            return "No user context — memory not saved.";
        }
        if (string.IsNullOrWhiteSpace(category))
        {
            return "No category provided.";
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            return "No content provided.";
        }
        content = content.Trim();
        if (content.Length > MaxFieldLength)
        {
            content = content.Substring(0, MaxFieldLength);
        }
        JObject profile = GetProfile(user);
        string normalized = category.Trim().ToLowerInvariant();
        string summary;
        switch (normalized)
        {
            case "preferred_name":
            case "name":
                profile["preferredName"] = content;
                summary = $"Updated preferred name to \"{content}\".";
                break;
            case "pronouns":
                profile["pronouns"] = content;
                summary = $"Updated pronouns to \"{content}\".";
                break;
            case "bio":
                profile["bio"] = content;
                summary = "Updated user bio.";
                break;
            case "current_work":
            case "currentwork":
                profile["currentWork"] = content;
                summary = $"Updated current work: \"{content}\".";
                break;
            case "preference":
            case "like":
                AppendUnique(profile, "preferences", content, MaxListEntries);
                summary = $"Saved preference: \"{content}\".";
                break;
            case "dislike":
                AppendUnique(profile, "dislikes", content, MaxListEntries);
                summary = $"Saved dislike: \"{content}\".";
                break;
            case "fact":
            default:
                AppendFact(profile, content);
                summary = $"Saved fact: \"{content}\".";
                break;
        }
        SaveProfile(user, profile);
        return summary;
    }

    /// <summary>Renders a user profile as a system-prompt-ready text block, or an empty string
    /// if there's nothing to report. This is what gets injected via the <c>{{userProfile}}</c>
    /// template variable.</summary>
    public static string RenderProfileForPrompt(JObject profile)
    {
        if (profile is null)
        {
            return "";
        }
        StringBuilder sb = new();
        bool any = false;
        sb.AppendLine("# About the user");
        string name = profile["preferredName"]?.ToString();
        if (!string.IsNullOrWhiteSpace(name))
        {
            string pronouns = profile["pronouns"]?.ToString();
            sb.Append("- Name: ").Append(name);
            if (!string.IsNullOrWhiteSpace(pronouns))
            {
                sb.Append(" (").Append(pronouns).Append(')');
            }
            sb.AppendLine();
            any = true;
        }
        string bio = profile["bio"]?.ToString();
        if (!string.IsNullOrWhiteSpace(bio))
        {
            sb.Append("- Bio: ").AppendLine(bio);
            any = true;
        }
        string currentWork = profile["currentWork"]?.ToString();
        if (!string.IsNullOrWhiteSpace(currentWork))
        {
            sb.Append("- Currently working on: ").AppendLine(currentWork);
            any = true;
        }
        if (profile["preferences"] is JArray preferences && preferences.Count > 0)
        {
            sb.AppendLine("- Preferences:");
            foreach (JToken pref in preferences)
            {
                sb.Append("    * ").AppendLine(pref.ToString());
            }
            any = true;
        }
        if (profile["dislikes"] is JArray dislikes && dislikes.Count > 0)
        {
            sb.AppendLine("- Dislikes:");
            foreach (JToken d in dislikes)
            {
                sb.Append("    * ").AppendLine(d.ToString());
            }
            any = true;
        }
        if (profile["facts"] is JArray facts && facts.Count > 0)
        {
            sb.AppendLine("- Notes:");
            foreach (JToken fact in facts)
            {
                string text = (fact is JObject fo) ? fo["content"]?.ToString() : fact.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.Append("    * ").AppendLine(text);
                }
            }
            any = true;
        }
        return any ? sb.ToString() : "";
    }

    /// <summary>Builds the variable dict for <see cref="InstructionService.SubstituteVariables"/>.
    /// Called from <see cref="WebAPI.ChatEndpoints"/> when assembling the system prompt.</summary>
    public static Dictionary<string, string> BuildPromptVariables(User user, JObject assistant)
    {
        JObject profile = GetProfile(user);
        string name = profile["preferredName"]?.ToString() ?? "";
        return new Dictionary<string, string>
        {
            ["userName"] = string.IsNullOrWhiteSpace(name) ? "" : name,
            ["userProfile"] = RenderProfileForPrompt(profile),
            ["currentDate"] = DateTime.Now.ToString("yyyy-MM-dd"),
            ["assistantName"] = assistant?["name"]?.ToString() ?? "Assistant"
        };
    }

    /// <summary>Blank profile shape, e.g. <c>{ preferredName: "Kaleb", pronouns: "he/him", bio: "...",
    /// currentWork: "...", preferences: [...], dislikes: [...], facts: [{ content, created }], updated: "ISO8601" }</c>.</summary>
    private static JObject EmptyProfile() => new()
    {
        ["preferredName"] = "",
        ["pronouns"] = "",
        ["bio"] = "",
        ["currentWork"] = "",
        ["preferences"] = new JArray(),
        ["dislikes"] = new JArray(),
        ["facts"] = new JArray(),
        ["updated"] = ""
    };

    /// <summary>Appends a value to a list field, deduplicating case-insensitively and capping
    /// the list at <paramref name="cap"/> entries (oldest dropped first).</summary>
    private static void AppendUnique(JObject profile, string field, string value, int cap)
    {
        JArray arr = profile[field] as JArray ?? [];
        string normalized = value.Trim();
        foreach (JToken existing in arr)
        {
            if (string.Equals(existing.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                profile[field] = arr;
                return;
            }
        }
        arr.Add(normalized);
        while (arr.Count > cap)
        {
            arr.RemoveAt(0);
        }
        profile[field] = arr;
    }

    /// <summary>Appends a fact object (content + created timestamp) with case-insensitive dedup
    /// and a hard cap.</summary>
    private static void AppendFact(JObject profile, string content)
    {
        JArray arr = profile["facts"] as JArray ?? [];
        string normalized = content.Trim();
        foreach (JToken existing in arr)
        {
            string txt = (existing is JObject fo) ? fo["content"]?.ToString() : existing.ToString();
            if (string.Equals(txt, normalized, StringComparison.OrdinalIgnoreCase))
            {
                profile["facts"] = arr;
                return;
            }
        }
        arr.Add(new JObject
        {
            ["content"] = normalized,
            ["created"] = DateTime.UtcNow.ToString("o")
        });
        while (arr.Count > MaxFactEntries)
        {
            arr.RemoveAt(0);
        }
        profile["facts"] = arr;
    }
}
