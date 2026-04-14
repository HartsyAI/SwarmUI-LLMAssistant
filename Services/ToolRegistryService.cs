using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Tools;
using SwarmUI.Extensions.LLMAssistant.WebAPI;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Central registry for tool definitions (stored in settings) and executable handlers (in-memory).
///
/// <para>Multi-user model matches <see cref="AssistantService"/>: reads use the user's merged view
/// (shared ⊕ personal); writes target either the shared baseline (requires
/// <see cref="LLMAssistantAPI.PermSharedWrite"/>) or the user's personal override layer.</para>
/// </summary>
public static class ToolRegistryService
{
    private static readonly ConcurrentDictionary<string, ToolHandler> _handlers = new();

    /// <summary>Registers a handler instance. Called during extension OnInit for each built-in.</summary>
    public static void RegisterHandler(ToolHandler handler)
    {
        _handlers[handler.HandlerId] = handler;
        Logs.Debug($"[LLMAssistant] Registered tool handler: {handler.HandlerId}");
    }

    /// <summary>Gets a handler by its ID, or null if not registered.</summary>
    public static ToolHandler GetHandler(string handlerId)
    {
        if (string.IsNullOrEmpty(handlerId))
        {
            return null;
        }
        return _handlers.TryGetValue(handlerId, out ToolHandler handler) ? handler : null;
    }

    /// <summary>Returns all tools as a JArray for the UI. Includes the <c>_scope</c> marker.</summary>
    public static JArray GetToolList(JObject settings = null, User user = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        JObject tools = settings["tools"] as JObject;
        JArray result = [];
        if (tools is null)
        {
            return result;
        }
        foreach (KeyValuePair<string, JToken> kvp in tools)
        {
            if (kvp.Value is JObject obj)
            {
                result.Add(obj.DeepClone());
            }
        }
        return result;
    }

    /// <summary>Gets a single tool definition by ID from the user's merged view.</summary>
    public static JObject GetTool(string toolId, JObject settings = null, User user = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        JObject tools = settings["tools"] as JObject;
        return tools?[toolId] as JObject;
    }

    /// <summary>Returns the list of tools that are both globally enabled AND enabled on the given assistant.</summary>
    public static List<JObject> GetEnabledTools(string assistantId, JObject settings = null, User user = null)
    {
        settings ??= SettingsService.GetMergedSettings(user);
        JObject tools = settings["tools"] as JObject;
        if (tools is null)
        {
            return [];
        }
        JObject assistant = AssistantService.GetAssistant(assistantId, settings, user);
        JArray enabledIds = assistant?["enabledToolIds"] as JArray;
        HashSet<string> enabledSet = [];
        if (enabledIds is not null)
        {
            foreach (JToken t in enabledIds)
            {
                enabledSet.Add(t.ToString());
            }
        }
        List<JObject> result = [];
        foreach (KeyValuePair<string, JToken> kvp in tools)
        {
            if (kvp.Value is not JObject tool)
            {
                continue;
            }
            bool globallyEnabled = tool["enabled"]?.Value<bool>() ?? true;
            if (!globallyEnabled)
            {
                continue;
            }
            if (!enabledSet.Contains(kvp.Key))
            {
                continue;
            }
            result.Add(tool);
        }
        return result;
    }

