using System.Net.WebSockets;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Core;
using Hartsy.Extensions.LLMAssistant.Backends;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Tools;
using HartsyInference.Engine.Requests;
using HartsyInference.Tools;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>Chat message endpoints (HTTP and WebSocket streaming).</summary>
public static class ChatEndpoints
{
    /// <summary>Process-lifetime cache of non-streaming completion responses, keyed by prompt+instruction.</summary>
    private static readonly PromptCacheService Cache = new(500);

    /// <summary>Uploads an image the user just attached to chat: parses the data URI, resizes if
    /// needed (long edge capped at <see cref="MediaStorageService.MaxDimension"/>), writes to the
    /// user's per-user uploads dir, and returns the served URL. The frontend stores the URL,
    /// not the base64, on the message so thread blobs stay slim.</summary>
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
    /// magic vision). Does NOT touch chat threads; pass an explicit <paramref name="message"/>
    /// and you get back the raw model output. For chat use the WS endpoint with a threadId. <paramref name="backendId"/> picks one of
    /// several backends serving the same model; <paramref name="device"/> overrides the backend's device (eg <c>cuda:0+cuda:1</c>).
    /// Both are part of the cache key, as is the resolved loop count (<paramref name="loops"/>, else the saved <c>loops</c>
    /// parameter; recurrent-block passes for a looped model such as Dory, ignored by other models), since a looped model's reply
    /// changes with it. <paramref name="enableThinking"/> (<c>true</c>/<c>false</c>; anything else leaves the template's default, which is
    /// on for Dory and Qwen3) sets the chat template's reasoning switch, as on the voice route, and keys the cache too.
    /// <para>Reply (<see cref="OneShotReply"/>): <c>{ success, response, reasoning, finish_reason }</c>. <c>response</c> is the answer alone,
    /// <c>reasoning</c> what the model wrote before it (empty when it wrote none), <c>finish_reason</c> <c>stop</c> or <c>length</c>: a
    /// budget that runs out mid-reasoning is a success with the partial reasoning, an empty <c>response</c> and <c>length</c>. A failure is
    /// <c>{ success: false, error, finish_reason: "error" }</c>.</para></summary>
    public static async Task<JObject> LLMAssistantSendMessage(Session session,
        string message, string instructionId = null, string model = null,
        double temperature = -1, int maxTokens = -1, bool noCache = false,
        string assistantId = null, string device = null, int backendId = -1, int loops = 0, string enableThinking = null)
    {
        try
        {
            JObject settings = SettingsService.GetMergedSettings(session.User);
            assistantId ??= AssistantService.GetActiveAssistantId(settings, session.User);
            string systemPrompt = ResolveInstructionForRequest(instructionId, assistantId, settings, session.User);
            ExtendedLLMInput input = ExtendedLLMInput.Create(message, systemPrompt, model);
            input.RequestSession = session;
            input.BackendId = backendId;
            if (!string.IsNullOrWhiteSpace(device))
            {
                input.Device = device;
            }
            JObject resolvedParams = AssistantService.ResolveParameters(assistantId, settings, session.User);
            ApplyParameters(input, resolvedParams, temperature, maxTokens, loops: loops);
            input.EnableThinking = ReadOptionalBool(enableThinking);
            LLMGenerateResult result;
            if (noCache)
            {
                result = await LLMDispatcher.GenerateDetailed(input);
            }
            else
            {
                // The token budget is in the key too: a reply cut off by a small budget must not answer a request with a larger one.
                string cacheModel = $"detailed|{model}|{device}|{backendId}|{input.Loops ?? 0}|{input.EnableThinking?.ToString() ?? "default"}|{input.MaxTokens}";
                string cached = await Cache.GetOrCreate(session.User?.UserID, cacheModel, assistantId, message, instructionId, async () =>
                {
                    return (await LLMDispatcher.GenerateDetailed(input)).ToJson();
                });
                result = LLMGenerateResult.FromJson(cached);
            }
            return OneShotReply(result);
        }
        catch (Exception ex)
        {
            return OneShotError(ex.Message);
        }
    }

    /// <summary>The success reply of <see cref="LLMAssistantSendMessage"/>.</summary>
    internal static JObject OneShotReply(LLMGenerateResult result) => new()
    {
        ["success"] = true,
        ["response"] = result.Response ?? "",
        ["reasoning"] = result.Reasoning ?? "",
        ["finish_reason"] = result.FinishReason ?? LLMGenerateResult.Stop
    };

    /// <summary>The failure reply of <see cref="LLMAssistantSendMessage"/>.</summary>
    internal static JObject OneShotError(string message) => new()
    {
        ["success"] = false,
        ["error"] = message,
        ["finish_reason"] = LLMGenerateResult.Error
    };

