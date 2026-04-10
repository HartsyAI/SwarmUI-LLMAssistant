using LLama.Common;
using Newtonsoft.Json.Linq;
using SwarmUI.LLMs;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>Extends SwarmUI's LLMParamInput with richer generation parameters.</summary>
public class ExtendedLLMInput : LLMParamInput
{
    /// <summary>System prompt / instruction to prepend to the conversation.</summary>
    public string SystemPrompt { get; set; }

    /// <summary>Controls randomness. 0.0 = deterministic, 2.0 = very creative.</summary>
    public double Temperature { get; set; } = 1.0;

    /// <summary>Maximum number of tokens to generate.</summary>
    public int MaxTokens { get; set; } = 1024;

    /// <summary>Nucleus sampling cutoff (0.0-1.0).</summary>
    public double TopP { get; set; } = 0.9;

    /// <summary>Random seed. -1 = random.</summary>
    public long Seed { get; set; } = -1;

    /// <summary>Whether to stream the response token-by-token.</summary>
    public bool Stream { get; set; } = true;

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
            SystemPrompt = systemPrompt,
            ChatHistory = new ChatHistory()
        };
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            input.ChatHistory.AddMessage(AuthorRole.System, systemPrompt);
        }
        input.ChatHistory.AddMessage(AuthorRole.User, userMessage);
        return input;
    }

    /// <summary>Creates an ExtendedLLMInput from a full conversation history.</summary>
    public static ExtendedLLMInput CreateFromHistory(List<ChatMessageData> messages, string systemPrompt = null, string model = null)
    {
        ExtendedLLMInput input = new()
        {
            Model = model,
            SystemPrompt = systemPrompt,
            ChatHistory = new ChatHistory()
        };
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            input.ChatHistory.AddMessage(AuthorRole.System, systemPrompt);
        }
        foreach (ChatMessageData msg in messages)
        {
            AuthorRole role = msg.Role.ToLowerInvariant() switch
            {
                Roles.User => AuthorRole.User,
                Roles.Assistant => AuthorRole.Assistant,
                Roles.System => AuthorRole.System,
                _ => AuthorRole.User
            };
            input.ChatHistory.AddMessage(role, msg.Content);
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
