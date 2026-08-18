using System.Net.WebSockets;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Tools;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Chat message endpoints (HTTP and WebSocket streaming).</summary>
public static class ChatEndpoints
{
    /// <summary>Process-lifetime cache of non-streaming completion responses, keyed by prompt+instruction.</summary>
    private static readonly PromptCacheService Cache = new(500);

    /// <summary>Uploads an image the user just attached to chat: parses the data URI, resizes if
    /// needed (long edge capped at <see cref="MediaStorageService.MaxDimension"/>), writes to the
    /// user's per-user uploads dir, and returns the served URL. The frontend stores the URL —
    /// not the base64 — on the message so thread blobs stay slim.</summary>
    public static async Task<JObject> LLMAssistantUploadChatImage(Session session, JObject rawInput)
    {
        if (session?.User is null)
        {
            return new JObject { ["success"] = false, ["error"] = "Authentication required." };
        }
        string threadId = rawInput["threadId"]?.ToString();
        string messageId = rawInput["messageId"]?.ToString();
        string imageData = rawInput["imageData"]?.ToString();
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new JObject { ["success"] = false, ["error"] = "threadId is required." };
        }
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return new JObject { ["success"] = false, ["error"] = "messageId is required." };
        }
        if (string.IsNullOrWhiteSpace(imageData))
        {
            return new JObject { ["success"] = false, ["error"] = "imageData (data URI) is required." };
        }
        try
        {
            // Respect the user's "Vision Image Max Size" setting so uploads are downscaled before storage,
            // trimming vision-token cost on remote APIs. Falls back to the service default when unset.
            JObject settings = SettingsService.GetMergedSettings(session.User);
            int maxDim = (settings["parameters"] as JObject)?["imageMaxDimension"]?.Value<int>() ?? MediaStorageService.MaxDimension;
            MediaStorageService.StoredImage stored = await MediaStorageService.SaveDataUriAsync(
                session.User, threadId, messageId, imageData, maxDimension: maxDim);
            if (stored is null)
            {
                return new JObject { ["success"] = false, ["error"] = "Malformed data URI." };
            }
            return new JObject
            {
                ["success"] = true,
                ["url"] = stored.Url,
                ["mediaType"] = stored.MimeType,
                ["width"] = stored.Width,
                ["height"] = stored.Height,
                ["bytesWritten"] = stored.BytesWritten
            };
        }
        catch (InvalidOperationException ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Chat image upload failed for {session.User.UserID}: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = "Upload failed (see server logs)." };
        }
    }

    /// <summary>Test-runs an unsaved instruction text against the LLM with a sample user message,
    /// without persisting anything (no thread, no memory write). Used by the assistant editor's
    /// per-tab "Test" button so users can verify a persona/instruction <i>before</i> saving.
    /// <para>Variable substitution (<c>{{userName}}</c>, <c>{{currentDate}}</c>, etc.) is applied
    /// just like a real chat call, so previews honor the user's profile.</para></summary>
    public static async Task<JObject> LLMAssistantTestInstruction(Session session, JObject rawInput)
    {
        if (session?.User is null)
        {
            return new JObject { ["success"] = false, ["error"] = "Authentication required." };
        }
        string instructionText = rawInput["instructionText"]?.ToString();
        string sampleInput = rawInput["sampleInput"]?.ToString();
        string model = rawInput["model"]?.ToString();
        if (string.IsNullOrWhiteSpace(instructionText))
        {
            return new JObject { ["success"] = false, ["error"] = "instructionText is required." };
        }
        if (string.IsNullOrWhiteSpace(sampleInput))
        {
            return new JObject { ["success"] = false, ["error"] = "sampleInput is required." };
        }
        try
        {
            // Substitute the standard variables so previews accurately reflect what the model
            // sees in real chat. Use a synthetic assistant name since the assistant being edited
            // isn't saved yet.
            string assistantName = (rawInput["assistantName"]?.ToString() ?? "").Trim();
            if (string.IsNullOrEmpty(assistantName)) { assistantName = "Test Assistant"; }
            JObject syntheticAssistant = new() { ["name"] = assistantName };
            Dictionary<string, string> vars = UserProfileService.BuildPromptVariables(session.User, syntheticAssistant);
            string systemPrompt = InstructionService.SubstituteVariables(instructionText, vars);
            ExtendedLLMInput input = ExtendedLLMInput.Create(sampleInput, systemPrompt, model);
            input.RequestSession = session;
            // Apply the user's global parameter defaults (so the preview's temperature etc.
            // matches what their real chat would use).
            JObject settings = SettingsService.GetMergedSettings(session.User);
            ApplyParameters(input, settings["parameters"] as JObject, -1, -1);
            string response = await LLMDispatcher.Generate(input);
            return new JObject { ["success"] = true, ["response"] = response };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Creates a new empty chat thread for the given assistant. The frontend calls this
    /// before sending the first message in a fresh chat; subsequent messages reference the returned
    /// <c>threadId</c>. If <paramref name="assistantId"/> is empty, the user's active assistant is used.</summary>
    public static async Task<JObject> LLMAssistantCreateThread(Session session, string assistantId = null, string title = null)
    {
        if (session?.User is null)
        {
            return new JObject { ["success"] = false, ["error"] = "Authentication required." };
        }
        JObject settings = SettingsService.GetMergedSettings(session.User);
        if (string.IsNullOrEmpty(assistantId))
        {
            assistantId = AssistantService.GetActiveAssistantId(settings, session.User);
        }
        JObject thread = ThreadStorageService.CreateThread(session.User, assistantId, title);
        return new JObject
        {
            ["success"] = true,
            ["thread"] = thread
        };
    }

    /// <summary>Non-streaming completion for instruction/utility callers (eg prompt enhancement,
    /// magic vision). Does NOT touch chat threads — pass an explicit <paramref name="message"/>
    /// and you get back the raw model output. For chat use the WS endpoint with a threadId.</summary>
    public static async Task<JObject> LLMAssistantSendMessage(Session session,
        string message, string instructionId = null, string model = null,
        double temperature = -1, int maxTokens = -1, bool noCache = false,
        string assistantId = null)
    {
        try
        {
            JObject settings = SettingsService.GetMergedSettings(session.User);
            assistantId ??= AssistantService.GetActiveAssistantId(settings, session.User);
            string systemPrompt = ResolveInstructionForRequest(instructionId, assistantId, settings, session.User);
            ExtendedLLMInput input = ExtendedLLMInput.Create(message, systemPrompt, model);
            input.RequestSession = session;
            JObject resolvedParams = AssistantService.ResolveParameters(assistantId, settings, session.User);
            ApplyParameters(input, resolvedParams, temperature, maxTokens);
            string response;
            if (noCache)
            {
                response = await LLMDispatcher.Generate(input);
            }
            else
            {
                response = await Cache.GetOrCreate(message, instructionId, async () =>
                {
                    return await LLMDispatcher.Generate(input);
                });
            }
            return new JObject
            {
                ["success"] = true,
                ["response"] = response
            };
        }
        catch (Exception ex)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    /// <summary>Sends a chat message with streaming response over WebSocket. Server-authoritative:
    /// the thread is the source of truth for history. Request shape:
    /// <c>{ threadId: string (required), message: string, model?, temperature?, maxTokens?, instructionId? }</c>.
    /// The server loads the thread, appends the user message, builds the LLM input from the stored
    /// history, streams the response, and persists the assistant reply when done.</summary>
    public static async Task<JObject> LLMAssistantSendMessageWS(WebSocket socket, Session session, JObject rawInput)
    {
        try
        {
            string threadId = rawInput["threadId"]?.ToString();
            string message = rawInput["message"]?.ToString();
            // Client-generated user message id: persist under the same id the client rendered with so
            // subsequent edit/delete/fork can resolve it server-side.
            string userMessageId = rawInput["userMessageId"]?.ToString();
            if (string.IsNullOrEmpty(threadId))
            {
                return new JObject { ["success"] = false, ["error"] = "threadId is required. Call LLMAssistantCreateThread first." };
            }
            if (string.IsNullOrEmpty(message))
            {
                return new JObject { ["success"] = false, ["error"] = "message is required." };
            }
            JObject thread = ThreadStorageService.GetThread(session.User, threadId);
            if (thread is null)
            {
                return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
            }
            // Append the user message to the thread BEFORE generation so it persists even if generation
            // fails or the client disconnects mid-stream. In the branch model this hangs off the current
            // active leaf. Image attachments are persisted as URLs (uploaded earlier), never base64.
            JObject userMsg = new() { ["role"] = Roles.User, ["content"] = message };
            if (!string.IsNullOrEmpty(userMessageId))
            {
                userMsg["id"] = userMessageId;
            }
            if (rawInput["media"] is JArray mediaArr && mediaArr.Count > 0)
            {
                userMsg["media"] = mediaArr.DeepClone();
            }
            ThreadStorageService.AppendMessage(session.User, threadId, userMsg);
            // AppendMessage fills in the id when the client didn't supply one; capture it as the explicit
            // parent for compare lanes (so both replies become siblings of this exact user turn).
            string parentUserId = userMsg["id"]?.ToString();
            // Compare mode: `models` is an array of { model, device?, assistantMessageId? }. Two-or-more
            // entries fan the same prompt out to each model side-by-side. Otherwise the normal single path.
            JArray compareModels = rawInput["models"] as JArray;
            if (compareModels is not null && compareModels.Count >= 2)
            {
                await StreamCompareForThread(socket, session, threadId, parentUserId, rawInput, compareModels);
            }
            else
            {
                await StreamReplyForThread(socket, session, threadId, rawInput);
            }
            return null;
        }
        catch (Exception ex)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    /// <summary>Edits a user message into a NEW branch and streams a fresh reply. The original message and
    /// the subtree beneath it are kept as a sibling branch (switchable via the pager); the edited copy
    /// becomes the active branch. WS — same streaming protocol as <see cref="LLMAssistantSendMessageWS"/>.
    /// Request: <c>{ threadId, messageId (edited message), content, userMessageId (id for the new branch
    /// node), assistantMessageId, model?, ... }</c>.</summary>
    public static async Task<JObject> LLMAssistantEditMessageWS(WebSocket socket, Session session, JObject rawInput)
    {
        try
        {
            string threadId = rawInput["threadId"]?.ToString();
            string messageId = rawInput["messageId"]?.ToString();
            string content = rawInput["content"]?.ToString();
            string userMessageId = rawInput["userMessageId"]?.ToString();
            if (string.IsNullOrEmpty(threadId)) { return Fail("threadId is required."); }
            if (string.IsNullOrEmpty(messageId)) { return Fail("messageId is required."); }
            if (content is null) { return Fail("content is required."); }
            JObject thread = ThreadStorageService.GetThread(session.User, threadId);
            if (thread is null) { return Fail($"Chat '{threadId}' not found."); }
            JObject original = (thread["messages"] as JArray)?.OfType<JObject>().FirstOrDefault(m => m["id"]?.ToString() == messageId);
            if (original is null) { return Fail($"Message '{messageId}' not found in thread."); }
            // Build the edited sibling: same role, new content, carry the original's media.
            JObject edited = new() { ["role"] = original["role"]?.ToString() ?? Roles.User, ["content"] = content };
            if (!string.IsNullOrEmpty(userMessageId)) { edited["id"] = userMessageId; }
            if (original["media"] is JArray om && om.Count > 0) { edited["media"] = om.DeepClone(); }
            edited["editedAt"] = DateTime.UtcNow.ToString("o");
            edited["editedFrom"] = messageId;
            if (ThreadStorageService.AddBranch(session.User, threadId, messageId, edited) is null)
            {
                return Fail("Failed to create edit branch.");
            }
            await StreamReplyForThread(socket, session, threadId, rawInput);
            return null;
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Regenerates an assistant reply as a NEW sibling branch — the previous reply stays switchable
    /// via the pager — and streams it. WS. Request: <c>{ threadId, messageId (assistant reply to regen),
    /// assistantMessageId, model?, ... }</c>.</summary>
    public static async Task<JObject> LLMAssistantRegenerateWS(WebSocket socket, Session session, JObject rawInput)
    {
        try
        {
            string threadId = rawInput["threadId"]?.ToString();
            string messageId = rawInput["messageId"]?.ToString();
            if (string.IsNullOrEmpty(threadId)) { return Fail("threadId is required."); }
            if (string.IsNullOrEmpty(messageId)) { return Fail("messageId is required."); }
            JObject thread = ThreadStorageService.GetThread(session.User, threadId);
            if (thread is null) { return Fail($"Chat '{threadId}' not found."); }
            JObject target = (thread["messages"] as JArray)?.OfType<JObject>().FirstOrDefault(m => m["id"]?.ToString() == messageId);
            if (target is null) { return Fail($"Message '{messageId}' not found in thread."); }
            JToken pidTok = target["parentId"];
            string parentId = pidTok is null || pidTok.Type == JTokenType.Null ? null : pidTok.ToString();
            if (string.IsNullOrEmpty(parentId)) { return Fail("Cannot regenerate a message with no preceding turn."); }
            // Move the active leaf to the preceding turn; the streamed reply appends as a new child — a
            // sibling of the previous reply.
            if (ThreadStorageService.SetActiveLeaf(session.User, threadId, parentId) is null)
            {
                return Fail("Failed to set branch point.");
            }
            await StreamReplyForThread(socket, session, threadId, rawInput);
            return null;
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>Shared streaming tail. Resolves assistant/instructions/params/tools, builds the LLM input
    /// from the thread's ACTIVE branch, and streams the reply. Callers must have already put the thread in
    /// the desired state (user turn appended for send, edited sibling branched for edit, active leaf moved
    /// for regenerate).</summary>
    private static async Task StreamReplyForThread(WebSocket socket, Session session, string threadId, JObject rawInput)
    {
        string instructionId = rawInput["instructionId"]?.ToString();
        string assistantMessageId = rawInput["assistantMessageId"]?.ToString();
        string model = rawInput["model"]?.ToString();
        // Option B (in-chat tool picker): a forced tool id nudges the model to call exactly that tool.
        string forceToolId = rawInput["forceToolId"]?.ToString();
        double temperature = rawInput["temperature"]?.Value<double>() ?? -1;
        int maxTokens = rawInput["maxTokens"]?.Value<int>() ?? -1;
        long seed = rawInput["seed"]?.Value<long>() ?? -1;

        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            await SendWsError(socket, $"Chat '{threadId}' not found.");
            return;
        }
        // Assistant is locked to the thread (set at creation). Per-message override would make history
        // confusing — assistant identity is part of the thread's identity.
        string assistantId = thread["assistantId"]?.ToString();
        JObject settings = SettingsService.GetMergedSettings(session.User);
        if (string.IsNullOrEmpty(assistantId))
        {
            assistantId = AssistantService.GetActiveAssistantId(settings, session.User);
        }
        // Resolve model facts once so per-model instruction variants can pick the right text.
        LLMModelInfo modelInfo = await LLMModelLookup.GetByIdAsync(model);
        string systemPrompt = ResolveInstructionForRequest(instructionId, assistantId, settings, session.User, modelInfo);
        // Build LLM input from the active branch (truncated to maxContextMessages).
        List<ChatMessageData> history = BuildHistoryFromThread(thread, settings, rawInput);
        ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory(history, systemPrompt, model);
        input.RequestSession = session;
        JObject resolvedParams = AssistantService.ResolveParameters(assistantId, settings, session.User);
        ApplyParameters(input, resolvedParams, temperature, maxTokens, seed);
        // Load tools enabled for this assistant and inject their descriptions into the system prompt —
        // unless tool-calling is switched off for this thread/assistant (see EffectiveToolsEnabled).
        List<JObject> enabledTools = EffectiveToolsEnabled(thread, assistantId, settings, session.User)
            ? ToolRegistryService.GetEnabledTools(assistantId, settings, session.User)
            : [];
        await ApplyToolsToInput(input, enabledTools, session, assistantId, forceToolId);
        await LLMStreamHelper.StreamToWebSocket(socket, input, session, threadId, assistantId, clientAssistantMessageId: assistantMessageId);
        await MaybeGenerateTitleAsync(socket, session, threadId, model);
    }

    /// <summary>Claude/ChatGPT-style auto-titling: right after a chat's first exchange, asks the model
    /// for a short title and replaces the raw-first-message fallback with it. No-ops once the title has
    /// been claimed (by a prior attempt or a manual rename — see <see cref="ThreadStorageService.NeedsGeneratedTitle"/>),
    /// and never throws into the caller — a failed title generation just leaves the fallback title in place.</summary>
    private static async Task MaybeGenerateTitleAsync(WebSocket socket, Session session, string threadId, string model)
    {
        try
        {
            JObject thread = ThreadStorageService.GetThread(session.User, threadId);
            if (!ThreadStorageService.NeedsGeneratedTitle(thread))
            {
                return;
            }
            // Claim immediately (before the LLM call) so a slow/failed generation can't be retried forever
            // by the next message in the same exchange, and so it can never race a manual rename backwards.
            ThreadStorageService.MarkTitleClaimed(thread);
            List<JObject> active = ThreadStorageService.GetActivePath(thread);
            string userText = active[0]?["content"]?.ToString();
            string replyText = active[1]?["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(userText))
            {
                ThreadStorageService.SaveThread(session.User, thread);
                return;
            }
            string title = await GenerateChatTitle(userText, replyText, model);
            if (!string.IsNullOrWhiteSpace(title))
            {
                thread["title"] = title;
            }
            ThreadStorageService.SaveThread(session.User, thread);
            if (!string.IsNullOrWhiteSpace(title) && socket.State == WebSocketState.Open)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(new JObject { ["titleUpdated"] = title, ["threadId"] = threadId }.ToString(Newtonsoft.Json.Formatting.None));
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Logs.Debug($"[LLMAssistant] Auto-title generation failed for chat {threadId}: {ex.Message}");
        }
    }

    /// <summary>Asks the LLM for a short (3-6 word) chat title from the first exchange. Returns null on
    /// any failure or if the model ignores the format and returns something unusably long.</summary>
    private static async Task<string> GenerateChatTitle(string userText, string replyText, string model)
    {
        const string system = "You write extremely short titles for chat conversations, in the style of Claude.ai or ChatGPT's auto-generated chat names. Reply with ONLY the title: 3-6 words, no quotes, no trailing punctuation, no preamble like \"Title:\".";
        string prompt = $"User: {Truncate(userText, 400)}";
        if (!string.IsNullOrWhiteSpace(replyText))
        {
            prompt += $"\nAssistant: {Truncate(replyText, 400)}";
        }
        ExtendedLLMInput input = ExtendedLLMInput.Create(prompt, system, model);
        input.MaxTokens = 20;
        input.Temperature = 0.7;
        string raw = await LLMDispatcher.Generate(input);
        string cleaned = (raw ?? "").Trim().Trim('"', '\'', '.', ' ');
        // Models occasionally ignore the instruction and answer the question instead of titling it —
        // a real title is short; anything long enough to be a paragraph isn't usable, so fall back.
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length > 80 || cleaned.Contains('\n'))
        {
            return null;
        }
        return cleaned;
    }

    /// <summary>Truncates text to at most <paramref name="maxLen"/> chars, on a clean boundary.</summary>
    private static string Truncate(string text, int maxLen)
    {
        return string.IsNullOrEmpty(text) || text.Length <= maxLen ? text : text[..maxLen] + "…";
    }

    /// <summary>Whether tool-calling is active for this thread: an explicit per-thread override (set via
    /// <see cref="ThreadEndpoints.LLMAssistantSetThreadToolsEnabled"/>) wins; otherwise falls back to the
    /// resolved assistant's master switch. Callers must pass an empty tools list when this is false so
    /// <see cref="Services.ToolPromptService.BuildToolSystemPrompt"/> never runs — small local GGUF models
    /// reliably get confused by tool descriptions in the system prompt regardless of how compact they are.</summary>
    private static bool EffectiveToolsEnabled(JObject thread, string assistantId, JObject settings, User user)
    {
        if (thread["toolsEnabled"]?.Type == JTokenType.Boolean)
        {
            return thread["toolsEnabled"].Value<bool>();
        }
        return AssistantResolver.Resolve(assistantId, user, settings).ToolsEnabled;
    }

    /// <summary>Enriches the assistant's enabled tools per-user and attaches them to the request. Providers
    /// with <see cref="ILLMProvider.SupportsNativeToolCalling"/> (eg Anthropic) get <see cref="ExtendedLLMInput.Tools"/>
    /// / <see cref="ExtendedLLMInput.ForceToolId"/> and nothing else — their own native tools/tool_choice
    /// wire mechanism teaches the model, so injecting the &lt;tool_call&gt; tag convention on top would
    /// confuse it. Every other provider gets today's prompt-injected tag convention. Shared by single-model
    /// and compare streaming.</summary>
    private static async Task ApplyToolsToInput(ExtendedLLMInput input, List<JObject> enabledTools, Session session, string assistantId, string forceToolId)
    {
        if (enabledTools is null || enabledTools.Count == 0)
        {
            return;
        }
        List<JObject> enrichedTools = [];
        foreach (JObject tool in enabledTools)
        {
            ToolHandler handler = ToolRegistryService.GetHandler(tool["handlerId"]?.ToString());
            enrichedTools.Add(handler is null ? tool : handler.EnrichForUser(tool, session, assistantId));
        }
        input.Tools = enrichedTools;
        if (!string.IsNullOrEmpty(forceToolId) && enabledTools.Any(t => string.Equals(t["id"]?.ToString(), forceToolId, StringComparison.OrdinalIgnoreCase)))
        {
            input.ForceToolId = forceToolId;
        }
        ILLMProvider provider = await LLMDispatcher.GetProvider(input);
        if (provider?.SupportsNativeToolCalling == true)
        {
            return;
        }
        string toolPrompt = ToolPromptService.BuildToolSystemPrompt(enrichedTools);
        input.SystemPrompt = (input.SystemPrompt ?? "") + toolPrompt;
        if (input.Messages.Count > 0 && input.Messages[0].Role == LLMRoles.System)
        {
            input.Messages[0].Content = input.SystemPrompt;
        }
        else if (!string.IsNullOrEmpty(input.SystemPrompt))
        {
            input.Messages.Insert(0, new LLMMessage() { Role = LLMRoles.System, Content = input.SystemPrompt });
        }
        if (!string.IsNullOrEmpty(input.ForceToolId))
        {
            string directive = $"\n\nIMPORTANT: The user has explicitly requested the `{forceToolId}` tool. You MUST respond by emitting a single <tool_call> block for `{forceToolId}` with arguments derived from the user's message — do not answer in prose and do not pick a different tool.";
            input.SystemPrompt += directive;
            if (input.Messages.Count > 0 && input.Messages[0].Role == LLMRoles.System)
            {
                input.Messages[0].Content = input.SystemPrompt;
            }
        }
    }

    /// <summary>Streams two-or-more models concurrently against the same user turn (side-by-side compare).
    /// Each lane builds its own input from the SAME active-branch history, streams over one shared socket
    /// (events tagged with <c>lane</c>, writes serialized), and persists its reply as a sibling child of the
    /// user message (<paramref name="parentUserId"/>) tagged with a shared <c>groupId</c>. Lane 0 becomes the
    /// active leaf so the thread has a definite path; the user picks a winner via "Keep this one".
    /// <para>Concurrency: all lanes stream in parallel so both models load and generate at the same time.
    /// The backend is responsible for VRAM contention if two large local models don't both fit.</para></summary>
    private static async Task StreamCompareForThread(WebSocket socket, Session session, string threadId, string parentUserId, JObject rawInput, JArray modelsArr)
    {
        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            await SendWsError(socket, $"Chat '{threadId}' not found.");
            return;
        }
        string assistantId = thread["assistantId"]?.ToString();
        JObject settings = SettingsService.GetMergedSettings(session.User);
        if (string.IsNullOrEmpty(assistantId))
        {
            assistantId = AssistantService.GetActiveAssistantId(settings, session.User);
        }
        string instructionId = rawInput["instructionId"]?.ToString();
        string forceToolId = rawInput["forceToolId"]?.ToString();
        double temperature = rawInput["temperature"]?.Value<double>() ?? -1;
        int maxTokens = rawInput["maxTokens"]?.Value<int>() ?? -1;
        long seed = rawInput["seed"]?.Value<long>() ?? -1;
        // Build the active-branch history ONCE — it's identical for every lane (same prompt, same context).
        // Each lane gets its own ExtendedLLMInput (the agentic loop mutates input.Messages per-lane).
        List<ChatMessageData> baseHistory = BuildHistoryFromThread(thread, settings, rawInput);
        JObject resolvedParams = AssistantService.ResolveParameters(assistantId, settings, session.User);
        List<JObject> enabledTools = EffectiveToolsEnabled(thread, assistantId, settings, session.User)
            ? ToolRegistryService.GetEnabledTools(assistantId, settings, session.User)
            : [];
        SemaphoreSlim sendLock = new(1, 1);
        // Parse lane descriptors: each is { model, device?, assistantMessageId? }.
        List<(string Model, string Device, int BackendId, string AssistantMessageId)> lanes = [];
        foreach (JToken entry in modelsArr)
        {
            string model = entry is JObject eo ? eo["model"]?.ToString() : entry?.ToString();
            if (string.IsNullOrEmpty(model))
            {
                continue;
            }
            string device = (entry as JObject)?["device"]?.ToString();
            // Optional pinned backend instance (the lane's chosen GPU/device); -1 = any owner.
            int backendId = (entry as JObject)?["backendId"]?.Value<int?>() ?? -1;
            string amid = (entry as JObject)?["assistantMessageId"]?.ToString();
            lanes.Add((model, device, backendId, amid));
        }
        if (lanes.Count < 2)
        {
            await SendWsError(socket, "Compare mode needs at least two models.");
            return;
        }

        async Task RunLane(int lane)
        {
            (string Model, string Device, int BackendId, string AssistantMessageId) L = lanes[lane];
            try
            {
                LLMModelInfo modelInfo = await LLMModelLookup.GetByIdAsync(L.Model);
                string systemPrompt = ResolveInstructionForRequest(instructionId, assistantId, settings, session.User, modelInfo);
                ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory(baseHistory, systemPrompt, L.Model);
                input.RequestSession = session;
                input.BackendId = L.BackendId;
                input.Device = L.Device;
                ApplyParameters(input, resolvedParams, temperature, maxTokens, seed);
                await ApplyToolsToInput(input, enabledTools, session, assistantId, forceToolId);
                await LLMStreamHelper.StreamToWebSocket(socket, input, session, threadId, assistantId,
                    clientAssistantMessageId: L.AssistantMessageId,
                    lane: lane, sendLock: sendLock, parentMessageId: parentUserId,
                    compareGroupId: parentUserId, deviceLabel: L.Device, setActiveLeaf: lane == 0);
            }
            catch (Exception ex)
            {
                // Isolate a lane failure — the other lane(s) must keep streaming. Surface as a lane-tagged error.
                Logs.Error($"[LLMAssistant] Compare lane {lane} ({L.Model}) failed: {ex.Message}");
                await SendJsonLane(socket, sendLock, lane, new JObject { ["error"] = ex.Message });
            }
        }

        await Task.WhenAll(Enumerable.Range(0, lanes.Count).Select(RunLane));
    }

    /// <summary>Sends a single lane-tagged frame (used for lane-scoped errors raised outside the stream helper).</summary>
    private static async Task SendJsonLane(WebSocket socket, SemaphoreSlim sendLock, int lane, JObject data)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        data["lane"] = lane;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data.ToString(Newtonsoft.Json.Formatting.None));
        await sendLock.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>Small helper for pre-stream validation failures on the chat endpoints.</summary>
    private static JObject Fail(string error) => new() { ["success"] = false, ["error"] = error };

    /// <summary>Sends a single <c>{ "error": ... }</c> frame over the socket (mirrors the client's error
    /// handling) for failures that surface after streaming has begun.</summary>
    private static async Task SendWsError(WebSocket socket, string error)
    {
        if (socket.State == WebSocketState.Open)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(new JObject { ["error"] = error }.ToString(Newtonsoft.Json.Formatting.None));
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    /// <summary>Reads stored thread messages, applies maxContextMessages truncation.
    /// Source of truth is the saved thread — the client cannot inject fake history.
    /// Per-message <c>media</c> entries (URLs, persisted by the upload endpoint) are converted to
    /// <see cref="LLMMediaAttachment"/>s and propagated so vision-capable backends can pass them.</summary>
    private static List<ChatMessageData> BuildHistoryFromThread(JObject thread, JObject settings, JObject rawInput)
    {
        List<ChatMessageData> history = [];
        // Branch model: the LLM only ever sees the ACTIVE path (root → active leaf), never off-path siblings.
        foreach (JObject msg in ThreadStorageService.GetActivePath(thread))
        {
            ChatMessageData entry = new()
            {
                Role = msg["role"]?.ToString() ?? Roles.User,
                Content = msg["content"]?.ToString() ?? ""
            };
            if (msg["media"] is JArray mediaArr && mediaArr.Count > 0)
            {
                entry.Media = [];
                List<string> urlsForContext = [];
                foreach (JToken m in mediaArr)
                {
                    string url = m["url"]?.ToString();
                    if (string.IsNullOrEmpty(url))
                    {
                        continue;
                    }
                    entry.Media.Add(new LLMMediaAttachment
                    {
                        Type = "url",
                        Data = url,
                        MediaType = m["mediaType"]?.ToString() ?? "image/png"
                    });
                    urlsForContext.Add(url);
                }
                // Inline the URL(s) as a system-style annotation so the LLM has the path
                // accessible as text — needed for tool calls like generate_image's initImage.
                // The LLM sees the image natively via vision; this just lets it *reference*
                // the URL string when chaining tools.
                if (urlsForContext.Count > 0)
                {
                    string suffix = string.Join("\n", urlsForContext.Select(u => $"[Attached image URL: {u}]"));
                    entry.Content = string.IsNullOrEmpty(entry.Content) ? suffix : $"{entry.Content}\n\n{suffix}";
                }
            }
            history.Add(entry);
        }
        return TruncateHistory(history, settings, rawInput);
    }

    /// <summary>Resolves instruction text for a request, routing through AssistantService for
    /// canonical IDs and substituting <c>{{userName}}</c>, <c>{{userProfile}}</c>,
    /// <c>{{currentDate}}</c>, and <c>{{assistantName}}</c> from the calling user's profile.
    /// User-profile lookups are scoped strictly to <paramref name="user"/>. Pass
    /// <paramref name="modelInfo"/> to enable per-model instruction variants.</summary>
    private static string ResolveInstructionForRequest(string instructionId, string assistantId, JObject settings, User user, LLMModelInfo modelInfo = null)
    {
        if (string.IsNullOrEmpty(instructionId))
        {
            instructionId = InstructionIds.Chat;
        }
        string text;
        if (InstructionIds.All.Contains(instructionId))
        {
            // Canonical instruction IDs resolve from the assistant
            text = AssistantService.ResolveInstruction(instructionId, assistantId, settings, user, modelInfo);
        }
        else
        {
            // Custom/legacy instruction IDs fall through to InstructionService
            text = InstructionService.ResolveInstruction(instructionId, settings, user);
        }
        // Inject per-user profile variables so the model knows who it's talking to.
        // Profile reads are strictly scoped to `user` by UserProfileService.
        JObject assistant = AssistantService.GetAssistant(assistantId, settings, user);
        Dictionary<string, string> vars = UserProfileService.BuildPromptVariables(user, assistant);
        return InstructionService.SubstituteVariables(text, vars);
    }

    /// <summary>Truncates message history to the configured maxContextMessages limit.</summary>
    private static List<ChatMessageData> TruncateHistory(List<ChatMessageData> history, JObject settings, JObject rawInput)
    {
        int maxCtx = rawInput["maxContextMessages"]?.Value<int>() ?? 0;
        if (maxCtx <= 0)
        {
            maxCtx = (settings["parameters"] as JObject)?["maxContextMessages"]?.Value<int>() ?? 0;
        }
        if (maxCtx > 0 && history.Count > maxCtx)
        {
            return history.GetRange(history.Count - maxCtx, maxCtx);
        }
        return history;
    }

    /// <summary>Applies per-request parameter overrides on top of resolved parameters.
    /// <paramref name="seed"/> <c>-1</c> means "random, let the backend pick"; any non-negative
    /// value pins the seed (only providers that honor <c>ExtendedLLMInput.Seed</c> will use it).</summary>
    private static void ApplyParameters(ExtendedLLMInput input, JObject parameters, double temperature, int maxTokens, long seed = -1)
    {
        input.Temperature = temperature >= 0 ? temperature : parameters?["temperature"]?.Value<double>() ?? 1.0;
        input.MaxTokens = maxTokens >= 0 ? maxTokens : parameters?["maxTokens"]?.Value<int>() ?? 4096;
        input.TopP = parameters?["topP"]?.Value<double>() ?? 0.9;
        if (seed < 0)
        {
            // Pull a default from saved parameters if the request didn't pin one. Saved value -1 means random.
            seed = parameters?["seed"]?.Value<long>() ?? -1;
        }
        input.Seed = seed;
    }

    /// <summary>Counts tokens for a block of text or a chat history array (accepts <c>text</c> or a
    /// <c>messages</c> array, flattened with role headers). Returns an exact count when a loaded
    /// provider can tokenize cheaply, otherwise a <c>chars/4</c> heuristic; never triggers a model load.</summary>
    public static async Task<JObject> LLMAssistantCountTokens(Session session, JObject rawInput)
    {
        try
        {
            string text = rawInput["text"]?.ToString();
            if (text is null)
            {
                JArray messages = rawInput["messages"] as JArray;
                if (messages is not null)
                {
                    StringBuilder sb = new();
                    foreach (JToken msg in messages)
                    {
                        string role = msg["role"]?.ToString() ?? "user";
                        string content = msg["content"]?.ToString() ?? "";
                        sb.Append(role).Append(": ").Append(content).Append('\n');
                    }
                    text = sb.ToString();
                }
            }
            text ??= "";
            (int count, bool exact, string source) = LLMDispatcher.CountTokens(text);
            return new JObject
            {
                ["success"] = true,
                ["count"] = count,
                ["exact"] = exact,
                ["source"] = source
            };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }
}
