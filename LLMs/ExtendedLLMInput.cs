using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>The extension's self-contained LLM request shape, handed to an <see cref="ILLMProvider"/>.
/// <para>Originally subclassed SwarmUI core's <c>LLMParamInput</c>; it's now standalone so the
/// extension builds against the upstream skeleton (whose <c>LLMParamInput</c> is intentionally
/// minimal). Per-message media lives on <see cref="LLMMessage.Media"/> so providers can emit
/// multimodal content blocks message-by-message.</para></summary>
public class ExtendedLLMInput
{
    /// <summary>The most recent user message text (convenience mirror of the last user turn).</summary>
    public string UserMessage;

    /// <summary>The model id to use, or null/empty to let the dispatcher pick.</summary>
    public string Model;

    /// <summary>Effective system prompt (also mirrored into the first <see cref="Messages"/> entry).</summary>
    public string SystemPrompt;

    /// <summary>Full conversation handed to the provider, including the system message.</summary>
    public List<LLMMessage> Messages = [];

    /// <summary>Sampling temperature.</summary>
    public double Temperature = 1.0;

    /// <summary>Max response tokens.</summary>
    public int MaxTokens = 1024;

    /// <summary>Nucleus sampling cutoff.</summary>
    public double TopP = 0.9;

    /// <summary>Pinned seed, or -1 for "let the provider pick". Only honored by providers that support it.</summary>
    public long Seed = -1;

    /// <summary>Whether to stream output.</summary>
    public bool Stream = true;

    /// <summary>The originating user session (for per-user keys / permission-scoped tool runs).</summary>
    public Session RequestSession;

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
