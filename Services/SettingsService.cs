using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Manages extension-level settings (instructions, features, parameters). Backend config is in Server > Backends.
///
/// <para>Storage model (multi-user aware):</para>
/// <list type="bullet">
/// <item><b>Shared layer</b> — one blob at <c>__shared/llmassistant/config</c>, containing the admin-curated
/// baseline (shared assistants, shared tools, default instructions, default parameters). Only users with the
/// <c>llm_shared_write</c> permission may mutate this layer.</item>
/// <item><b>User layer</b> — one blob per user at <c>{userId}/llmassistant/user_config</c>, containing that
/// user's personal overrides, personal assistants, personal tools, and their preferred model / parameters.</item>
/// </list>
/// <para>A user's effective view (<see cref="GetMergedSettings"/>) is shared ⊕ user. For dict-valued fields like
/// <c>assistants</c> and <c>tools</c>, entries are unioned and the personal layer wins on ID collision. The
/// extension tags each entry with a <c>_scope</c> marker (<c>"shared"</c> or <c>"personal"</c>) so the UI can
/// render badges and know which layer a delete targets.</para>
/// </summary>
public static class SettingsService
{
    public const string DataName = "llmassistant";
    public const string ConfigKey = "config";           // shared / admin-managed baseline
    public const string UserConfigKey = "user_config";  // per-user override layer

    /// <summary>Scope marker injected on dict entries (assistants/tools) so the UI can distinguish
    /// admin-managed shared items from the user's personal items.</summary>
    public const string ScopeShared = "shared";
    public const string ScopePersonal = "personal";

    /// <summary>Default settings structure.</summary>
    public static JObject DefaultSettings => new()
    {
        ["preferredModel"] = "",
        ["preferredVisionModel"] = "",
        ["activeAssistantId"] = AssistantConstants.DefaultId,
        ["parameters"] = new JObject
        {
            ["temperature"] = 1.0,
            ["maxTokens"] = 1024,
            ["topP"] = 0.9,
            ["maxContextMessages"] = 0
        },
        ["assistants"] = new JObject
        {
            [AssistantConstants.DefaultId] = BuildDefaultAssistant()
        },
        ["tools"] = ToolRegistryService.BuildDefaultTools(),
        // Legacy keys kept for backward compatibility and T2I prompt tags
        ["instructions"] = new JObject
        {
            [InstructionIds.Chat] = DefaultInstructions.Chat,
            [InstructionIds.Vision] = DefaultInstructions.Vision,
            [InstructionIds.Caption] = DefaultInstructions.Caption,
            [InstructionIds.Prompt] = DefaultInstructions.Prompt,
            [InstructionIds.RandomPrompt] = DefaultInstructions.RandomPrompt,
            [InstructionIds.InstructionGen] = DefaultInstructions.InstructionGen,
            [InstructionIds.Companion] = DefaultInstructions.Companion,
            ["custom"] = new JObject()
        },
        ["featureMappings"] = new JObject
        {
            [FeatureKeys.EnhancePrompt] = InstructionIds.Prompt,
            [FeatureKeys.MagicVision] = InstructionIds.Caption,
            [FeatureKeys.ChatMode] = InstructionIds.Chat,
            [FeatureKeys.VisionMode] = InstructionIds.Vision,
            [FeatureKeys.PromptMode] = InstructionIds.Prompt,
            [FeatureKeys.RandomPrompt] = InstructionIds.RandomPrompt,
            [FeatureKeys.GenerateInstruction] = InstructionIds.InstructionGen,
            [FeatureKeys.CompanionMode] = InstructionIds.Companion
        },
        ["companion"] = BuildDefaultCompanionSettings()
    };

