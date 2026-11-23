using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.ChatLibrary;

/// <summary>The order the chat list comes in. Pure.</summary>
public static class ThreadOrdering
{
    /// <summary>Whether a thread (or its row in the index) is pinned.</summary>
    public static bool IsPinned(JToken thread)
    {
        return thread?["pinned"]?.Type == JTokenType.Boolean && thread["pinned"].Value<bool>();
    }

    /// <summary>Pinned chats first, then the rest, each part keeping the order it was in (the index is newest first, so pinned chats are newest first among themselves).
    /// A sub-session is listed under the chat that started it, so only chats that are not sessions rise to the top; a session's own pin (none can be set) is ignored.</summary>
    public static JArray PinnedFirst(JArray index)
    {
        List<JToken> pinned = [];
        List<JToken> rest = [];
        foreach (JToken row in index)
        {
            bool isSession = row?[Sessions.SessionFields.Parent] is { Type: not JTokenType.Null } parent && !string.IsNullOrEmpty(parent.ToString());
            if (IsPinned(row) && !isSession)
            {
                pinned.Add(row);
            }
            else
            {
                rest.Add(row);
            }
        }
        return new JArray(pinned.Concat(rest));
    }
}