    /// <summary>Saves (creates or updates) a tool definition into the layer identified by
    /// <paramref name="scope"/>. See <see cref="AssistantService.SaveAssistant"/> for scope semantics.
    /// <para>For built-in tools, only the description and enabled fields are editable. Built-ins live
    /// in the shared layer, so toggling their enabled state on a per-user basis is done via the
    /// personal layer's override (personal tool entry with <c>isBuiltIn=false</c> for that ID).</para>
    /// </summary>
    public static string SaveTool(JObject toolData, User user, string scope = null)
    {
        if (toolData is null)
        {
            return null;
        }
        scope = NormalizeScope(scope);
        if (scope == SettingsService.ScopeShared && !CanWriteShared(user))
        {
            return null;
        }
        string id = toolData["id"]?.ToString();
        if (string.IsNullOrEmpty(id))
        {
            id = $"tool-{Guid.NewGuid():N}";
            toolData["id"] = id;
        }
        JObject stripped = SettingsService.StripScope(toolData);
        if (scope == SettingsService.ScopeShared)
        {
            JObject shared = SettingsService.GetSettings();
            JObject tools = shared["tools"] as JObject ?? [];
            ApplyToolUpsert(tools, id, stripped);
            shared["tools"] = tools;
            SettingsService.ReplaceSharedSettings(shared);
        }
        else
        {
            JObject personal = SettingsService.GetUserSettings(user);
            JObject tools = personal["tools"] as JObject ?? [];
            // Personal-layer tools are never built-in; always store full overrides.
            stripped["updated"] = DateTime.UtcNow.ToString("o");
            if (stripped["created"] is null)
            {
                stripped["created"] = DateTime.UtcNow.ToString("o");
            }
            if (stripped["handlerType"] is null)
            {
                stripped["handlerType"] = ToolConstants.HandlerBuiltIn;
            }
            if (stripped["enabled"] is null)
            {
                stripped["enabled"] = true;
            }
            tools[id] = stripped;
            personal["tools"] = tools;
            SettingsService.ReplaceUserSettings(user, personal);
        }
        return id;
    }

    /// <summary>Applies upsert semantics to a shared tool dict, respecting built-in field protection.</summary>
    private static void ApplyToolUpsert(JObject tools, string id, JObject incoming)
    {
        if (tools[id] is JObject existing && existing["isBuiltIn"]?.Value<bool>() == true)
        {
            existing["description"] = incoming["description"] ?? existing["description"];
            existing["enabled"] = incoming["enabled"] ?? existing["enabled"];
            existing["updated"] = DateTime.UtcNow.ToString("o");
        }
        else
        {
            incoming["updated"] = DateTime.UtcNow.ToString("o");
            if (incoming["created"] is null)
            {
                incoming["created"] = DateTime.UtcNow.ToString("o");
            }
            if (incoming["handlerType"] is null)
            {
                incoming["handlerType"] = ToolConstants.HandlerBuiltIn;
            }
            if (incoming["enabled"] is null)
            {
                incoming["enabled"] = true;
            }
            tools[id] = incoming;
        }
    }

    /// <summary>Deletes a tool. Auto-detects the owning layer if <paramref name="scope"/> is null.
    /// Built-in tools cannot be deleted from the shared layer (they're part of the baseline).
    /// A user CAN shadow a built-in by creating a personal tool with the same ID, then later
    /// delete that personal override to restore the baseline.</summary>
    public static bool DeleteTool(string toolId, User user, string scope = null)
    {
        scope = NormalizeScope(scope, allowAuto: true);
        JObject personal = SettingsService.GetUserSettings(user);
        JObject personalTools = personal["tools"] as JObject;
        bool inPersonal = personalTools is not null && personalTools.ContainsKey(toolId);
        JObject shared = SettingsService.GetSettings();
        JObject sharedTools = shared["tools"] as JObject;
        bool inShared = sharedTools is not null && sharedTools.ContainsKey(toolId);
        if (scope is null)
        {
            scope = inPersonal ? SettingsService.ScopePersonal : (inShared ? SettingsService.ScopeShared : null);
        }
        if (scope is null)
        {
            return false;
        }
        if (scope == SettingsService.ScopeShared)
        {
            if (!inShared || !CanWriteShared(user))
            {
                return false;
            }
            if (sharedTools[toolId] is JObject existing && existing["isBuiltIn"]?.Value<bool>() == true)
            {
                return false;
            }
            sharedTools.Remove(toolId);
            SettingsService.ReplaceSharedSettings(shared);
            return true;
        }
        else
        {
            if (!inPersonal)
            {
                return false;
            }
            personalTools.Remove(toolId);
            SettingsService.ReplaceUserSettings(user, personal);
            return true;
        }
    }

    /// <summary>Normalizes a scope string. Null → <c>"personal"</c> unless <paramref name="allowAuto"/>.</summary>
    private static string NormalizeScope(string scope, bool allowAuto = false)
    {
        if (string.IsNullOrEmpty(scope))
        {
            return allowAuto ? null : SettingsService.ScopePersonal;
        }
        if (string.Equals(scope, SettingsService.ScopeShared, StringComparison.OrdinalIgnoreCase))
        {
            return SettingsService.ScopeShared;
        }
        return SettingsService.ScopePersonal;
    }