    /// <summary>Default companion overlay settings — a per-user block controlling whether the
    /// floating in-page helper is visible, which assistant powers it, and which quick-action
    /// buttons / chatter triggers are enabled. Per-user only; no shared layer (each user picks
    /// their own companion experience).</summary>
    public static JObject BuildDefaultCompanionSettings() => new()
    {
        // Master on/off. Defaults to OFF so the companion is always opt-in.
        ["enabled"] = false,
        // Which assistant powers the companion. Empty string = follow the user's active assistant.
        ["personaId"] = "",
        // Snap corner: top-left | top-right | bottom-left | bottom-right. Free-drag offsets stack on top.
        ["corner"] = "bottom-right",
        ["offsetX"] = 24,
        ["offsetY"] = 24,
        ["opacity"] = 0.95,
        ["expanded"] = false,
        ["buttons"] = new JObject
        {
            [InstructionIds.Companion + "_ask"] = true,
            ["critique_last_image"] = true,
            ["help_with_prompt"] = true,
            ["suggest_preset"] = true,
            ["explain_feature"] = true,
            ["daily_tip"] = true
        },
        // Ambient chatter — unsolicited messages from the companion. Default values are
        // deliberately conservative: greeting on, reactions and idle off, so a fresh install
        // doesn't surprise the user. Quiet mode is the master mute the user can hit if any
        // of the other triggers ever feel intrusive.
        ["chatter"] = new JObject
        {
            ["quietMode"] = false,
            ["greeting"] = true,
            ["reactions"] = false,
            ["idle"] = false,
            ["idleMinutes"] = 8,
            ["maxPerSession"] = 5,
            ["quietHours"] = false,
            ["quietStart"] = 22,
            ["quietEnd"] = 8
        }
    };

    /// <summary>Builds the default assistant — "Swarmie", a SwarmUI-savvy helper that can look
    /// up the bundled docs to give exact, doc-grounded how-to answers. Uses the SwarmUI logo
    /// (served via OtherAssets) as its avatar and a Swarmie-specific chat instruction.</summary>
    public static JObject BuildDefaultAssistant() => new()
    {
        ["id"] = AssistantConstants.DefaultId,
        ["name"] = "Swarmie",
        ["description"] = "Your friendly SwarmUI helper. Answers how-tos straight from the official docs.",
        ["icon"] = "chat",
        ["color"] = "#7c8aff",
        ["avatar"] = SwarmieAvatarUrl,
        ["instructions"] = new JObject
        {
            [InstructionIds.Chat] = DefaultInstructions.Swarmie,
            [InstructionIds.Vision] = DefaultInstructions.Vision,
            [InstructionIds.Caption] = DefaultInstructions.Caption,
            [InstructionIds.Prompt] = DefaultInstructions.Prompt,
            [InstructionIds.RandomPrompt] = DefaultInstructions.RandomPrompt,
            [InstructionIds.InstructionGen] = DefaultInstructions.InstructionGen,
            [InstructionIds.Companion] = DefaultInstructions.Companion
        },
        ["parameters"] = new JObject(),
        ["enabledToolIds"] = new JArray(ToolConstants.BuiltInIds.Cast<object>().ToArray()),
        ["isBuiltIn"] = true,
        ["created"] = DateTime.UtcNow.ToString("o"),
        ["updated"] = DateTime.UtcNow.ToString("o")
    };

    /// <summary>URL Swarmie's avatar resolves to in the browser. Served by SwarmUI's
    /// ExtensionFile route from <c>Assets/swarmui-logo.jpg</c> registered in
    /// <see cref="LLMAssistantExtension.OnPreInit"/> via <c>OtherAssets</c>.</summary>
    public const string SwarmieAvatarUrl = "/ExtensionFile/LLMAssistantExtension/Assets/swarmui-logo.jpg";

    private static readonly JsonMergeSettings MergeSettings = new()
    {
        MergeArrayHandling = MergeArrayHandling.Replace,
        MergeNullValueHandling = MergeNullValueHandling.Merge
    };

    /// <summary>Gets the current settings, merged with defaults for any missing keys.</summary>
    public static JObject GetSettings()
    {
        string raw = Program.Sessions.GenericSharedUser.GetGenericData(DataName, ConfigKey);
        JObject settings = string.IsNullOrEmpty(raw) ? new JObject() : JObject.Parse(raw);
        JObject result = (JObject)DefaultSettings.DeepClone();
        result.Merge(settings, MergeSettings);
        return result;
    }

