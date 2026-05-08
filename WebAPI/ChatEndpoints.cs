using System.Net.WebSockets;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using SwarmUI.Extensions.LLMAssistant.LLMs;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.Extensions.LLMAssistant.Tools;
using SwarmUI.LLMs;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.WebAPI;

/// <summary>Chat message endpoints (HTTP and WebSocket streaming).</summary>
public static class ChatEndpoints
{
    private static readonly PromptCacheService _cache = new(500);

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
            MediaStorageService.StoredImage stored = await MediaStorageService.SaveDataUriAsync(
                session.User, threadId, messageId, imageData);
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
                response = await _cache.GetOrCreate(message, instructionId, async () =>
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
            string instructionId = rawInput["instructionId"]?.ToString();
            string model = rawInput["model"]?.ToString();
            double temperature = rawInput["temperature"]?.Value<double>() ?? -1;
            int maxTokens = rawInput["maxTokens"]?.Value<int>() ?? -1;
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
                return new JObject { ["success"] = false, ["error"] = $"Thread '{threadId}' not found." };
            }
            // Assistant is locked to the thread (set at thread creation). Per-message override would
            // make history confusing — assistant identity is part of the thread's identity.
            string assistantId = thread["assistantId"]?.ToString();
            JObject settings = SettingsService.GetMergedSettings(session.User);
            if (string.IsNullOrEmpty(assistantId))
            {
                assistantId = AssistantService.GetActiveAssistantId(settings, session.User);
            }
            // Resolve model facts once so per-model instruction variants can pick the right text.
            // Tolerates unknown models (returns null; only Default/Exact/Glob matchers will then match).
            LLMModelInfo modelInfo = await LLMModelLookup.GetByIdAsync(model);
            string systemPrompt = ResolveInstructionForRequest(instructionId, assistantId, settings, session.User, modelInfo);
            // Append the user message to the thread BEFORE generation so it persists even if
            // generation fails or the client disconnects mid-stream. Image attachments — uploaded
            // via LLMAssistantUploadChatImage and shipped here as `media: [{ url, mediaType }]` —
            // are persisted as URLs, never base64 (the upload step already wrote them to disk).
            JObject userMsg = new() { ["role"] = Roles.User, ["content"] = message };
            if (rawInput["media"] is JArray mediaArr && mediaArr.Count > 0)
            {
                userMsg["media"] = mediaArr.DeepClone();
            }
            ThreadStorageService.AppendMessage(session.User, threadId, userMsg);
            // Reload to get the canonical (just-saved) thread for input building.
            thread = ThreadStorageService.GetThread(session.User, threadId);
            // Build LLM input from the stored thread history (truncated to maxContextMessages).
            List<ChatMessageData> history = BuildHistoryFromThread(thread, settings, rawInput);
            ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory(history, systemPrompt, model);
            input.RequestSession = session;
            JObject resolvedParams = AssistantService.ResolveParameters(assistantId, settings, session.User);
            ApplyParameters(input, resolvedParams, temperature, maxTokens);
            // Load tools enabled for this assistant and inject their descriptions into the system prompt.
            // Tool handlers may enrich their descriptions per-user (eg generate_image injecting the
            // user's presets) — apply enrichment before building the prompt.
            List<JObject> enabledTools = ToolRegistryService.GetEnabledTools(assistantId, settings, session.User);
            if (enabledTools.Count > 0)
            {
                List<JObject> enrichedTools = [];
                foreach (JObject tool in enabledTools)
                {
                    ToolHandler handler = ToolRegistryService.GetHandler(tool["handlerId"]?.ToString());
                    enrichedTools.Add(handler is null ? tool : handler.EnrichForUser(tool, session, assistantId));
                }
                input.Tools = enrichedTools;
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
            }
            await LLMStreamHelper.StreamToWebSocket(socket, input, session, threadId, assistantId);
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

    /// <summary>Reads stored thread messages, applies maxContextMessages truncation.
    /// Source of truth is the saved thread — the client cannot inject fake history.
    /// Per-message <c>media</c> entries (URLs, persisted by the upload endpoint) are converted to
    /// <see cref="LLMMediaAttachment"/>s and propagated so vision-capable backends can pass them.</summary>
    private static List<ChatMessageData> BuildHistoryFromThread(JObject thread, JObject settings, JObject rawInput)
    {
        List<ChatMessageData> history = [];
        if (thread?["messages"] is JArray messages)
        {
            foreach (JToken msg in messages)
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

    /// <summary>Applies per-request parameter overrides on top of resolved parameters.</summary>
    private static void ApplyParameters(ExtendedLLMInput input, JObject parameters, double temperature, int maxTokens)
    {
        input.Temperature = temperature >= 0 ? temperature : parameters?["temperature"]?.Value<double>() ?? 1.0;
        input.MaxTokens = maxTokens >= 0 ? maxTokens : parameters?["maxTokens"]?.Value<int>() ?? 1024;
        input.TopP = parameters?["topP"]?.Value<double>() ?? 0.9;
    }

    /// <summary>Counts tokens for a block of text or a chat history array.
    /// <para>Accepts either <c>text</c> (a single string) or <c>messages</c> (an array of
    /// <c>{role, content}</c> objects). If <c>messages</c> is provided, the array is flattened
    /// with role headers before counting.</para>
    /// <para>When a <see cref="LlamaSharpLLMBackend"/> is running with a model already loaded,
    /// the exact tokenizer is used and <c>exact=true</c> is returned. Otherwise a cheap
    /// <c>chars/4</c> heuristic is returned with <c>exact=false</c>. Calling this endpoint
    /// never loads a model — if nothing is loaded yet, the caller still gets a heuristic
    /// response immediately rather than paying a multi-second model-load cost.</para>
    /// </summary>
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
            // Prefer the running LlamaSharp backend's tokenizer when a model is already loaded.
            // Deliberately never call Load() here — tokenization should be near-instant; if the
            // model isn't loaded yet we fall back to the heuristic rather than paying load cost.
            try
            {
                LlamaSharpLLMBackend llama = Program.Backends.RunningBackendsOfType<LlamaSharpLLMBackend>().FirstOrDefault();
                if (llama?.LoadedContext is not null)
                {
                    LLama.Native.LLamaToken[] toks = llama.LoadedContext.Tokenize(text, addBos: true, special: true);
                    return new JObject
                    {
                        ["success"] = true,
                        ["count"] = toks.Length,
                        ["exact"] = true,
                        ["source"] = "llama.cpp"
                    };
                }
            }
            catch
            {
                // Fall through to heuristic on any tokenizer error.
            }
            int approx = Math.Max(0, (int)Math.Ceiling(text.Length / 4.0));
            return new JObject
            {
                ["success"] = true,
                ["count"] = approx,
                ["exact"] = false,
                ["source"] = "heuristic"
            };
        }
        catch (Exception ex)
        {
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }
}