    /// <summary>True if the user holds <see cref="LLMAssistantAPI.PermSharedWrite"/>.</summary>
    private static bool CanWriteShared(User user)
    {
        return user is not null && user.HasPermission(LLMAssistantAPI.PermSharedWrite);
    }

    /// <summary>Builds the default tool definitions seeded on fresh installs.</summary>
    public static JObject BuildDefaultTools()
    {
        string now = DateTime.UtcNow.ToString("o");
        JObject tools = new();

        tools[ToolConstants.GenerateImage] = new JObject
        {
            ["id"] = ToolConstants.GenerateImage,
            ["name"] = "Generate Image",
            ["description"] = "Generate an image from a text prompt using SwarmUI's built-in text-to-image engine. Returns a URL to the generated image.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["prompt"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Detailed text prompt describing the image to generate."
                    },
                    ["aspect"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("square", "portrait", "landscape"),
                        ["description"] = "Aspect ratio of the image.",
                        ["default"] = "square"
                    }
                },
                ["required"] = new JArray("prompt")
            },
            ["handlerType"] = ToolConstants.HandlerBuiltIn,
            ["handlerId"] = ToolConstants.GenerateImage,
            ["enabled"] = true,
            ["isBuiltIn"] = true,
            ["created"] = now,
            ["updated"] = now
        };

        tools[ToolConstants.WebSearch] = new JObject
        {
            ["id"] = ToolConstants.WebSearch,
            ["name"] = "Web Search",
            ["description"] = "Search the web for up-to-date information. Returns a list of result snippets with titles and URLs.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["query"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Search query."
                    },
                    ["limit"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Max number of results to return (1-10).",
                        ["default"] = 5
                    }
                },
                ["required"] = new JArray("query")
            },
            ["handlerType"] = ToolConstants.HandlerBuiltIn,
            ["handlerId"] = ToolConstants.WebSearch,
            ["enabled"] = true,
            ["isBuiltIn"] = true,
            ["created"] = now,
            ["updated"] = now
        };

        tools[ToolConstants.FileRead] = new JObject
        {
            ["id"] = ToolConstants.FileRead,
            ["name"] = "Read File",
            ["description"] = "Read a text file from the user's SwarmUI data directory. Sandboxed - cannot access files outside the SwarmUI Data folder.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["path"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Relative path within SwarmUI's Data directory."
                    },
                    ["maxBytes"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Maximum number of bytes to read (default 65536).",
                        ["default"] = 65536
                    }
                },
                ["required"] = new JArray("path")
            },
            ["handlerType"] = ToolConstants.HandlerBuiltIn,
            ["handlerId"] = ToolConstants.FileRead,
            ["enabled"] = true,
            ["isBuiltIn"] = true,
            ["created"] = now,
            ["updated"] = now
        };

        tools[ToolConstants.HttpRequest] = new JObject
        {
            ["id"] = ToolConstants.HttpRequest,
            ["name"] = "HTTP Request",
            ["description"] = "Make an HTTP request to a URL (GET/POST/PUT/DELETE/HEAD/PATCH) and return the response. Supports custom headers and body. Blocks requests to private/loopback addresses for safety.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["url"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Full URL (must start with http:// or https://)."
                    },
                    ["method"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("GET", "POST", "PUT", "DELETE", "HEAD", "PATCH"),
                        ["description"] = "HTTP method.",
                        ["default"] = "GET"
                    },
                    ["headers"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "Optional map of request headers (string to string)."
                    },
                    ["body"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional request body (string). For JSON, pass a stringified JSON here and set Content-Type in headers."
                    },
                    ["maxBytes"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Maximum response bytes to return (default 262144, hard cap 2097152).",
                        ["default"] = 262144
                    },
                    ["timeoutSeconds"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Request timeout in seconds (default 30, max 120).",
                        ["default"] = 30
                    }
                },
                ["required"] = new JArray("url")
            },
            ["handlerType"] = ToolConstants.HandlerBuiltIn,
            ["handlerId"] = ToolConstants.HttpRequest,
            ["enabled"] = true,
            ["isBuiltIn"] = true,
            ["created"] = now,
            ["updated"] = now
        };

        tools[ToolConstants.MemoryWrite] = new JObject
        {
            ["id"] = ToolConstants.MemoryWrite,
            ["name"] = "Remember",
            ["description"] = "Save something important about the user to long-term memory so you'll remember it in future conversations. Use this whenever the user shares their preferred name, pronouns, a preference, what they're currently working on, or any durable fact worth recalling later. Memory is strictly per-user and private.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["category"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("preferred_name", "pronouns", "bio", "current_work", "preference", "dislike", "fact"),
                        ["description"] = "Kind of memory. 'preferred_name', 'pronouns', 'bio', and 'current_work' replace existing values. 'preference', 'dislike', and 'fact' append to a deduplicated list. Use 'fact' as the catch-all for anything that doesn't fit the other categories."
                    },
                    ["content"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "The value to save. For replacing fields, the full new value. For appending lists, a single concise statement (e.g. 'Prefers Python for scripting')."
                    }
                },
                ["required"] = new JArray("category", "content")
            },
            ["handlerType"] = ToolConstants.HandlerBuiltIn,
            ["handlerId"] = ToolConstants.MemoryWrite,
            ["enabled"] = true,
            ["isBuiltIn"] = true,
            ["created"] = now,
            ["updated"] = now
        };

        tools[ToolConstants.MemoryRead] = new JObject
        {
            ["id"] = ToolConstants.MemoryRead,
            ["name"] = "Recall Memory",
            ["description"] = "Read the calling user's full memory profile. The profile is normally already injected into your system prompt every turn, so you usually don't need this — but it's available for explicit recall or to confirm what's stored.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject()
            },
            ["handlerType"] = ToolConstants.HandlerBuiltIn,
            ["handlerId"] = ToolConstants.MemoryRead,
            ["enabled"] = true,
            ["isBuiltIn"] = true,
            ["created"] = now,
            ["updated"] = now
        };

        tools[ToolConstants.SwarmDocs] = new JObject
        {
            ["id"] = ToolConstants.SwarmDocs,
            ["name"] = "SwarmUI Docs",
            ["description"] = "Look up SwarmUI's official documentation. Sandboxed to the install's docs/ folder. Use action='list' first to discover available docs (returns all .md filenames), then action='read' with the relative path to fetch a specific document. Cite the doc you read so the user knows where the answer comes from. Use this whenever the user asks how to do something in SwarmUI — never guess from memory if a doc exists.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["action"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("list", "read"),
                        ["description"] = "'list' returns every .md doc available; 'read' returns the contents of one specific doc."
                    },
                    ["path"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Required when action='read'. Relative path inside docs/, e.g. 'Basic Usage.md' or 'Features/Prompt Syntax.md'."
                    }
                },
                ["required"] = new JArray("action")
            },
            ["handlerType"] = ToolConstants.HandlerBuiltIn,
            ["handlerId"] = ToolConstants.SwarmDocs,
            ["enabled"] = true,
            ["isBuiltIn"] = true,
            ["created"] = now,
            ["updated"] = now
        };

        tools[ToolConstants.ShellExec] = new JObject
        {
            ["id"] = ToolConstants.ShellExec,
            ["name"] = "Shell Command",
            ["description"] = "Execute a shell command on the SwarmUI host machine and return stdout, stderr, and exit code. DANGEROUS: this gives the LLM full access to the host shell. Disabled by default — enable per-assistant only when you explicitly want it.",
            ["parameters"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["command"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Shell command to run (executed via the system shell: cmd.exe on Windows, /bin/sh elsewhere)."
                    },
                    ["workingDirectory"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional working directory (relative to the SwarmUI Data folder; must stay inside it)."
                    },
                    ["timeoutSeconds"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Max seconds to wait before killing the process (default 30, max 300).",
                        ["default"] = 30
                    },
                    ["maxOutputBytes"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Max bytes of stdout+stderr to return (default 65536, hard cap 1048576).",
                        ["default"] = 65536
                    }
                },
                ["required"] = new JArray("command")
            },
            ["handlerType"] = ToolConstants.HandlerBuiltIn,
            ["handlerId"] = ToolConstants.ShellExec,
            ["enabled"] = false,
            ["isBuiltIn"] = true,
            ["created"] = now,
            ["updated"] = now
        };

        return tools;
    }
}
