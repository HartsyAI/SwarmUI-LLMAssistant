using Newtonsoft.Json.Linq;
using Hartsy.Extensions.LLMAssistant.LLMs;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Rules for the actions a user takes on one message of a chat. Storage stays in <see cref="ThreadStorageService"/>.</summary>
public static class MessageActions
{
    /// <summary>The message of a chat with this id, on any branch, or null.</summary>
    public static JObject Find(JObject thread, string messageId) => (thread?["messages"] as JArray)?.OfType<JObject>().FirstOrDefault(m => m["id"]?.ToString() == messageId);

    /// <summary>Finds the message a new reply hangs under when <paramref name="messageId"/> is regenerated. For a reply that is the message before it, so the new reply is a sibling of the old one.
    /// For a user message it is that message itself: a retry of a turn that got no reply (a failed or stopped request saves none), or one more answer to it.
    /// Returns false with <paramref name="error"/> set when the message is not in the chat, or is a reply with nothing before it.</summary>
    public static bool TryResolveReplyPoint(JObject thread, string messageId, out string replyTo, out string error)
    {
        replyTo = null;
        error = null;
        JObject target = Find(thread, messageId);
        if (target is null)
        {
            error = $"Message '{messageId}' not found in thread.";
            return false;
        }
        if (target["role"]?.ToString() == Roles.User)
        {
            replyTo = messageId;
            return true;
        }
        JToken parent = target["parentId"];
        replyTo = parent is null || parent.Type == JTokenType.Null ? null : parent.ToString();
        if (string.IsNullOrEmpty(replyTo))
        {
            replyTo = null;
            error = "Cannot regenerate a message with no preceding turn.";
            return false;
        }
        return true;
    }
}