    /// <summary>Saves settings (merges with existing). Legacy merge semantics — use
    /// <see cref="ReplaceSharedSettings"/> for write paths that need to remove keys (deletes).</summary>
    public static JObject SaveSettings(JObject incoming)
    {
        JObject current = GetSettings();
        current.Merge(incoming, MergeSettings);
        Program.Sessions.GenericSharedUser.SaveGenericData(DataName, ConfigKey, current.ToString(Formatting.None));
        return current;
    }

    /// <summary>Fully replaces the shared settings blob. Unlike <see cref="SaveSettings"/>, keys
    /// that are missing from <paramref name="replacement"/> will be removed on disk. Use this for
    /// any mutation of assistants/tools/instructions so that deletes are honored.</summary>
    public static JObject ReplaceSharedSettings(JObject replacement)
    {
        replacement ??= [];
        Program.Sessions.GenericSharedUser.SaveGenericData(DataName, ConfigKey, replacement.ToString(Formatting.None));
        return replacement;
    }

    /// <summary>Resets shared settings to defaults. Does NOT touch user overrides.</summary>
    public static JObject ResetSettings()
    {
        JObject defaults = DefaultSettings;
        Program.Sessions.GenericSharedUser.SaveGenericData(DataName, ConfigKey, defaults.ToString(Formatting.None));
        return defaults;
    }

