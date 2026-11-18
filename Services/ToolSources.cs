using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Somewhere tools come from besides the stored list: a source offers tool definitions (the same shape as a stored tool, plus
/// <c>isDynamic</c> and <c>source</c>) that exist only while the source is up. The built-in tools stay in the settings layers; MCP servers are
/// the first source, and skills and sub-sessions are meant to follow. A tool a source offers runs through the handler its definition names, so
/// permission, approval, hooks, rate limits and audit apply as they do to any tool.</summary>
public interface IToolSource
{
    /// <summary>Names the source (<c>mcp</c>).</summary>
    string Id { get; }

    /// <summary>The tools this source offers <paramref name="user"/> right now. Must not block: a source that has to connect or fetch does that
    /// in the background and offers what it has.</summary>
    IEnumerable<JObject> GetTools(User user);
}

/// <summary>The registered <see cref="IToolSource"/>s, and how their tools join the user's view of the tool list.</summary>
public static class ToolSources
{
    private static readonly List<IToolSource> Sources = [];
    private static readonly object Lock = new();

    /// <summary>Adds a source. Called once at start-up.</summary>
    public static void Register(IToolSource source)
    {
        lock (Lock)
        {
            Sources.RemoveAll(s => s.Id == source.Id);
            Sources.Add(source);
        }
    }

    /// <summary>Puts every source's tools into <paramref name="tools"/> (the merged <c>tools</c> object of a user's settings), marked
    /// <c>_scope: "source"</c>. A source's tool replaces a stored one with the same id, so a stored tool cannot pose as one a source offers. A
    /// source that fails offers nothing rather than breaking the list.</summary>
    public static void AddTo(JObject tools, User user)
    {
        List<IToolSource> sources;
        lock (Lock)
        {
            sources = [.. Sources];
        }
        foreach (IToolSource source in sources)
        {
            IEnumerable<JObject> offered;
            try
            {
                offered = [.. source.GetTools(user)];
            }
            catch (Exception ex)
            {
                SwarmUI.Utils.Logs.Warning($"[LLMAssistant] Tool source '{source.Id}' failed: {ex.Message}");
                continue;
            }
            foreach (JObject tool in offered)
            {
                string id = tool["id"]?.ToString();
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                tool["_scope"] = "source";
                tools[id] = tool;
            }
        }
    }
}
