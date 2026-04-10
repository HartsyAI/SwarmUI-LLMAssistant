using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using SwarmUI.Extensions.LLMAssistant.Tools;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Central registry for tool definitions (stored in settings) and executable handlers (in-memory).</summary>
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

    /// <summary>Returns all tools as a JArray for the UI.</summary>
    public static JArray GetToolList(JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
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

    /// <summary>Gets a single tool definition by ID.</summary>
    public static JObject GetTool(string toolId, JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
        JObject tools = settings["tools"] as JObject;
        return tools?[toolId] as JObject;
    }

    /// <summary>Returns the list of tools that are both globally enabled AND enabled on the given assistant.</summary>
    public static List<JObject> GetEnabledTools(string assistantId, JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
        JObject tools = settings["tools"] as JObject;
        if (tools is null)
        {
            return [];
        }
        JObject assistant = AssistantService.GetAssistant(assistantId, settings);
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

    /// <summary>Saves (creates or updates) a tool definition.</summary>
    public static string SaveTool(JObject toolData, JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
        JObject tools = settings["tools"] as JObject ?? new JObject();
        string id = toolData["id"]?.ToString();
        if (string.IsNullOrEmpty(id))
        {
            id = $"tool-{Guid.NewGuid():N}";
            toolData["id"] = id;
        }
        // For built-ins, protect core fields (only allow editing description + enabled)
        if (tools[id] is JObject existing && existing["isBuiltIn"]?.Value<bool>() == true)
        {
            existing["description"] = toolData["description"] ?? existing["description"];
            existing["enabled"] = toolData["enabled"] ?? existing["enabled"];
            existing["updated"] = DateTime.UtcNow.ToString("o");
        }
        else
        {
            toolData["updated"] = DateTime.UtcNow.ToString("o");
            if (toolData["created"] is null)
            {
                toolData["created"] = DateTime.UtcNow.ToString("o");
            }
            if (toolData["handlerType"] is null)
            {
                toolData["handlerType"] = ToolConstants.HandlerBuiltIn;
            }
            if (toolData["enabled"] is null)
            {
                toolData["enabled"] = true;
            }
            tools[id] = toolData;
        }
        settings["tools"] = tools;
        SettingsService.SaveSettings(settings);
        return id;
    }

    /// <summary>Deletes a custom tool. Built-in tools cannot be deleted.</summary>
    public static bool DeleteTool(string toolId, JObject settings = null)
    {
        settings ??= SettingsService.GetSettings();
        JObject tools = settings["tools"] as JObject;
        if (tools is null || !tools.ContainsKey(toolId))
        {
            return false;
        }
        if (tools[toolId] is JObject existing && existing["isBuiltIn"]?.Value<bool>() == true)
        {
            return false;
        }
        tools.Remove(toolId);
        settings["tools"] = tools;
        SettingsService.SaveSettings(settings);
        return true;
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

        return tools;
    }
}