    /// <summary>Loads a user's personal override layer. Returns an empty object if the user has no overrides.</summary>
    public static JObject GetUserSettings(User user)
    {
        if (user is null)
        {
            return [];
        }
        string raw = user.GetGenericData(DataName, UserConfigKey);
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }
        try
        {
            return JObject.Parse(raw);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Replaces a user's personal override layer in full.</summary>
    public static void ReplaceUserSettings(User user, JObject userSettings)
    {
        if (user is null)
        {
            return;
        }
        userSettings ??= [];
        user.SaveGenericData(DataName, UserConfigKey, userSettings.ToString(Formatting.None));
    }

    /// <summary>Merges the given patch into the user's personal override layer. Returns the new personal layer.</summary>
    public static JObject PatchUserSettings(User user, JObject patch)
    {
        JObject current = GetUserSettings(user);
        if (patch is not null)
        {
            current.Merge(patch, MergeSettings);
        }
        ReplaceUserSettings(user, current);
        return current;
    }

    /// <summary>Clears a user's personal override layer entirely (reset-to-defaults for that user).
    /// Does NOT touch the shared layer.</summary>
    public static void ResetUserSettings(User user)
    {
        user?.DeleteGenericData(DataName, UserConfigKey);
    }

    /// <summary>Returns the effective settings for a user: shared baseline ⊕ user overrides.
    /// Dict-valued fields (<c>assistants</c>, <c>tools</c>) are unioned with per-entry <c>_scope</c> tags;
    /// personal entries win on ID collision. Scalar fields and object fields use the standard merge rules
    /// (personal wins, null values are preserved).</summary>
    public static JObject GetMergedSettings(User user)
    {
        JObject shared = GetSettings();
        if (user is null)
        {
            AnnotateScopes(shared, ScopeShared);
            return shared;
        }
        JObject personal = GetUserSettings(user);
        // Union merge for assistants/tools (the dicts need scope tagging)
        JObject mergedAssistants = UnionMergeDict(shared["assistants"] as JObject, personal["assistants"] as JObject);
        JObject mergedTools = UnionMergeDict(shared["tools"] as JObject, personal["tools"] as JObject);
        // Everything else: shallow merge with personal winning
        JObject result = (JObject)shared.DeepClone();
        result["assistants"] = mergedAssistants;
        result["tools"] = mergedTools;
        // Merge scalar + object fields (instructions, parameters, featureMappings, preferredModel, etc.)
        JObject personalClone = (JObject)personal.DeepClone();
        personalClone.Remove("assistants");
        personalClone.Remove("tools");
        result.Merge(personalClone, MergeSettings);
        return result;
    }

    /// <summary>Union-merges two dicts of entries (e.g., assistants or tools), tagging each entry's scope.
    /// Personal entries override shared entries with the same key.</summary>
    private static JObject UnionMergeDict(JObject sharedDict, JObject personalDict)
    {
        JObject result = [];
        if (sharedDict is not null)
        {
            foreach (KeyValuePair<string, JToken> kvp in sharedDict)
            {
                if (kvp.Value is not JObject entry)
                {
                    continue;
                }
                JObject clone = (JObject)entry.DeepClone();
                clone["_scope"] = ScopeShared;
                result[kvp.Key] = clone;
            }
        }
        if (personalDict is not null)
        {
            foreach (KeyValuePair<string, JToken> kvp in personalDict)
            {
                if (kvp.Value is not JObject entry)
                {
                    continue;
                }
                JObject clone = (JObject)entry.DeepClone();
                clone["_scope"] = ScopePersonal;
                result[kvp.Key] = clone;
            }
        }
        return result;
    }

    /// <summary>Tags every assistant/tool entry in a settings blob with the given scope marker.
    /// Used when returning the shared settings as-is for anonymous (null user) calls.</summary>
    private static void AnnotateScopes(JObject settings, string scope)
    {
        if (settings?["assistants"] is JObject assistants)
        {
            foreach (KeyValuePair<string, JToken> kvp in assistants)
            {
                if (kvp.Value is JObject entry)
                {
                    entry["_scope"] = scope;
                }
            }
        }
        if (settings?["tools"] is JObject tools)
        {
            foreach (KeyValuePair<string, JToken> kvp in tools)
            {
                if (kvp.Value is JObject entry)
                {
                    entry["_scope"] = scope;
                }
            }
        }
    }

    /// <summary>Strips the <c>_scope</c> marker from an entry before saving it back. The scope is metadata
    /// that lives in the merged view only; it should never end up in either the shared or user blob.</summary>
    public static JObject StripScope(JObject entry)
    {
        if (entry is null)
        {
            return null;
        }
        JObject clone = (JObject)entry.DeepClone();
        clone.Remove("_scope");
        return clone;
    }
}

/// <summary>Default instruction texts for each feature.</summary>
public static class DefaultInstructions
{
    /// <summary>Swarmie's chat persona — a SwarmUI-savvy helper that uses the swarm_docs tool
    /// to answer how-to questions with citations to the official docs. Used as the
    /// <c>chat</c> instruction on the built-in default assistant.</summary>
    public const string Swarmie = """
        You are Swarmie, the official in-app helper for SwarmUI — a Stable Diffusion / image generation interface.
        You are warm, concise, and unmistakably useful. Today is {{currentDate}}.

        {{userProfile}}

        # How you answer SwarmUI questions
        Whenever the user asks how to do anything in SwarmUI — a feature, a setting, an API call, an extension, prompt syntax, model setup, troubleshooting — you MUST consult the bundled documentation rather than guessing.

        Use the swarm_docs tool:
          1. If you don't already know which doc covers the topic, call swarm_docs with action='list' once to see every available .md file.
          2. Then call swarm_docs with action='read' and the most relevant path (e.g. 'Basic Usage.md', 'Features/Prompt Syntax.md', 'APIRoutes/T2IAPI.md') to get the actual content.
          3. Answer the user using the doc as your source of truth. Quote short passages where helpful and ALWAYS cite the doc you used at the end of your reply, like: "(Source: docs/Features/Prompt Syntax.md)".
          4. If multiple docs are relevant, read each one before answering. Don't speculate beyond what the docs say — if a doc doesn't cover the question, say so plainly.

        # Memory
        You also have a strictly per-user memory via the memory_write tool. When the user shares their preferred name, pronouns, what they're working on, or any durable preference / fact about themselves, save it with the appropriate category (preferred_name, pronouns, bio, current_work, preference, dislike, fact). Never ask questions just to fill memory; only save things the user volunteered.

        # Style
        - Be concise but thorough. Use short paragraphs and tight bullet lists.
        - When discussing image prompts, use Stable Diffusion terminology naturally.
        - When you cite a doc, give the exact relative path so the user can open it.
        - If you don't know something and the docs don't cover it, say so honestly instead of inventing details.
        """;

