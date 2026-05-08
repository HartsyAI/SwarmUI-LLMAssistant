using Newtonsoft.Json.Linq;
using SwarmUI.LLMs;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>Extends SwarmUI's LLMParamInput with richer generation parameters.
/// Per-message media now lives on <see cref="LLMMessage.Media"/> (in core) so backends can
/// emit multimodal content blocks message-by-message; this class no longer carries a flat
/// media list.</summary>
public class ExtendedLLMInput : LLMParamInput
{
    /// <summary>Tools available to the LLM for this request (prompt-injected for local models).</summary>
    public List<JObject> Tools { get; set; } = [];

    /// <summary>Creates an ExtendedLLMInput from a user message and optional system prompt.</summary>
    public static ExtendedLLMInput Create(string userMessage, string systemPrompt = null, string model = null)
    {
        ExtendedLLMInput input = new()
        {
            UserMessage = userMessage,
            Model = model,
            SystemPrompt = systemPrompt
        };
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            input.Messages.Add(new LLMMessage() { Role = LLMRoles.System, Content = systemPrompt });
        }
        input.Messages.Add(new LLMMessage() { Role = LLMRoles.User, Content = userMessage });
        return input;
    }

    /// <summary>Creates an ExtendedLLMInput from a full conversation history.</summary>
    public static ExtendedLLMInput CreateFromHistory(List<ChatMessageData> messages, string systemPrompt = null, string model = null)
    {
        ExtendedLLMInput input = new()
        {
            Model = model,
            SystemPrompt = systemPrompt
        };
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            input.Messages.Add(new LLMMessage() { Role = LLMRoles.System, Content = systemPrompt });
        }
        foreach (ChatMessageData msg in messages)
        {
            string llmRole = msg.Role.ToLowerInvariant() switch
            {
                Roles.User => LLMRoles.User,
                Roles.Assistant => LLMRoles.Assistant,
                Roles.System => LLMRoles.System,
                _ => LLMRoles.User
            };
            input.Messages.Add(new LLMMessage()
            {
                Role = llmRole,
                Content = msg.Content,
                // Convert URL-shaped attachments (eg local Output paths) to base64 so backends
                // can ship them inline. External HTTPS URLs pass through untouched.
                Media = Services.MediaResolver.ResolveForLLM(msg.Media)
            });
        }
        if (messages.Count > 0)
        {
            input.UserMessage = messages[^1].Content;
        }
        return input;
    }
}

/// <summary>A single message in a conversation, used by the chat endpoint when reconstructing
/// LLM input from saved thread history.</summary>
public class ChatMessageData
{
    public string Role { get; set; }
    public string Content { get; set; }
    public string Timestamp { get; set; }
    public string Id { get; set; }
    /// <summary>Media URLs persisted on the saved message (eg user-attached images). Built into
    /// <see cref="LLMMessage.Media"/> when the chat endpoint constructs the LLM input.</summary>
    public List<LLMMediaAttachment> Media { get; set; }
}