    /// <summary>One-shot conversational turn <b>with tool calling</b>, for a voice satellite: text in, spoken
    /// reply plus any device actions out.
    ///
    /// <para>Exists because the two existing paths each miss half of what a device needs.
    /// <see cref="LLMAssistantSendMessage"/> is one-shot but has no tool loop, so the assistant can talk and
    /// nothing else. <c>LLMAssistantSendMessageWS</c> has the tool loop but is a WebSocket carrying incremental
    /// frames and requires a stored thread; the wrong shape for a microcontroller that wants one request and
    /// one answer.</para>
    ///
    /// <para>Device actions (<c>set_led_profile</c>, <c>set_volume</c>, <c>mute_mic</c>) execute as no-ops
    /// server-side and come back in <c>toolCalls</c> for the caller to run against its own hardware; see
    /// <see cref="Tools.BuiltIn.DeviceActionTool"/>. Server-side tools still run normally here, so the same turn
    /// can search the web and dim the lights.</para>
    ///
    /// <para>Stateless like <see cref="LLMAssistantSendMessage"/>; no thread is loaded or written. Unlike it,
    /// nothing is cached: a voice assistant asked the same thing twice should answer twice, not replay one
    /// canned reply. The local engine still reuses the system prompt and tool definitions across calls (see
    /// <see cref="VoiceTurnConversationId"/>); reused KV, never a reused reply.</para>
    ///
    /// <para>Request: <c>{ message (required), assistantId?, model?, temperature?, maxTokens?, loops? }</c> (<c>loops</c> as on
    /// <see cref="LLMAssistantVoiceTurnWS"/>). Response: <c>{ success, response, toolCalls: [{ name, arguments }], truncated? }</c>.</para></summary>
    public static async Task<JObject> LLMAssistantVoiceTurn(Session session,
        string message, string assistantId = null, string model = null,
        double temperature = -1, int maxTokens = -1, int loops = 0)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return new JObject { ["success"] = false, ["error"] = "message is required." };
            }
            JObject settings = SettingsService.GetMergedSettings(session.User);
            assistantId = ResolveAssistantId(assistantId, () => AssistantService.GetActiveAssistantId(settings, session.User));
            // A voice satellite sends an utterance and nothing else; there is no model picker on a device
            // with no screen. Left unresolved, the empty id reaches the provider and comes back as
            // "LLM model '' not found in the LLM model folder(s)", which reads like a missing file rather
            // than a missing request field. Verified on hardware 2026-09-05: this was the whole failure.
            if (string.IsNullOrWhiteSpace(model))
            {
                LLMModelInfo fallback = await LLMModelLookup.GetFirstAvailableAsync();
                if (fallback is null)
                {
                    return new JObject
                    {
                        ["success"] = false,
                        ["error"] = "No LLM model is available. Add a backend under Server > Backends and "
                            + "make sure it advertises at least one model."
                    };
                }
                model = fallback.Id;
            }
            string systemPrompt = ResolveInstructionForRequest(InstructionIds.Chat, assistantId, settings, session.User);
            ExtendedLLMInput input = ExtendedLLMInput.Create(message, systemPrompt, model);
            input.RequestSession = session;
            input.ConversationId = VoiceTurnConversationId(assistantId);
            ApplyParameters(input, AssistantService.ResolveParameters(assistantId, settings, session.User), temperature, maxTokens, loops: loops);

            List<JObject> enabledTools = AssistantResolver.Resolve(assistantId, session.User, settings).ToolsEnabled
                ? ToolRegistryService.GetEnabledTools(assistantId, settings, session.User)
                : [];
            AttachTools(input, enabledTools, session, assistantId, forceToolId: null);

            CollectingSink collected = new();
            ToolTurnOutcome outcome = await ToolTurnRunner.RunAsync(input, new ToolTurnContext { Session = session, AssistantId = assistantId }, collected);
            string spokenText = (outcome.SpokenText ?? outcome.FullText).Trim();
            JArray deviceCalls = VoiceWsSink.DeviceCalls(outcome.ToolEvents);
            bool truncated = outcome.Truncated;
            if (VoiceTurnFailure(outcome, model, spokenText, deviceCalls) is JObject failure)
            {
                Logs.Error($"[LLMAssistant] Voice turn failed: {failure["error"]}");
                return failure;
            }
            JObject response = new()
            {
                ["success"] = true,
                ["response"] = spokenText,
                ["toolCalls"] = deviceCalls
            };
            if (truncated)
            {
                // The device still gets whatever was said and done; it just did not reach a natural end.
                response["truncated"] = true;
                response["reason"] = "max_iterations";
            }
            return response;
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Voice turn failed: {ex.Message}");
            return new JObject { ["success"] = false, ["error"] = ex.Message };
        }
    }

    /// <summary>The failure reply of <see cref="LLMAssistantVoiceTurn"/> for a finished turn, or null when it succeeded. An engine error keeps
    /// its message, as the one-shot route reports it. A turn with neither speech nor a device action is a failure too, not an empty success: a
    /// voice device would otherwise speak silence and log nothing. A tool-only turn ("turn the ring blue") has no speech and still succeeds.</summary>
    internal static JObject VoiceTurnFailure(ToolTurnOutcome outcome, string model, string spokenText, JArray deviceCalls)
    {
        if (outcome.StopReason == "error")
        {
            string reason = string.IsNullOrWhiteSpace(outcome.Error) ? "the backend reported a failure without a message" : outcome.Error;
            return new JObject { ["success"] = false, ["error"] = $"The model '{model}' stopped with an error: {reason}" };
        }
        if (spokenText.Length == 0 && deviceCalls.Count == 0)
        {
            return new JObject
            {
                ["success"] = false,
                ["error"] = "The model produced no reply. This is usually a backend that cannot run "
                    + "on its configured device; check Server > Backends and the server log."
            };
        }
        return null;
    }

    /// <summary>Streaming variant of <see cref="LLMAssistantVoiceTurn"/>: runs the same <see cref="ToolTurnRunner"/> turn as the chat routes,
    /// on any provider, and forwards it as frames (<see cref="VoiceWsSink"/>) instead of returning one accumulated response.
    ///
    /// <para>Stateless like <see cref="LLMAssistantVoiceTurn"/>: no thread is loaded or written, and device actions (<c>set_led_profile</c>,
    /// etc.) execute as no-ops server-side and come back in the final <c>done</c> frame's <c>toolCalls</c> for the caller to run against its
    /// own hardware, exactly as the one-shot route returns them.</para>
    ///
    /// <para>Every turn is its own WebSocket connection (one request frame in, frames out, then closed), so the socket cannot identify a
    /// conversation across turns. The optional <c>conversationId</c> does, when the client sends one; otherwise the caller's SwarmUI session
    /// does (see <see cref="VoiceTurnWsConversationId"/>). Either way it only scopes the local engine's prefix-KV reuse; nothing is stored
    /// here.</para>
    ///
    /// <para>Request: <c>{ message?|messages?, assistantId?, model?, temperature?, maxTokens?, enableThinking?, loops?, conversationId? }</c>.
    /// At least one of <c>message</c> (a single new user turn) or <c>messages</c> (the full conversation so far, oldest first, as
    /// <c>[{role: system|user|assistant|tool, content, toolCallId?, name?, toolCalls?}, ...]</c>) is required; <c>messages</c> takes priority
    /// when both are present. The assistant's resolved system prompt is prepended as a leading system turn unless <c>messages</c> already
    /// opens with one (see <see cref="ExtendedLLMInput.CreateFromMessages"/>). <c>enableThinking</c> maps straight onto
    /// <see cref="TextRequest.EnableThinking"/>; omitted, the model's template keeps its own default. Frames: <c>{chunk:"..."}</c>*,
    /// <c>{native_tool_call:{id,name,arguments}}</c> then <c>{tool_result:{id,name,result}}</c> per call that was run (a call that is never
    /// run is never sent), a round-limit <c>{notice:"..."}</c> when the tool loop stopped with a call still unrun, then one final
    /// <c>{done:true, full_text, toolCalls:[{name,arguments}], stopReason?}</c>, or <c>{error:"..."}</c> at any point, which ends the stream
    /// without a <c>done</c> frame.</para></summary>
    public static async Task<JObject> LLMAssistantVoiceTurnWS(WebSocket socket, Session session, JObject rawInput)
    {
        async Task SendAsync(JObject payload)
        {
            if (socket.State == WebSocketState.Open)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None));
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        // Tied to the socket's own lifetime, not just process shutdown: this route forwards every frame as
        // it streams, including through an in-flight ToolLoop round and whatever tool handler is running
        // (shell/http tools have no timeout of their own) -- a disconnect must cancel that, not just stop
        // this method from sending into a dead socket.
        using CancellationTokenSource turnCancel = CancellationTokenSource.CreateLinkedTokenSource(Program.GlobalProgramCancel);
        _ = WatchForDisconnectAsync(socket, () =>
        {
            // The turn may already be over and this disposed by the time the client actually disconnects
            // (eg the normal case: the turn finishes first, the framework calls CloseAsync, the client ACKs
            // the close, WatchForDisconnectAsync's ReceiveAsync completes with that Close frame, and only then
            // does this callback run) -- that's not a bug to guard against so much as the expected shape of a
            // clean finish, so a disposed turnCancel here is a no-op, not a failure.
            try { turnCancel.Cancel(); }
            catch (ObjectDisposedException) { }
        });
        try
        {
            JArray rawMessages = rawInput["messages"] as JArray;
            string message = rawInput["message"]?.ToString();
            if ((rawMessages is null || rawMessages.Count == 0) && string.IsNullOrWhiteSpace(message))
            {
                await SendAsync(new JObject { ["error"] = "message or messages is required." });
                return null;
            }
            string assistantId = rawInput["assistantId"]?.ToString();
            string model = rawInput["model"]?.ToString();
            double temperature = rawInput["temperature"]?.Value<double>() ?? -1;
            int maxTokens = rawInput["maxTokens"]?.Value<int>() ?? -1;
            bool? enableThinking = rawInput["enableThinking"]?.Type == JTokenType.Boolean
                ? rawInput["enableThinking"].Value<bool>()
                : (bool?)null;
            int loops = ReadPositiveInt(rawInput, "loops");

            JObject settings = SettingsService.GetMergedSettings(session.User);
            assistantId = ResolveAssistantId(assistantId, () => AssistantService.GetActiveAssistantId(settings, session.User));
            if (string.IsNullOrWhiteSpace(model))
            {
                LLMModelInfo fallback = await LLMModelLookup.GetFirstAvailableAsync();
                if (fallback is null)
                {
                    await SendAsync(new JObject
                    {
                        ["error"] = "No LLM model is available. Add a backend under Server > Backends and "
                            + "make sure it advertises at least one model."
                    });
                    return null;
                }
                model = fallback.Id;
            }
            string systemPrompt = ResolveInstructionForRequest(InstructionIds.Chat, assistantId, settings, session.User);
            // messages takes priority over message when both are present ("replaces", per this method's own
            // doc); CreateFromMessages applies the same no-double-injection system-prompt rule Create already
            // guarantees on the single-message path (Create always folds systemPrompt into Messages[0] itself).
            ExtendedLLMInput input = rawMessages is { Count: > 0 }
                ? ExtendedLLMInput.CreateFromMessages(ParseMessagesArray(rawMessages), systemPrompt, model)
                : ExtendedLLMInput.Create(message, systemPrompt, model);
            input.RequestSession = session;
            input.EnableThinking = enableThinking;
            input.ConversationId = VoiceTurnWsConversationId(rawInput["conversationId"]?.ToString(), session.ID);
            ApplyParameters(input, AssistantService.ResolveParameters(assistantId, settings, session.User), temperature, maxTokens, loops: loops);

            List<JObject> enabledTools = AssistantResolver.Resolve(assistantId, session.User, settings).ToolsEnabled
                ? ToolRegistryService.GetEnabledTools(assistantId, settings, session.User)
                : [];
            AttachTools(input, enabledTools, session, assistantId, forceToolId: null);
            JArray deviceCalls = [];
            await ToolTurnRunner.RunAsync(input,
                new ToolTurnContext { Session = session, AssistantId = assistantId, MaxRounds = ToolLoop.DefaultMaxRounds },
                new VoiceWsSink(SendAsync, () => socket.State == WebSocketState.Open, deviceCalls), turnCancel.Token);
            return null;
        }
        catch (OperationCanceledException) when (turnCancel.IsCancellationRequested)
        {
            // WatchForDisconnectAsync's callback detected a disconnect -- an ordinary end, not a failure
            // worth logging as one. SendAsync no-ops once the socket is no longer open, so this is safe to
            // call unconditionally.
            await SendAsync(new JObject { ["error"] = "cancelled" });
            return null;
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Voice turn (streaming) failed: {ex.Message}");
            await SendAsync(new JObject { ["error"] = ex.Message });
            return null;
        }
        // No finally cancelling turnCancel (or anything else) here on an ordinary return: WatchForDisconnectAsync's
        // own ReceiveAsync was started with CancellationToken.None specifically so nothing here ever cancels
        // it. The framework calls socket.CloseAsync(...) unconditionally right after this method returns
        // (API.cs); .NET's own WebSocket implementation waits on an already-outstanding receive to complete
        // the close handshake instead of issuing a second one, so leaving that receive alone is what lets
        // CloseAsync finish cleanly. Cancelling it here (tried in an earlier version of this fix) transitions
        // the socket to Aborted before CloseAsync runs, which is a state CloseAsync does not accept -- it
        // threw on roughly 41% of otherwise-normal completions, reproduced empirically in
        // Tests/VoiceTurnWsDisconnectTests.cs.
    }

    /// <summary>Watches <paramref name="socket"/> for a disconnect and invokes <paramref name="onDisconnect"/>
    /// exactly once when it happens: on a received <see cref="WebSocketMessageType.Close"/> frame, or on any
    /// exception from <see cref="WebSocket.ReceiveAsync(ArraySegment{byte}, CancellationToken)"/> (a reset
    /// connection, a transport error, etc; treated the same as a clean close since this route has no way to
    /// tell those apart and no reason to react differently). An unexpected <c>Text</c>/<c>Binary</c> frame
    /// (nothing is expected from the client on this route) is <b>not</b> treated as a disconnect; it keeps
    /// receiving instead of ending the watch early.
    ///
    /// <para><b>Always receives with <see cref="CancellationToken.None"/>, never a token the caller might
    /// cancel.</b> An earlier version of this took a token tied to the turn's own lifetime and cancelled the
    /// pending receive when the turn finished normally, to "clean up", but cancelling a pending
    /// <c>ReceiveAsync</c> aborts the whole <see cref="WebSocket"/> (observed, not just documented:
    /// <c>Tests/VoiceTurnWsDisconnectTests.cs</c> reproduces it against a real loopback socket). SwarmUI's own
    /// request handler calls <c>socket.CloseAsync(...)</c> unconditionally right after the route returns
    /// (<c>API.cs</c>, not wrapped in its own try/catch), and an <c>Aborted</c> socket is not a state
    /// <c>CloseAsync</c> accepts; it threw on roughly 41% of otherwise-completely-normal turns. Leaving the
    /// receive outstanding and uncancelled is what lets it work: .NET's own <c>ManagedWebSocket.CloseAsync</c>
    /// waits on an already-outstanding receive to complete the close handshake instead of starting a second
    /// one, so the close frame <c>CloseAsync</c> sends (and the peer's reply to it) is exactly what completes
    /// this method's own pending <c>ReceiveAsync</c>; this method's job is only to notice that and call
    /// <paramref name="onDisconnect"/>, never to make the receive happen sooner.</para></summary>
    internal static async Task WatchForDisconnectAsync(WebSocket socket, Action onDisconnect)
    {
        byte[] buffer = new byte[16];
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                onDisconnect();
                return;
            }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                onDisconnect();
                return;
            }
            // Unexpected data frame: not a disconnect signal on this protocol. Loop back to ReceiveAsync
            // rather than returning, so the eventual real close (or error) is still caught.
        }
    }

    /// <summary>The assistant a request runs as: the one it names, or the active one when it names none. A blank value, including a JSON null
    /// read as text, counts as naming none, so the assistant's tool allowlist always applies.</summary>
    internal static string ResolveAssistantId(string requested, Func<string> activeAssistant)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested;
        }
        string active = activeAssistant();
        return string.IsNullOrWhiteSpace(active) ? AssistantConstants.DefaultId : active;
    }

    /// <summary>Cancels a chat turn that runs tools when its tab closes, so no later call starts and what ran is saved. A turn without
    /// tools is left to finish as before, since nothing runs after its reply.</summary>
    internal static void CancelToolTurnOnClose(WebSocket socket, CancellationTokenSource turn, bool runsTools)
    {
        _ = WatchForDisconnectAsync(socket, () =>
        {
            if (!runsTools)
            {
                return;
            }
            // The turn may already be over and its source disposed by then; there is nothing left to stop.
            try
            {
                turn.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    /// <summary>Parses <see cref="LLMAssistantVoiceTurnWS"/>'s optional <c>messages</c> request field,
    /// <c>[{role, content, toolCallId?, name?, toolCalls?}, …]</c>, into the extension's own
    /// <see cref="LLMMessage"/> shape. An entry's <c>toolCalls</c> is expected in the same <c>{id, name,
    /// arguments}</c> shape a <c>native_tool_call</c> frame sent earlier in the same session, so a client can
    /// replay its own history (including an earlier native tool call/result pair) back verbatim. A non-object
    /// entry is skipped rather than failing the whole turn; an unrecognized <c>role</c> falls back to
    /// <see cref="LLMRoles.User"/>, the same policy <see cref="ExtendedLLMInput.CreateFromHistory"/> already
    /// uses for a saved thread's roles.</summary>
    internal static List<LLMMessage> ParseMessagesArray(JArray raw)
    {
        List<LLMMessage> list = [];
        foreach (JToken tok in raw)
        {
            if (tok is not JObject obj)
            {
                continue;
            }
            string role = obj["role"]?.ToString()?.ToLowerInvariant() switch
            {
                LLMRoles.System => LLMRoles.System,
                LLMRoles.Assistant => LLMRoles.Assistant,
                LLMRoles.Tool => LLMRoles.Tool,
                _ => LLMRoles.User
            };
            LLMMessage message = new()
            {
                Role = role,
                Content = obj["content"]?.ToString() ?? "",
                ToolCallId = obj["toolCallId"]?.ToString(),
                Name = obj["name"]?.ToString()
            };
            if (obj["toolCalls"] is JArray toolCalls && toolCalls.Count > 0)
            {
                message.ToolCalls = [.. toolCalls.OfType<JObject>()];
            }
            list.Add(message);
        }
        return list;
    }

    /// <summary><see cref="ExtendedLLMInput.ConversationId"/> for a stored chat thread: the thread is the
    /// conversation, and every route here loads it through <see cref="ThreadStorageService.GetThread"/> for the
    /// calling user before building an input from it. Edits and regenerations stay on the same id (the engine
    /// reuses the prefix up to wherever the new branch diverges), and compare-mode lanes share it, since the
    /// provider keys each lane's model separately. Null for a blank id.</summary>
    internal static string ThreadConversationId(string threadId)
        => string.IsNullOrWhiteSpace(threadId) ? null : $"thread:{threadId}";

    /// <summary><see cref="ExtendedLLMInput.ConversationId"/> for the stateless one-shot
    /// <see cref="LLMAssistantVoiceTurn"/>: one scope per assistant, shared by all of the user's calls and devices
    /// (the provider adds the user and the model). Each call sends only the assistant's system prompt, its tool
    /// definitions and one new message, so that prefix is the only thing a later call could reuse, and it is the
    /// same for every caller with this assistant; a per-session scope would hold one copy per device and churn
    /// the engine's store for a client that opens a fresh session per call, for no extra reuse. Two calls at
    /// once cannot clash either: the engine serializes requests per device and runs a second request on a key
    /// that is in use without the cache.</summary>
    internal static string VoiceTurnConversationId(string assistantId)
        => $"voice-turn:{assistantId}";

    /// <summary><see cref="ExtendedLLMInput.ConversationId"/> for <see cref="LLMAssistantVoiceTurnWS"/>. Each turn
    /// arrives on a fresh WebSocket, so the connection identifies nothing past one turn. The client's own
    /// <c>conversationId</c> wins when present (one phone call, say), else the SwarmUI session the turn was sent
    /// with, which a voice client keeps for the whole call (AudioLab's <c>RemoteTextService</c> forwards its
    /// browser session's id on every turn) but which is shared by every call made from that session. The two
    /// forms have different prefixes, so a client-chosen id can never land on a session's scope, and the
    /// provider adds the user, so it can never reach another user's. Null when neither is available.</summary>
    internal static string VoiceTurnWsConversationId(string clientConversationId, string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(clientConversationId))
        {
            return $"voice-ws:{clientConversationId.Trim()}";
        }
        return string.IsNullOrWhiteSpace(sessionId) ? null : $"voice-ws-session:{sessionId}";
    }

    /// <summary>Stop button: cancels whatever generation is currently in flight for a thread (a single
    /// reply or every lane of a compare turn), so the model actually stops producing tokens server-side
    /// instead of just having the client stop listening. Plain HTTP, not WebSocket: the chat WS
    /// endpoints read exactly one incoming frame and never listen again, so a second in-band message on
    /// that same socket isn't a viable signal path; this is a separate call the client fires before it
    /// closes its socket.</summary>
    public static Task<JObject> LLMAssistantStopGeneration(Session session, string threadId)
    {
        // Thread ids are otherwise-unauthenticated strings (they can even appear in output paths), so
        // without this check any caller with PermChat could cancel another user's in-flight generation
        // by guessing/reusing their thread id. Ownership failure and "nothing was in flight" return the
        // identical shape on purpose: distinguishing them would turn this into a thread-id oracle.
        if (ThreadStorageService.GetThread(session.User, threadId) is null)
        {
            return Task.FromResult(new JObject { ["success"] = true, ["cancelled"] = false });
        }
        bool cancelled = GenerationCancellationRegistry.Cancel(threadId);
        return Task.FromResult(new JObject { ["success"] = true, ["cancelled"] = cancelled });
    }

    /// <summary>Sends a chat message with streaming response over WebSocket. Server-authoritative:
    /// the thread is the source of truth for history. Request shape:
    /// <c>{ threadId: string (required), message: string, model?, temperature?, maxTokens?, loops?, instructionId? }</c>.
    /// <c>loops</c> is the recurrent-block pass count for a looped model such as Dory (1 to 4); omitted, the saved
    /// <c>loops</c> parameter and then the local backend's <c>RecurrentLoops</c> setting apply, and other models ignore it.
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
            if (ThreadStorageService.AppendMessage(session.User, threadId, userMsg) is null)
            {
                return new JObject { ["success"] = false, ["error"] = $"Chat '{threadId}' not found." };
            }
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
    /// becomes the active branch. WS, same streaming protocol as <see cref="LLMAssistantSendMessageWS"/>.
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

    /// <summary>Regenerates an assistant reply as a NEW sibling branch (the previous reply stays switchable
    /// via the pager) and streams it. WS. Request: <c>{ threadId, messageId (assistant reply to regen),
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
            // Move the active leaf to the preceding turn; the streamed reply appends as a new child, a
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
        int loops = ReadPositiveInt(rawInput, "loops");
        // The composer's Thinking toggle: true or false, or absent for the model's template default.
        bool? enableThinking = ReadOptionalBool(rawInput, "enableThinking");

        JObject thread = ThreadStorageService.GetThread(session.User, threadId);
        if (thread is null)
        {
            await SendWsError(socket, $"Chat '{threadId}' not found.");
            return;
        }
        // Assistant is locked to the thread (set at creation). Per-message override would make history
        // confusing; assistant identity is part of the thread's identity.
        string assistantId = thread["assistantId"]?.ToString();
        JObject settings = SettingsService.GetMergedSettings(session.User);
        if (string.IsNullOrEmpty(assistantId))
        {
            assistantId = AssistantService.GetActiveAssistantId(settings, session.User);
        }
        // Registered before the setup below (not just before streaming): a model-cache-miss lookup or a
        // slow tool resolution can each take a while, and a Stop clicked during that window needs
        // somewhere to land. Setup itself isn't interruptible mid-call, but registering early means Stop
        // is at least observed, and the check right before streaming turns that into a fast, clean bail
        // instead of ignoring the Stop and running the full generation anyway.
        CancellationTokenSource cts = GenerationCancellationRegistry.Begin(threadId);
        bool stopped;
        try
        {
            // Resolve model facts once so per-model instruction variants can pick the right text.
            LLMModelInfo modelInfo = await LLMModelLookup.GetByIdAsync(model);
            string systemPrompt = ResolveInstructionForRequest(instructionId, assistantId, settings, session.User, modelInfo);
            // Build LLM input from the active branch (truncated to maxContextMessages).
            List<ChatMessageData> history = BuildHistoryFromThread(thread, settings, rawInput);
            ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory(history, systemPrompt, model);
            input.RequestSession = session;
            input.ConversationId = ThreadConversationId(threadId);
            input.EnableThinking = enableThinking;
            JObject resolvedParams = AssistantService.ResolveParameters(assistantId, settings, session.User);
            ApplyParameters(input, resolvedParams, temperature, maxTokens, seed, loops);
            // Attach the tools enabled for this assistant, unless tool calling is switched off for this thread or assistant
            // (see EffectiveToolsEnabled).
            List<JObject> enabledTools = EffectiveToolsEnabled(thread, assistantId, settings, session.User)
                ? ToolRegistryService.GetEnabledTools(assistantId, settings, session.User)
                : [];
            AttachTools(input, enabledTools, session, assistantId, forceToolId);
            if (cts.IsCancellationRequested)
            {
                // Stopped during setup, before a single token was ever requested: nothing to persist.
                return;
            }
            CancelToolTurnOnClose(socket, cts, enabledTools.Count > 0);
            await LLMStreamHelper.StreamToWebSocket(socket, input, session, threadId, assistantId, ct: cts.Token, clientAssistantMessageId: assistantMessageId);
        }
        finally
        {
            // Captured before End() disposes the source: a Stop makes StreamToWebSocket return normally
            // (it persists the partial reply itself), so this is the only signal left afterward that the
            // user actually asked to stop, as opposed to a normal finish.
            stopped = cts.IsCancellationRequested;
            GenerationCancellationRegistry.End(threadId, cts);
        }
        if (!stopped)
        {
            // Skip auto-titling after a stopped reply: MaybeGenerateTitleAsync starts a new, uncancellable
            // generation (LLMDispatcher.Generate, no token), and running it right after Stop would silently
            // start more model work the instant the user asked for it to stop.
            await MaybeGenerateTitleAsync(socket, session, threadId, model);
        }
    }

    /// <summary>Claude/ChatGPT-style auto-titling: right after a chat's first exchange, asks the model
    /// for a short title and replaces the raw-first-message fallback with it. No-ops once the title has
    /// been claimed (by a prior attempt or a manual rename; see <see cref="ThreadStorageService.NeedsGeneratedTitle"/>),
    /// and never throws into the caller; a failed title generation just leaves the fallback title in place.</summary>
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

    /// <summary>The token cap for a generated chat title: a title is 3-6 words, so this is deliberately far below the
    /// chat's <see cref="ExtendedLLMInput.DefaultMaxTokens"/>.</summary>
    public const int TitleMaxTokens = 20;

    /// <summary>The temperature for a generated chat title: a little below the chat default, for a steadier short phrase.</summary>
    public const double TitleTemperature = 0.7;

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
        input.MaxTokens = TitleMaxTokens;
        input.Temperature = TitleTemperature;
        string raw = await LLMDispatcher.Generate(input);
        return CleanGeneratedTitle(raw);
    }

    /// <summary>Reasoning models spend the whole 20-token budget "thinking out loud" ("Okay, let's tackle this.
    /// The user wants a short title..."); that is never a title.</summary>
    private static readonly System.Text.RegularExpressions.Regex ReasoningLead = new(@"^(okay|ok|alright|hmm|let me|let's|lets|so,|first,|i need|i should|the user|user wants|looking at)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Thinking-aloud text opens like prose AND is long or talks about the task itself; a real title that
    /// merely starts with "Let's" or "First," ("Let's Build a Snake Game") is short and has neither.</summary>
    private static bool LooksLikeReasoning(string text)
    {
        return ReasoningLead.IsMatch(text) && (text.Length > 50 || ReasoningTopic.IsMatch(text));
    }

    private static readonly System.Text.RegularExpressions.Regex ReasoningTopic = new(@"\b(user|title|conversation|i need|i should|i'll|let me)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Turns raw model output into a usable chat title, or null when it isn't one. Small models return
    /// junk like `Assistant: **"Something`, and reasoning models return their thinking: strip think blocks,
    /// take the first line, drop label prefixes / markdown / quotes, and reject what is left if unusable.</summary>
    internal static string CleanGeneratedTitle(string raw)
    {
        string cleaned = System.Text.RegularExpressions.Regex.Replace(raw ?? "", @"<think>[\s\S]*?</think>|<think>[\s\S]*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        cleaned = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault("").Trim();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"^(title|chat title|assistant|answer)\s*[:\-]\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        cleaned = cleaned.Replace("**", "").Replace("`", "").Trim().Trim('"', '\'', '*', '_', '.', ' ', '\u201C', '\u201D');
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ");
        if (cleaned.Length is < 3 or > 80 || !cleaned.Any(char.IsLetter) || LooksLikeReasoning(cleaned))
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
    /// tools are never attached to the request; small local GGUF models get confused by tool descriptions
    /// they should not see.</summary>
    private static bool EffectiveToolsEnabled(JObject thread, string assistantId, JObject settings, User user)
    {
        if (thread["toolsEnabled"]?.Type == JTokenType.Boolean)
        {
            return thread["toolsEnabled"].Value<bool>();
        }
        return AssistantResolver.Resolve(assistantId, user, settings).ToolsEnabled;
    }

    /// <summary>Puts the enabled tools on the request: each tool's handler may enrich its definition for this user, and a forced tool
    /// is kept only when it is one of the enabled tools. The model learns the tools from the request itself; the engine writes the
    /// tool prompt for templates that have no tools slot.</summary>
    private static void AttachTools(ExtendedLLMInput input, List<JObject> enabledTools, Session session, string assistantId, string forceToolId)
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
        // The enabled tool's own id: the local engine refuses a forced name that differs from the offered one, even in case.
        string forcedId = string.IsNullOrEmpty(forceToolId) ? null
            : enabledTools.Select(t => t["id"]?.ToString()).FirstOrDefault(id => string.Equals(id, forceToolId, StringComparison.OrdinalIgnoreCase));
        if (forcedId is not null)
        {
            input.ForceToolId = forcedId;
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
        int loops = ReadPositiveInt(rawInput, "loops");
        bool? enableThinking = ReadOptionalBool(rawInput, "enableThinking");
        // Build the active-branch history ONCE; it's identical for every lane (same prompt, same context).
        // Each lane gets its own ExtendedLLMInput (its own model, device and tool rounds).
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

        // One shared source for the whole turn: Stop should kill every lane at once, not just one.
        CancellationTokenSource cts = GenerationCancellationRegistry.Begin(threadId);

        async Task RunLane(int lane)
        {
            (string Model, string Device, int BackendId, string AssistantMessageId) L = lanes[lane];
            try
            {
                // cts is registered (Begin, above) before this task ever starts, so a Stop that lands
                // during this lane's own setup (model lookup, tool resolution) is observed here instead
                // of being silently ignored: the check right before StreamToWebSocket turns it into a
                // fast bail for this lane rather than starting the model anyway.
                LLMModelInfo modelInfo = await LLMModelLookup.GetByIdAsync(L.Model);
                string systemPrompt = ResolveInstructionForRequest(instructionId, assistantId, settings, session.User, modelInfo);
                ExtendedLLMInput input = ExtendedLLMInput.CreateFromHistory(baseHistory, systemPrompt, L.Model);
                input.RequestSession = session;
                input.ConversationId = ThreadConversationId(threadId);
                input.BackendId = L.BackendId;
                input.Device = L.Device;
                input.EnableThinking = enableThinking;
                ApplyParameters(input, resolvedParams, temperature, maxTokens, seed, loops);
                AttachTools(input, enabledTools, session, assistantId, forceToolId);
                if (cts.IsCancellationRequested)
                {
                    return;
                }
                await LLMStreamHelper.StreamToWebSocket(socket, input, session, threadId, assistantId,
                    clientAssistantMessageId: L.AssistantMessageId,
                    lane: lane, sendLock: sendLock, parentMessageId: parentUserId,
                    compareGroupId: parentUserId, deviceLabel: L.Device, setActiveLeaf: lane == 0,
                    ct: cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Stopped, not failed: no lane-tagged error frame for this one.
            }
            catch (Exception ex)
            {
                // Isolate a lane failure; the other lane(s) must keep streaming. Surface as a lane-tagged error.
                Logs.Error($"[LLMAssistant] Compare lane {lane} ({L.Model}) failed: {ex.Message}");
                await SendJsonLane(socket, sendLock, lane, new JObject { ["error"] = ex.Message });
            }
        }

        CancelToolTurnOnClose(socket, cts, enabledTools.Count > 0);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, lanes.Count).Select(RunLane));
        }
        finally
        {
            GenerationCancellationRegistry.End(threadId, cts);
        }
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
    /// Source of truth is the saved thread; the client cannot inject fake history.
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
            if (msg["toolCalls"] is JArray savedCalls && savedCalls.Count > 0)
            {
                entry.ToolCalls = [.. savedCalls.OfType<JObject>()];
            }
            if (msg["rounds"] is JArray savedRounds && savedRounds.Count > 0)
            {
                entry.Rounds = [.. savedRounds.OfType<JObject>()];
            }
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
                // accessible as text; needed for tool calls like generate_image's initImage.
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

    /// <summary>Reads <paramref name="key"/> from <paramref name="obj"/> as a positive <see cref="int"/>: the value when it is a JSON integer
    /// above zero that fits, else 0, the "not set" value of a zero-means-unset integer field (absent, null, zero, negative, fractional, a
    /// string, or an integer beyond <see cref="int"/> all read as not set). Reads the <c>loops</c> field of a websocket request and the
    /// saved <c>loops</c> parameter; the engine rejects a loop count above the model's maximum when the request runs.</summary>
    /// <summary>A tri-state switch from a string API parameter: <c>true</c> or <c>false</c> in any case is the value; anything else, null
    /// included, is null ("not set"), since SwarmUI's reflected API parameters have no nullable bool.</summary>
    internal static bool? ReadOptionalBool(string value) => bool.TryParse(value?.Trim(), out bool parsed) ? parsed : null;

    /// <summary>Reads <paramref name="key"/> from a websocket request body as a tri-state switch: a JSON boolean, or the string <c>true</c> or
    /// <c>false</c>, is the value; absent, null or anything else is null ("not set", so the model's template default applies).</summary>
    internal static bool? ReadOptionalBool(JObject obj, string key) => obj?[key] switch
    {
        JValue { Type: JTokenType.Boolean } flag => flag.Value<bool>(),
        JValue { Type: JTokenType.String } text => ReadOptionalBool(text.Value<string>()),
        _ => null
    };

    internal static int ReadPositiveInt(JObject obj, string key)
    {
        // Newtonsoft stores every JSON integer that fits a long as a long (a bigger one is a BigInteger, which fails the pattern).
        return obj?[key] is JValue { Type: JTokenType.Integer, Value: long value } && value is > 0 and <= int.MaxValue ? (int)value : 0;
    }

    /// <summary>Applies per-request parameter overrides on top of resolved parameters.
    /// <paramref name="seed"/> <c>-1</c> means "random, let the backend pick"; any non-negative
    /// value pins the seed (only providers that honor <c>ExtendedLLMInput.Seed</c> will use it).
    /// <paramref name="loops"/> <c>0</c> means "not set by this request": the saved <c>loops</c> parameter applies when
    /// positive, else the provider's own default (the checkpoint's for a looped model, ignored by every other model).</summary>
    internal static void ApplyParameters(ExtendedLLMInput input, JObject parameters, double temperature, int maxTokens, long seed = -1, int loops = 0)
    {
        input.Temperature = temperature >= 0 ? temperature : parameters?["temperature"]?.Value<double>() ?? ExtendedLLMInput.DefaultTemperature;
        input.MaxTokens = maxTokens >= 0 ? maxTokens : parameters?["maxTokens"]?.Value<int>() ?? ExtendedLLMInput.DefaultMaxTokens;
        input.TopP = parameters?["topP"]?.Value<double>() ?? ExtendedLLMInput.DefaultTopP;
        int resolvedLoops = loops > 0 ? loops : ReadPositiveInt(parameters, "loops");
        input.Loops = resolvedLoops > 0 ? resolvedLoops : null;
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
