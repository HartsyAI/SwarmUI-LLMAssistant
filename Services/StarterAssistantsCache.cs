using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Loads the bundled <c>Assets/starter-assistants.json</c> once on first use and caches
/// the parsed array forever. The file ships with the extension and is read-only baseline data —
/// no per-user state, no need for invalidation. If the file is missing or malformed we log and
/// return an empty array (the editor's blank-create flow still works).</summary>
public static class StarterAssistantsCache
{
    private static JArray _cached;
    private static readonly object _lock = new();

    public static JArray GetTemplates()
    {
        if (_cached is not null)
        {
            return _cached;
        }
        lock (_lock)
        {
            if (_cached is not null)
            {
                return _cached;
            }
            _cached = LoadFromDisk();
            return _cached;
        }
    }

    private static JArray LoadFromDisk()
    {
        try
        {
            // The extension's directory is set in OnPreInit; this file lives in Assets/.
            string path = Path.Combine("src", "Extensions", "SwarmUI-LLMAssistant", "Assets", "starter-assistants.json");
            if (!File.Exists(path))
            {
                Logs.Warning($"[LLMAssistant] starter-assistants.json not found at {path}");
                return [];
            }
            string raw = File.ReadAllText(path);
            JObject root = JObject.Parse(raw);
            JArray templates = root["templates"] as JArray ?? [];
            return templates;
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Failed to load starter-assistants.json: {ex.Message}");
            return [];
        }
    }
}