    public const string Chat = """
        You are {{assistantName}}, a helpful AI assistant integrated into SwarmUI, a Stable Diffusion image generation interface.
        You can help users with prompt crafting, image generation tips, and general conversation.
        Be concise but thorough. When discussing image prompts, use Stable Diffusion terminology.

        Today is {{currentDate}}.

        {{userProfile}}

        You have a persistent, strictly per-user memory available via the memory_write tool. When the user shares something worth remembering across future conversations — their preferred name, pronouns, a real preference, what they're currently working on, or any durable fact about themselves — call memory_write with the appropriate category (preferred_name, pronouns, bio, current_work, preference, dislike, or fact). Only write when the user naturally shares information; never ask questions just to fill memory, and never store transient details like the current message's task. If you don't yet know the user's name, you may politely ask once near the start of a fresh conversation.
        """;

    public const string Vision = """
        You are a vision assistant. Analyze the provided image in detail.
        Describe what you see, including subjects, composition, style, colors, lighting, and mood.
        If asked specific questions about the image, answer them directly.
        """;

    public const string Caption = """
        Generate a detailed caption for this image suitable for use as a Stable Diffusion prompt.
        Focus on: subject description, art style, medium, lighting, color palette, composition, mood, and quality tags.
        Output ONLY the caption text, no explanations or prefixes.
        Use comma-separated tags and natural language descriptions mixed together.
        """;

    public const string Prompt = """
        You are a Stable Diffusion prompt expert. Take the user's basic idea and transform it into
        a detailed, high-quality image generation prompt. Include: subject details, art style, medium,
        lighting, color palette, composition, mood, and quality enhancers.
        Output ONLY the enhanced prompt, no explanations.
        """;

    public const string RandomPrompt = """
        Generate a random, creative, and detailed Stable Diffusion prompt for an interesting image.
        Be creative and varied. Include: subject, style, medium, lighting, colors, mood, and quality tags.
        Output ONLY the prompt, no explanations.
        """;

    public const string InstructionGen = """
        You are a system prompt engineer. Based on the user's description of what they want an AI to do,
        create a clear, effective system prompt / instruction set. The instruction should be specific,
        well-structured, and optimized for LLM performance.
        Output ONLY the instruction text, no meta-commentary.
        """;

    /// <summary>Companion overlay persona — short, glanceable, single-paragraph replies designed for
    /// the small floating speech bubble. Borrows the Swarmie chat persona's helpfulness but trims it to
    /// fit a Clippy-style ambient helper rather than a full chat transcript.</summary>
    public const string Companion = """
        You are {{assistantName}}, a friendly floating helper inside SwarmUI — a Stable Diffusion image generation interface.
        You appear as a small character in the corner of the user's screen and reply in a tiny speech bubble. Today is {{currentDate}}.

        {{userProfile}}

        # How you respond
        - Keep replies to ONE short paragraph (about 1–4 sentences). No headings, no long bullet lists, no preamble.
        - Be concrete and useful. If the user is asking about an image they made, look at the image and its metadata, point at the most likely fix first, and only mention secondary suggestions if they're clearly worth it.
        - When citing a SwarmUI doc via the swarm_docs tool, mention the doc name briefly (e.g. "the Prompt Syntax doc covers this") instead of a full source citation footer.
        - When suggesting parameter changes, state the current value and the recommended one ("CFG 2 → try 4–6") so the user can act on it immediately.
        - If you genuinely don't know, say so in one line — don't invent details.

        # Tools
        Prefer fast tools (swarm_docs read, memory) over slow ones in companion mode. Don't kick off image generation unless the user explicitly asks ("make it" / "generate"). Avoid long agentic chains — one or two tool calls at most before answering.
        """;
}
