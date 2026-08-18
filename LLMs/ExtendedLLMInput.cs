using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.LLMs;

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
    public int MaxTokens = 4096;

    /// <summary>Nucleus sampling cutoff.</summary>
    public double TopP = 0.9;

    /// <summary>Pinned seed, or -1 for "let the provider pick". Only honored by providers that support it.</summary>
    public long Seed = -1;

    /// <summary>Whether to stream output.</summary>
    public bool Stream = true;

    /// <summary>The originating user session (for per-user keys / permission-scoped tool runs).</summary>
    public Session RequestSession;

    /// <summary>Pin the request to a specific backend instance (GPU/device), or -1 to let the dispatcher
    /// pick the first backend that owns the model. Used by compare mode to route each lane to a chosen
    /// device when the same model is advertised by more than one backend.</summary>
    public int BackendId = -1;

    /// <summary>Device to run on within the chosen backend ("cpu", "cuda:0", …), or null for the backend's
    /// default. Lets one local backend serve a model on GPU or CPU per request, so compare lanes on
    /// different devices generate concurrently. Ignored by backends that don't do local device placement.</summary>
    public string Device;

    /// <summary>Tools available to the LLM for this request (prompt-injected for legacy/tag-convention
    /// providers, sent as a native <c>tools</c> field for providers with
    /// <see cref="ILLMProvider.SupportsNativeToolCalling"/>).</summary>
    public List<JObject> Tools { get; set; } = [];

    /// <summary>When set, the user explicitly requested this specific tool id — a native provider maps
    /// this to a forced <c>tool_choice</c>; a legacy provider gets an extra system-prompt directive.</summary>
    public string ForceToolId;

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
    /// <summary>The message's chat role (user/assistant/system).</summary>
    public string Role { get; set; }
    /// <summary>The message text.</summary>
    public string Content { get; set; }
    /// <summary>When the message was saved.</summary>
    public string Timestamp { get; set; }
    /// <summary>The message's persisted id within its thread.</summary>
    public string Id { get; set; }
    /// <summary>Media URLs persisted on the saved message (eg user-attached images). Built into
    /// <see cref="LLMMessage.Media"/> when the chat endpoint constructs the LLM input.</summary>
    public List<LLMMediaAttachment> Media { get; set; }
}
