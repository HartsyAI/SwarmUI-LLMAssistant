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

    /// <summary>Sets the model's chat-template <c>enable_thinking</c> variable (Qwen3-family reasoning-block
    /// toggle), mirroring <see cref="HartsyInference.Engine.Requests.TextRequest.EnableThinking"/> exactly:
    /// null leaves it undefined so the template falls back to its own default — today's behavior for every
    /// existing caller, none of which set this. Only <see cref="Backends.HartsyLocalLLMProvider"/> currently
    /// honors it; ignored by a provider/template without a thinking slot.</summary>
    public bool? EnableThinking;

    /// <summary>Which conversation this request continues (eg <c>thread:{threadId}</c>), so a provider that keeps
    /// per-conversation state can find it again next turn; null (the default) for one-off calls such as titles,
    /// prompt enhancement or a tool's own caption request. Set only by the conversational routes in
    /// <see cref="WebAPI.ChatEndpoints"/>. Only <see cref="Backends.HartsyLocalLLMProvider"/> reads it, to key the
    /// engine's prefix-KV reuse (scoped there by the calling user and the model); every other provider ignores it
    /// and never sends it anywhere.</summary>
    public string ConversationId;

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
            if (llmRole == LLMRoles.Assistant && msg.ToolCalls is { Count: > 0 } calls)
            {
                AddToolTurns(input, msg, calls);
                continue;
            }
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

    /// <summary>Replays a saved assistant turn that made tool calls: the calls as an assistant turn, one tool result per call
    /// (cut to <see cref="ToolConstants.MaxReplayedToolResultChars"/>, and a stand-in when none was recorded), then the prose.</summary>
    private static void AddToolTurns(ExtendedLLMInput input, ChatMessageData msg, List<JObject> calls)
    {
        input.Messages.Add(new LLMMessage()
        {
            Role = LLMRoles.Assistant,
            Content = "",
            ToolCalls = [.. calls.Select(c => new JObject
            {
                ["id"] = c["id"]?.ToString() ?? "",
                ["name"] = c["name"]?.ToString() ?? "",
                ["arguments"] = LLMMessageMapping.ArgumentsObject(c["arguments"]),
            })],
        });
        foreach (JObject call in calls)
        {
            string text = call["result"] is { Type: not JTokenType.Null } result
                ? result.Type == JTokenType.String ? result.ToString() : result.ToString(Newtonsoft.Json.Formatting.None)
                : "{\"success\":false,\"error\":\"no result recorded\"}";
            if (text.Length > ToolConstants.MaxReplayedToolResultChars)
            {
                text = text[..ToolConstants.MaxReplayedToolResultChars] + "...[truncated]";
            }
            input.Messages.Add(new LLMMessage()
            {
                Role = LLMRoles.Tool,
                Content = text,
                ToolCallId = call["id"]?.ToString() ?? "",
                Name = call["name"]?.ToString() ?? "",
            });
        }
        if (!string.IsNullOrWhiteSpace(msg.Content))
        {
            input.Messages.Add(new LLMMessage() { Role = LLMRoles.Assistant, Content = msg.Content });
        }
    }

    /// <summary>Creates an ExtendedLLMInput from an already-parsed conversation (eg
    /// <see cref="Hartsy.Extensions.LLMAssistant.WebAPI.ChatEndpoints.LLMAssistantVoiceTurnWS"/>'s optional
    /// <c>messages</c> request field, oldest first). Prepends <paramref name="systemPrompt"/> as a new leading
    /// system message unless <paramref name="messages"/> already opens with one — the same rule
    /// <c>HartsyInference.LLM.Generation.PromptBuilder.WithSystemPrompt</c> applies at the engine's own
    /// prompt-build layer, kept here too so this type's own invariant (see <see cref="SystemPrompt"/>'s doc:
    /// mirrored into <see cref="Messages"/>[0]) never ends up carrying two system turns into
    /// <see cref="Backends.HartsyLocalLLMProvider.BuildRequestAsync"/>, which trusts that invariant completely
    /// (it never sets <c>TextRequest.SystemPrompt</c>, on purpose — see that method's own comment). When
    /// <paramref name="messages"/> already opens with a system turn, <em>that turn's own content</em> — not
    /// <paramref name="systemPrompt"/> — is what gets mirrored into <see cref="SystemPrompt"/>, since the
    /// caller's own system message is what will actually reach the model.</summary>
    public static ExtendedLLMInput CreateFromMessages(List<LLMMessage> messages, string systemPrompt = null, string model = null)
    {
        List<LLMMessage> effective = messages is { Count: > 0 } ? [.. messages] : [];
        bool opensWithSystem = effective.Count > 0 && effective[0].Role == LLMRoles.System;
        if (!opensWithSystem && !string.IsNullOrEmpty(systemPrompt))
        {
            effective.Insert(0, new LLMMessage() { Role = LLMRoles.System, Content = systemPrompt });
        }
        return new ExtendedLLMInput()
        {
            Model = model,
            SystemPrompt = opensWithSystem ? effective[0].Content : systemPrompt,
            Messages = effective,
            // The last USER turn, not messages[^1] -- the conversation's own last entry may be a tool result or
            // an assistant turn (eg a client replaying history right after an earlier native tool call).
            UserMessage = effective.LastOrDefault(m => m.Role == LLMRoles.User)?.Content
        };
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
    /// <summary>The message's persisted id within its thread.</summary>
    public string Id { get; set; }
    /// <summary>Media URLs persisted on the saved message (eg user-attached images). Built into
    /// <see cref="LLMMessage.Media"/> when the chat endpoint constructs the LLM input.</summary>
    public List<LLMMediaAttachment> Media { get; set; }
    /// <summary>Tool calls an assistant message made, each <c>{id, name, arguments, result}</c>; null for a plain turn.</summary>
    public List<JObject> ToolCalls { get; set; }
}
