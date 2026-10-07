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
    /// and you get back the raw model output. For chat use the WS endpoint with a threadId. <paramref name="backendId"/> picks one of
    /// several backends serving the same model; <paramref name="device"/> overrides the backend's device (eg <c>cuda:0+cuda:1</c>).
    /// Both are part of the cache key.</summary>
    public static async Task<JObject> LLMAssistantSendMessage(Session session,
        string message, string instructionId = null, string model = null,
        double temperature = -1, int maxTokens = -1, bool noCache = false,
        string assistantId = null, string device = null, int backendId = -1)
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
            ApplyParameters(input, resolvedParams, temperature, maxTokens);
            string response;
            if (noCache)
            {
                response = await LLMDispatcher.Generate(input);
            }
            else
            {
                response = await Cache.GetOrCreate(session.User?.UserID, $"{model}|{device}|{backendId}", assistantId, message, instructionId, async () =>
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

    /// <summary>One-shot conversational turn <b>with tool calling</b>, for a voice satellite: text in, spoken
    /// reply plus any device actions out.
    ///
    /// <para>Exists because the two existing paths each miss half of what a device needs.
    /// <see cref="LLMAssistantSendMessage"/> is one-shot but has no tool loop, so the assistant can talk and
    /// nothing else. <c>LLMAssistantSendMessageWS</c> has the tool loop but is a WebSocket carrying incremental
    /// frames and requires a stored thread — the wrong shape for a microcontroller that wants one request and
    /// one answer.</para>
    ///
    /// <para>Device actions (<c>set_led_profile</c>, <c>set_volume</c>, <c>mute_mic</c>) execute as no-ops
    /// server-side and come back in <c>toolCalls</c> for the caller to run against its own hardware; see
    /// <see cref="Tools.BuiltIn.DeviceActionTool"/>. Server-side tools still run normally here, so the same turn
    /// can search the web and dim the lights.</para>
    ///
    /// <para>Stateless like <see cref="LLMAssistantSendMessage"/> — no thread is loaded or written. Unlike it,
    /// nothing is cached: a voice assistant asked the same thing twice should answer twice, not replay one
    /// canned reply. The local engine still reuses the system prompt and tool definitions across calls (see
    /// <see cref="VoiceTurnConversationId"/>) — reused KV, never a reused reply.</para>
    ///
    /// <para>Request: <c>{ message (required), assistantId?, model?, temperature?, maxTokens? }</c>.
    /// Response: <c>{ success, response, toolCalls: [{ name, arguments }], truncated? }</c>.</para></summary>
    public static async Task<JObject> LLMAssistantVoiceTurn(Session session,
        string message, string assistantId = null, string model = null,
        double temperature = -1, int maxTokens = -1)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return new JObject { ["success"] = false, ["error"] = "message is required." };
            }
            JObject settings = SettingsService.GetMergedSettings(session.User);
            assistantId ??= AssistantService.GetActiveAssistantId(settings, session.User);
            // A voice satellite sends an utterance and nothing else — there is no model picker on a device
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
            ApplyParameters(input, AssistantService.ResolveParameters(assistantId, settings, session.User), temperature, maxTokens);

            List<JObject> enabledTools = AssistantResolver.Resolve(assistantId, session.User, settings).ToolsEnabled
                ? ToolRegistryService.GetEnabledTools(assistantId, settings, session.User)
                : [];
            await ApplyToolsToInput(input, enabledTools, session, assistantId, forceToolId: null);

            StringBuilder spoken = new();
            JArray deviceCalls = [];
            bool truncated = true;
            for (int iteration = 0; iteration < ToolConstants.MaxAgenticIterations; iteration++)
            {
                string round = await LLMDispatcher.Generate(input);
                List<ToolPromptService.ParsedToolCall> calls = ToolPromptService.ParseToolCalls(round, out List<string> malformed);
                if (calls.Count == 0 && malformed.Count == 0)
                {
                    spoken.Append(round);
                    truncated = false;
                    break;
                }
                // The tool-call markup itself is not speech; only prose the model produced alongside it is.
                // Without this the device reads "<tool_call>{...}</tool_call>" out loud. Strips malformed
                // matches too (defense in depth, not just parsed calls' RawMatch): a native tool call that
                // failed to round-trip through AppendGenerateChunk's synthesized tag — or any model emitting
                // the tag convention natively and getting it wrong — would otherwise leak its raw, unparsed
                // <tool_call>…</tool_call> text straight into spoken/displayed output.
                string prose = calls.Select(call => call.RawMatch).Concat(malformed)
                    .Aggregate(round, (text, rawMatch) => string.IsNullOrEmpty(rawMatch) ? text : text.Replace(rawMatch, "")).Trim();
                if (prose.Length > 0) spoken.Append(prose);
                input.Messages.Add(new LLMMessage() { Role = LLMRoles.Assistant, Content = round });
                foreach (string _ in malformed)
                {
                    input.Messages.Add(new LLMMessage()
                    {
                        Role = LLMRoles.User,
                        Content = ToolPromptService.FormatToolResult("tool_call", new JObject
                        {
                            ["success"] = false,
                            ["error"] = "Could not parse this tool call — invalid or incomplete JSON. Re-emit it as a single well-formed <tool_call>{\"name\":\"...\",\"arguments\":{...}}</tool_call> block."
                        })
                    });
                }
                foreach (ToolPromptService.ParsedToolCall call in calls)
                {
                    JObject result;
                    try
                    {
                        result = await ToolExecutorService.ExecuteTool(call.Name, call.Arguments, session, assistantId, threadId: null, input.Model);
                    }
                    catch (Exception ex)
                    {
                        Logs.Error($"[LLMAssistant] Voice turn tool '{call.Name}' failed: {ex.Message}");
                        result = new JObject { ["success"] = false, ["error"] = ex.Message };
                    }
                    // Only surface calls the device is expected to act on, and only ones that validated —
                    // handing back a rejected call would have the device act on arguments the server refused.
                    if (Tools.BuiltIn.DeviceActionTool.IsDeviceAction(call.Name) && result["success"]?.Value<bool>() == true)
                    {
                        deviceCalls.Add(new JObject { ["name"] = call.Name, ["arguments"] = call.Arguments });
                    }
                    input.Messages.Add(new LLMMessage()
                    {
                        Role = LLMRoles.User,
                        Content = ToolPromptService.FormatToolResult(call.Name, result)
                    });
                }
            }
            string spokenText = spoken.ToString().Trim();
            // A turn that produced neither speech nor a device action is a failed turn, not an empty
            // success. The provider returns "" for an engine-side failure just as it would for a model
            // that genuinely said nothing, and a voice device cannot tell those apart: it would speak
            // silence and log nothing, which is the least debuggable possible outcome. Callers get a
            // real error instead. Tool-only turns ("turn the ring blue") legitimately have no speech,
            // so they must not trip this.
            if (spokenText.Length == 0 && deviceCalls.Count == 0)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "The model produced no reply. This is usually a backend that cannot run "
                        + "on its configured device — check Server > Backends and the server log."
                };
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

    /// <summary>Streaming variant of <see cref="LLMAssistantVoiceTurn"/>: forwards <c>chunk</c>/
    /// <c>native_tool_call</c>/<c>tool_result</c>/<c>done</c> frames over the socket as they happen, instead of
    /// returning one accumulated response. Runs <see cref="HartsyLocalLLMProvider.StreamToolLoopAsync"/> (the
    /// Tools package's <see cref="ToolLoop"/> against the engine directly) rather than the tag-based
    /// <c>&lt;tool_call&gt;</c> convention every other route in this file uses for tool calls, because that is
    /// the whole reason this route exists: a caller that wants tool-call ids and native dispatch latency
    /// instead of scanning generated text for a closing tag.
    ///
    /// <para><b>Hartsy-local only, by design.</b> Native tool calling through the Tools package only exists
    /// for the engine's own <see cref="HartsyInference.Engine.Services.ITextService"/>, which only the
    /// Hartsy-local provider exposes — there is no equivalent for a remote/cloud provider to plug into here,
    /// so a resolved provider that isn't Hartsy-local sends one <c>error</c> frame naming the gap. Use
    /// <see cref="LLMAssistantVoiceTurn"/> or <see cref="LLMAssistantSendMessageWS"/> for every other
    /// provider.</para>
    ///
    /// <para><b>Native tool calling being unavailable for this model is not an error.</b> When the resolved
    /// provider is Hartsy-local but <see cref="HartsyLocalLLMProvider.SupportsNativeToolCallingFor"/> is false
    /// for <c>model</c> (<see cref="HartsyLocalLLMProvider.HartsyLocalLLMProviderSettings.StructuredToolCalling"/>
    /// off — the default — or the resolved checkpoint's own chat template doesn't instruct Hermes JSON tool
    /// calls), the turn still runs: tools are dropped from the request entirely, with no tag-prompt fallback
    /// either (this route has no tag scanner of its own to read one back out). If the assistant actually had
    /// tools enabled, exactly one <c>{notice:"..."}</c> frame says so, before the plain stream starts, so the
    /// caller knows why no <c>native_tool_call</c> frame is coming; an assistant with no tools enabled at all
    /// gets no notice, since nothing was dropped.</para>
    ///
    /// <para>Stateless like <see cref="LLMAssistantVoiceTurn"/>: no thread is loaded or written, and device
    /// actions (<c>set_led_profile</c>, etc.) execute as no-ops server-side and come back in the final
    /// <c>done</c> frame's <c>toolCalls</c> for the caller to run against its own hardware, exactly as the
    /// one-shot route returns them.</para>
    ///
    /// <para>Every turn is its own WebSocket connection (one request frame in, frames out, then closed), so the
    /// socket cannot identify a conversation across turns. The optional <c>conversationId</c> does, when the
    /// client sends one; otherwise the caller's SwarmUI session does — see <see cref="VoiceTurnWsConversationId"/>.
    /// Either way it only scopes the local engine's prefix-KV reuse; nothing is stored here.</para>
    ///
    /// <para>Request: <c>{ message?|messages?, assistantId?, model?, temperature?, maxTokens?,
    /// enableThinking?, conversationId? }</c> — at least one of <c>message</c> (a single new user turn) or <c>messages</c> (the
    /// full conversation so far, oldest first, as
    /// <c>[{role: system|user|assistant|tool, content, toolCallId?, name?, toolCalls?}, …]</c>) is required;
    /// <c>messages</c> takes priority when both are present. The assistant's resolved system prompt is
    /// prepended as a leading system turn unless <c>messages</c> already opens with one — no double injection
    /// (see <see cref="ExtendedLLMInput.CreateFromMessages"/>). <c>enableThinking</c> maps straight onto
    /// <see cref="TextRequest.EnableThinking"/>; omitted, the model's template keeps its own default (today's
    /// behavior). Frames: <c>{notice:"..."}</c>? (at most one, before the first <c>chunk</c>),
    /// <c>{chunk:"..."}</c>*, <c>{native_tool_call:{id,name,arguments}}</c>,
    /// <c>{tool_result:{id,name,result}}</c> per call, then one final <c>{done:true, full_text,
    /// toolCalls:[{name,arguments}], stopReason?}</c> — or <c>{error:"..."}</c> at any point, which ends the
    /// stream without a <c>done</c> frame.</para></summary>
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

            JObject settings = SettingsService.GetMergedSettings(session.User);
            assistantId ??= AssistantService.GetActiveAssistantId(settings, session.User);
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
            ApplyParameters(input, AssistantService.ResolveParameters(assistantId, settings, session.User), temperature, maxTokens);

            // Resolved before touching tools at all, unlike the other routes in this file: the non-native
            // branch below must never call ApplyToolsToInput (it would inject the tag-based tool system prompt
            // this route has no scanner to read back out of the plain text stream), so the provider/
            // native-support check has to come first, not after tool enrichment.
            ILLMProvider provider = await LLMDispatcher.GetProvider(input);
            if (provider is not HartsyLocalLLMProvider hartsyProvider)
            {
                await SendAsync(new JObject
                {
                    ["error"] = "LLMAssistantVoiceTurnWS only runs on the Hartsy-local provider (Server > "
                        + "Backends). Use LLMAssistantVoiceTurn or LLMAssistantSendMessageWS for other providers."
                });
                return null;
            }

            List<JObject> enabledTools = AssistantResolver.Resolve(assistantId, session.User, settings).ToolsEnabled
                ? ToolRegistryService.GetEnabledTools(assistantId, settings, session.User)
                : [];
            // SupportsNativeToolCallingFor(model), not the plain SupportsNativeToolCalling property: this
            // backend serves whatever model `model` names, and the installed Hermes filter only matches
            // checkpoints whose own chat template instructs that convention — see that method's own doc.
            ToolRegistry registry;
            if (hartsyProvider.SupportsNativeToolCallingFor(model))
            {
                await ApplyToolsToInput(input, enabledTools, session, assistantId, forceToolId: null);
                registry = BuildToolRegistry(enabledTools, session, assistantId, model);
            }
            else
            {
                // Deliberately NOT ApplyToolsToInput: its non-native branch injects the tag-based <tool_call>
                // system prompt, which this route has no scanner to read back out of the plain text stream --
                // input.Tools is left at its default empty list, so BuildRequestAsync's own gate
                // (SupportsNativeToolCallingFor(input.Model) && input.Tools is {Count: >0}) leaves
                // TextRequest.Tools null and the engine's installed Hermes filter stays inert for this turn.
                // An empty ToolRegistry then makes StreamToolLoopAsync (via ToolLoop.RunAsync) run exactly one
                // plain round, the same shape
                // ToolLoopIntegrationTests.RunAsync_EmptyRegistryAndNoToolsOnTheRequest_StillStreamsPlainTextAsOneRound
                // already proves.
                registry = new ToolRegistry();
                if (enabledTools.Count > 0)
                {
                    // Only when tools were actually configured and are about to be silently dropped: an
                    // assistant with none configured at all would otherwise get this notice on every single
                    // turn, which is noise, not a fallback worth flagging.
                    string reason = hartsyProvider.DescribeNativeToolCallingUnavailability(model);
                    await SendAsync(new JObject { ["notice"] = $"tool calling unavailable for {model}: {reason}; replying without tools" });
                }
            }

            JArray deviceCalls = [];
            (string spoken, StopReason? finalStop) = await ForwardFramesAsync(
                hartsyProvider.StreamToolLoopAsync(input, registry, ToolLoop.DefaultMaxRounds, turnCancel.Token),
                SendAsync, () => socket.State == WebSocketState.Open, deviceCalls);
            if (socket.State != WebSocketState.Open)
            {
                return null;
            }
            await SendAsync(new JObject
            {
                ["done"] = true,
                ["full_text"] = spoken,
                ["toolCalls"] = deviceCalls,
                ["stopReason"] = finalStop switch
                {
                    StopReason.Length => "length",
                    StopReason.Cancelled => "cancelled",
                    StopReason.Error => "error",
                    // The round limit was hit while the model still wanted another call (ToolLoop never
                    // dispatches that last one) — distinct from a normal finish, same as the others above.
                    StopReason.ToolCall => "tool_call",
                    _ => null
                }
            });
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
    /// exactly once when it happens — on a received <see cref="WebSocketMessageType.Close"/> frame, or on any
    /// exception from <see cref="WebSocket.ReceiveAsync(ArraySegment{byte}, CancellationToken)"/> (a reset
    /// connection, a transport error, etc; treated the same as a clean close since this route has no way to
    /// tell those apart and no reason to react differently). An unexpected <c>Text</c>/<c>Binary</c> frame —
    /// nothing is expected from the client on this route — is <b>not</b> treated as a disconnect; it keeps
    /// receiving instead of ending the watch early.
    ///
    /// <para><b>Always receives with <see cref="CancellationToken.None"/>, never a token the caller might
    /// cancel.</b> An earlier version of this took a token tied to the turn's own lifetime and cancelled the
    /// pending receive when the turn finished normally, to "clean up" — but cancelling a pending
    /// <c>ReceiveAsync</c> aborts the whole <see cref="WebSocket"/> (observed, not just documented:
    /// <c>Tests/VoiceTurnWsDisconnectTests.cs</c> reproduces it against a real loopback socket). SwarmUI's own
    /// request handler calls <c>socket.CloseAsync(...)</c> unconditionally right after the route returns
    /// (<c>API.cs</c>, not wrapped in its own try/catch), and an <c>Aborted</c> socket is not a state
    /// <c>CloseAsync</c> accepts — it threw on roughly 41% of otherwise-completely-normal turns. Leaving the
    /// receive outstanding and uncancelled is what lets it work: .NET's own <c>ManagedWebSocket.CloseAsync</c>
    /// waits on an already-outstanding receive to complete the close handshake instead of starting a second
    /// one, so the close frame <c>CloseAsync</c> sends (and the peer's reply to it) is exactly what completes
    /// this method's own pending <c>ReceiveAsync</c> — this method's job is only to notice that and call
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

    /// <summary>Adapts the assistant's enriched, per-user tool JObjects (<see cref="ToolRegistryService.GetEnabledTools"/>,
    /// the same shape <see cref="ApplyToolsToInput"/> already put on <see cref="ExtendedLLMInput.Tools"/>) into
    /// a <see cref="ToolRegistry"/> that dispatches through the existing <see cref="ToolExecutorService"/>.
    /// Tool *definitions* for the model come from <see cref="TextRequest.Tools"/> (set by
    /// <c>HartsyLocalLLMProvider.BuildRequestAsync</c> from the same list, since <see cref="ToolLoop.RunAsync"/>
    /// prefers <c>request.Tools</c> over <paramref name="registry"/>'s own definitions when both are present) —
    /// this registry only needs to be able to run a call once the model makes one.</summary>
    internal static ToolRegistry BuildToolRegistry(List<JObject> enabledTools, Session session, string assistantId, string model)
    {
        ToolRegistry registry = new();
        foreach (JObject tool in enabledTools)
        {
            string name = tool["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            string description = tool["description"]?.ToString() ?? "";
            string jsonSchema = (tool["parameters"] as JObject ?? new JObject()).ToString();
            registry.Add(name, description, jsonSchema, async (argsJson, ct) =>
            {
                JObject result = await ToolExecutorService.ExecuteTool(name, ParseJsonOrEmpty(argsJson), session, assistantId, threadId: null, model, ct);
                return result.ToString(Newtonsoft.Json.Formatting.None);
            });
        }
        return registry;
    }

    /// <summary>Parses a JSON object string, or returns an empty <see cref="JObject"/> for null/blank/malformed
    /// input rather than throwing — every caller here is reading a model's tool-call arguments or a tool's own
    /// result text, neither of which this route controls closely enough to treat a parse failure as fatal to
    /// the whole turn.</summary>
    internal static JObject ParseJsonOrEmpty(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JObject();
        }
        try
        {
            return JObject.Parse(json);
        }
        catch (Exception ex)
        {
            Logs.Debug($"[LLMAssistant] Could not parse '{json}' as a JSON object: {ex.Message}");
            return new JObject();
        }
    }

    /// <summary>Parses <see cref="LLMAssistantVoiceTurnWS"/>'s optional <c>messages</c> request field —
    /// <c>[{role, content, toolCallId?, name?, toolCalls?}, …]</c> — into the extension's own
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
    /// calling user before building an input from it. Edits and regenerations stay on the same id — the engine
    /// reuses the prefix up to wherever the new branch diverges — and compare-mode lanes share it, since the
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

    /// <summary>Drains <paramref name="chunks"/> into wire frames via <paramref name="send"/>, exactly as
    /// <see cref="LLMAssistantVoiceTurnWS"/> did inline before this was pulled out: the same translation serves
    /// both its native-tool-calling branch and its no-tools-available fallback (an empty
    /// <see cref="ToolRegistry"/> makes <see cref="ToolLoop.RunAsync"/> run exactly one plain round — see
    /// <see cref="LLMAssistantVoiceTurnWS"/>'s own comment), so there is exactly one emission path and one
    /// <c>done</c> shape instead of two. Pulled out for testability too: this takes a plain
    /// <see cref="IAsyncEnumerable{TextChunk}"/>, the same contract <c>ToolLoopIntegrationTests</c> already
    /// drives against a fake <c>ITextService</c>-backed <see cref="ToolLoop.RunAsync"/>, with no live
    /// <see cref="WebSocket"/>/<see cref="Session"/> needed. Stops draining as soon as
    /// <paramref name="socketOpen"/> reports false, checked before every chunk is handled, same as the inline
    /// version's own check. <paramref name="deviceCalls"/> is appended in place (a validated device-action
    /// result, the same rule <see cref="LLMAssistantVoiceTurn"/> applies) rather than returned, since the
    /// caller already owns the <see cref="JArray"/> that goes on its own <c>done</c> frame.</summary>
    internal static async Task<(string FullText, StopReason? Stop)> ForwardFramesAsync(
        IAsyncEnumerable<TextChunk> chunks, Func<JObject, Task> send, Func<bool> socketOpen, JArray deviceCalls)
    {
        StringBuilder spoken = new();
        StopReason? finalStop = null;
        await foreach (TextChunk chunk in chunks)
        {
            if (!socketOpen())
            {
                break;
            }
            switch (chunk.Kind)
            {
                case TextChunkKind.Chunk:
                    spoken.Append(chunk.Text);
                    await send(new JObject { ["chunk"] = chunk.Text });
                    break;
                case TextChunkKind.NativeToolCall:
                    if (chunk.ToolCall is { } call)
                    {
                        await send(new JObject
                        {
                            ["native_tool_call"] = new JObject
                            {
                                ["id"] = call.Id,
                                ["name"] = call.Name,
                                ["arguments"] = ParseJsonOrEmpty(call.Arguments)
                            }
                        });
                    }
                    break;
                case TextChunkKind.Status when chunk.Status?.Phase == ToolLoop.ToolResultPhase:
                {
                    JObject result = ParseJsonOrEmpty(chunk.Text?[ToolLoop.ToolResultPrefix.Length..]);
                    await send(new JObject
                    {
                        ["tool_result"] = new JObject
                        {
                            ["id"] = chunk.ToolCall?.Id,
                            ["name"] = chunk.ToolCall?.Name,
                            ["result"] = result
                        }
                    });
                    // Same "only a validated device action" rule LLMAssistantVoiceTurn applies: a rejected
                    // call would hand the device arguments the server refused to run.
                    if (chunk.ToolCall is { } resultCall && Tools.BuiltIn.DeviceActionTool.IsDeviceAction(resultCall.Name)
                        && result["success"]?.Value<bool>() == true)
                    {
                        deviceCalls.Add(new JObject { ["name"] = resultCall.Name, ["arguments"] = ParseJsonOrEmpty(resultCall.Arguments) });
                    }
                    break;
                }
                case TextChunkKind.StopReason:
                    finalStop = chunk.Stop;
                    break;
            }
        }
        return (spoken.ToString(), finalStop);
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
            JObject resolvedParams = AssistantService.ResolveParameters(assistantId, settings, session.User);
            ApplyParameters(input, resolvedParams, temperature, maxTokens, seed);
            // Load tools enabled for this assistant and inject their descriptions into the system prompt —
            // unless tool-calling is switched off for this thread/assistant (see EffectiveToolsEnabled).
            List<JObject> enabledTools = EffectiveToolsEnabled(thread, assistantId, settings, session.User)
                ? ToolRegistryService.GetEnabledTools(assistantId, settings, session.User)
                : [];
            await ApplyToolsToInput(input, enabledTools, session, assistantId, forceToolId);
            if (cts.IsCancellationRequested)
            {
                // Stopped during setup, before a single token was ever requested: nothing to persist.
                return;
            }
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
        // Small models return junk like `Assistant: **"Something` — take the first line, strip label
        // prefixes / markdown / quotes, and fall back to the default title if what's left isn't usable.
        string cleaned = (raw ?? "").Trim();
        cleaned = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault("").Trim();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"^(title|chat title|assistant|answer)\s*[:\-]\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        cleaned = cleaned.Replace("**", "").Replace("`", "").Trim().Trim('"', '\'', '*', '_', '.', ' ', '\u201C', '\u201D');
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ");
        if (cleaned.Length is < 3 or > 80 || !cleaned.Any(char.IsLetter))
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
        // HartsyLocalLLMProvider needs the per-request, per-model check (SupportsNativeToolCallingFor):
        // it serves whatever GGUF input.Model names, and the plain SupportsNativeToolCalling property can't
        // see that model at all (it's a provider-wide yes/no, not a per-request one — see that property's
        // own doc comment). Skipping the tag prompt for a non-Hermes model here would leave it with no way
        // to learn about tools at all: its own Jinja template would render them in its native, non-Hermes
        // convention, which the engine's installed Hermes-only filter cannot parse.
        bool nativeSupported = provider is HartsyLocalLLMProvider hartsyProvider
            ? hartsyProvider.SupportsNativeToolCallingFor(input.Model)
            : provider?.SupportsNativeToolCalling == true;
        if (nativeSupported)
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
                ApplyParameters(input, resolvedParams, temperature, maxTokens, seed);
                await ApplyToolsToInput(input, enabledTools, session, assistantId, forceToolId);
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
                // Isolate a lane failure — the other lane(s) must keep streaming. Surface as a lane-tagged error.
                Logs.Error($"[LLMAssistant] Compare lane {lane} ({L.Model}) failed: {ex.Message}");
                await SendJsonLane(socket, sendLock, lane, new JObject { ["error"] = ex.Message });
            }
        }

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
