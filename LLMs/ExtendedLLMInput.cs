using Newtonsoft.Json.Linq;
using SwarmUI.LLMs;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>Extends SwarmUI's LLMParamInput with richer generation parameters.</summary>
public class ExtendedLLMInput : LLMParamInput
{
    /// <summary>Media attachments for vision requests.</summary>
    public List<MediaAttachment> Media { get; set; }

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
            input.Messages.Add(new LLMMessage() { Role = llmRole, Content = msg.Content });
        }
        if (messages.Count > 0)
        {
            input.UserMessage = messages[^1].Content;
        }
        return input;
    }
}

/// <summary>A single message in a conversation.</summary>
public class ChatMessageData
{
    public string Role { get; set; }
    public string Content { get; set; }
    public string Timestamp { get; set; }
    public string Id { get; set; }
}

/// <summary>Media attachment for vision requests.</summary>
public class MediaAttachment
{
    /// <summary>"base64" or "url".</summary>
    public string Type { get; set; }
    public string Data { get; set; }
    /// <summary>MIME type, e.g. "image/jpeg".</summary>
    public string MediaType { get; set; }
}
