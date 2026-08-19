using System.Net.WebSockets;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using Hartsy.Extensions.LLMAssistant.Services;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.LLMs;

/// <summary>Bridges GenerateLive callback output to WebSocket streaming, including an agentic
/// tool-calling loop: when the model emits &lt;tool_call&gt; blocks, the stream is interrupted,
/// tools execute, results are injected into history, and generation resumes for up to
/// <see cref="ToolConstants.MaxAgenticIterations"/> rounds. A CancellationTokenSource linked to
/// <c>Program.GlobalProgramCancel</c> is cancelled if the socket closes mid-stream; tool handlers
/// receive that token and must honor it.</summary>
public static class LLMStreamHelper
{
    /// <summary>Streams LLM generation output over a WebSocket connection. If <paramref name="threadId"/>
    /// is set and the session has a user, the reply (and any tool events) is appended to the stored
    /// thread on completion. <paramref name="assistantId"/> flows through to tool execution so
    /// per-assistant tool config takes effect.</summary>
    /// <param name="lane">Compare-mode column index (0/1/…). <c>-1</c> = normal single-model mode (no lane tag).</param>
    /// <param name="sendLock">Shared lock serializing socket writes when multiple lanes share one socket.</param>
    /// <param name="parentMessageId">When set, the reply is persisted as a child of THIS message (the user
    /// turn) rather than the current active leaf — required so compare lanes become siblings.</param>
    /// <param name="compareGroupId">Tags the persisted reply as part of a compare group (the renderer shows
    /// same-group siblings as side-by-side columns instead of a one-at-a-time pager).</param>
    /// <param name="deviceLabel">Compute device the model ran on (eg <c>cuda:0</c>/<c>cpu</c>/<c>cloud</c>), stored in meta.</param>
    /// <param name="setActiveLeaf">Whether this reply becomes the thread's active leaf (only lane 0 does).</param>
    public static async Task StreamToWebSocket(WebSocket socket, ExtendedLLMInput input, Session session = null, string threadId = null, string assistantId = null, CancellationToken ct = default, string clientAssistantMessageId = null,
        int lane = -1, SemaphoreSlim sendLock = null, string parentMessageId = null, string compareGroupId = null, string deviceLabel = null, bool setActiveLeaf = true)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(Program.GlobalProgramCancel, ct);
        bool hasTools = input.Tools is not null && input.Tools.Count > 0;
        DateTime startTime = DateTime.UtcNow;
        if (!hasTools)
        {
            await StreamSingleRound(socket, input, linked, session, threadId, startTime, clientAssistantMessageId, lane, sendLock, parentMessageId, compareGroupId, deviceLabel, setActiveLeaf);
            return;
        }
        // Agentic loop
        StringBuilder fullResponse = new();
        JArray toolEvents = [];
        for (int iteration = 0; iteration < ToolConstants.MaxAgenticIterations; iteration++)
        {
            if (SocketGone(socket) || linked.IsCancellationRequested)
            {
                linked.Cancel();
                Logs.Debug("[LLMAssistant] Stream cancelled (socket closed or token cancelled).");
                return;
            }
            if (iteration > 0)
            {
                await SendJson(socket, new JObject { ["iteration"] = iteration + 1 }, lane, sendLock);
            }
            StringBuilder roundBuffer = new();
            // A complete tool call ends the round early: cancelling this round-scoped token stops the
            // provider generating further instead of running to its token limit for nothing. Tail-window
            // scan (not a full-buffer re-scan) since this callback fires once per streamed chunk.
            ToolPromptService.TailWindowSentinel closeTagWatcher = new("</tool_call>");
            using CancellationTokenSource roundCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            bool toolCallDetected = false;
            string roundStopReason = null;
            // Populated by providers with ILLMProvider.SupportsNativeToolCalling (eg Anthropic's tool_use
            // blocks) — already-resolved, already-valid calls delivered as a discrete event instead of a
            // <tool_call> tag embedded in the text stream, so no tag-scanning/JSON-parsing is needed for them.
            List<ToolPromptService.ParsedToolCall> nativeToolCalls = [];
            try
            {
                await LLMDispatcher.GenerateStreaming(input, async chunk =>
                {
                    if (toolCallDetected || SocketGone(socket))
                    {
                        return;
                    }
                    if (chunk.TryGetValue("chunk", out JToken chunkToken))
                    {
                        string text = chunkToken.ToString();
                        roundBuffer.Append(text);
                        // Send before the cancel below so the </tool_call> chunk reaches the UI.
                        await SendJson(socket, new JObject { ["chunk"] = text }, lane, sendLock);
                        if (closeTagWatcher.Feed(text))
                        {
                            toolCallDetected = true;
                            roundCts.Cancel();
                        }
                    }
                    else if (chunk.TryGetValue("native_tool_call", out JToken nativeToken) && nativeToken is JObject nativeCall)
                    {
                        nativeToolCalls.Add(new ToolPromptService.ParsedToolCall
                        {
                            Id = nativeCall["id"]?.ToString() ?? $"call-{Guid.NewGuid():N}",
                            Name = nativeCall["name"]?.ToString(),
                            Arguments = nativeCall["arguments"] as JObject ?? new JObject(),
                        });
                        // Same early-stop semantics as the tag convention: one resolved call ends the round
                        // (execute it, feed the result back, let the model decide whether to call another).
                        toolCallDetected = true;
                        roundCts.Cancel();
                    }
                    else if (chunk.TryGetValue("result", out JToken resultToken))
                    {
                        roundBuffer.Clear();
                        roundBuffer.Append(resultToken.ToString());
                    }
                    else if (chunk.TryGetValue("status", out JToken statusToken))
                    {
                        // Forward backend status events (eg "loading_model") so the UI can show a spinner.
                        await SendJson(socket, chunk, lane, sendLock);
                    }
                    else if (chunk.TryGetValue("stopReason", out JToken stopReasonToken))
                    {
                        // Only meaningful on the round that actually finishes the reply (below); a tool-call
                        // round hitting this is unlikely (tool calls are short) and gets overwritten anyway.
                        roundStopReason = stopReasonToken.ToString();
                    }
                }, roundCts.Token);
            }
            catch (OperationCanceledException) when (toolCallDetected && !linked.IsCancellationRequested)
            {
                // Expected: the round was cancelled because a tool call completed — fall through and
                // execute it. A real user-Stop / socket-close cancels `linked` and isn't caught here.
            }
            if (SocketGone(socket))
            {
                linked.Cancel();
                return;
            }
            string roundText = roundBuffer.ToString();
            List<ToolPromptService.ParsedToolCall> toolCalls = ToolPromptService.ParseToolCalls(roundText, out List<string> malformedCalls);
            if (nativeToolCalls.Count > 0)
            {
                toolCalls.AddRange(nativeToolCalls);
            }
            if (toolCalls.Count == 0 && malformedCalls.Count == 0)
            {
                fullResponse.Append(roundText);
                PersistAssistantMessage(session, threadId, fullResponse.ToString(), toolEvents, input.Model, startTime, stopReason: roundStopReason, clientMessageId: clientAssistantMessageId, parentMessageId: parentMessageId, compareGroupId: compareGroupId, lane: lane, deviceLabel: deviceLabel, setActiveLeaf: setActiveLeaf);
                await SendJson(socket, new JObject
                {
                    ["done"] = true,
                    ["full_text"] = fullResponse.ToString(),
                    ["stopReason"] = roundStopReason
                }, lane, sendLock);
                return;
            }
            fullResponse.Append(roundText);
            input.Messages.Add(new LLMMessage() { Role = LLMRoles.Assistant, Content = roundText });
            foreach (string rawMalformed in malformedCalls)
            {
                // Previously these vanished silently — the model had no idea its call didn't register and
                // no way to retry. Feed an error result back exactly like a real failed tool execution.
                Logs.Debug($"[LLMAssistant] Malformed <tool_call> JSON dropped, feeding retry hint: {rawMalformed}");
                string formatted = ToolPromptService.FormatToolResult("tool_call", new JObject
                {
                    ["success"] = false,
                    ["error"] = "Could not parse this tool call — invalid or incomplete JSON. Re-emit it as a single well-formed <tool_call>{\"name\":\"...\",\"arguments\":{...}}</tool_call> block."
                });
                input.Messages.Add(new LLMMessage() { Role = LLMRoles.User, Content = formatted });
            }
            foreach (ToolPromptService.ParsedToolCall call in toolCalls)
            {
                if (SocketGone(socket) || linked.IsCancellationRequested)
                {
                    linked.Cancel();
                    return;
                }
                await SendJson(socket, new JObject
                {
                    ["tool_call"] = new JObject
                    {
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments
                    }
                }, lane, sendLock);
                JObject result;
                try
                {
                    result = await ToolExecutorService.ExecuteTool(call.Name, call.Arguments, session, assistantId, threadId, input.Model, linked.Token);
                }
                catch (OperationCanceledException)
                {
                    Logs.Debug($"[LLMAssistant] Tool {call.Name} cancelled.");
                    return;
                }
                catch (Exception ex)
                {
                    Logs.Error($"[LLMAssistant] Tool execution error: {ex.Message}");
                    result = new JObject { ["success"] = false, ["error"] = ex.Message };
                }
                await SendJson(socket, new JObject
                {
                    ["tool_result"] = new JObject
                    {
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["result"] = result
                    }
                }, lane, sendLock);
                toolEvents.Add(new JObject
                {
                    ["id"] = call.Id,
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments,
                    ["result"] = result
                });
                // Feed the result back as a user-role message so the model sees it next round.
                string formatted = ToolPromptService.FormatToolResult(call.Name, result);
                input.Messages.Add(new LLMMessage() { Role = LLMRoles.User, Content = formatted });
            }
        }
        PersistAssistantMessage(session, threadId, fullResponse.ToString(), toolEvents, input.Model, startTime, truncated: true, reason: "max_iterations", clientMessageId: clientAssistantMessageId, parentMessageId: parentMessageId, compareGroupId: compareGroupId, lane: lane, deviceLabel: deviceLabel, setActiveLeaf: setActiveLeaf);
        await SendJson(socket, new JObject
        {
            ["done"] = true,
            ["truncated"] = true,
            ["reason"] = "max_iterations",
            ["full_text"] = fullResponse.ToString()
        }, lane, sendLock);
    }

    /// <summary>Streams a single round of generation with no tool processing.</summary>
    private static async Task StreamSingleRound(WebSocket socket, ExtendedLLMInput input, CancellationTokenSource linked, Session session, string threadId, DateTime startTime, string clientAssistantMessageId = null,
        int lane = -1, SemaphoreSlim sendLock = null, string parentMessageId = null, string compareGroupId = null, string deviceLabel = null, bool setActiveLeaf = true)
    {
        StringBuilder fullText = new();
        string stopReason = null;
        await LLMDispatcher.GenerateStreaming(input, async chunk =>
        {
            if (SocketGone(socket))
            {
                return;
            }
            if (chunk.TryGetValue("chunk", out JToken chunkToken))
            {
                string text = chunkToken.ToString();
                fullText.Append(text);
                await SendJson(socket, new JObject { ["chunk"] = text }, lane, sendLock);
            }
            else if (chunk.TryGetValue("result", out JToken resultToken))
            {
                fullText.Clear();
                fullText.Append(resultToken.ToString());
            }
            else if (chunk.TryGetValue("status", out JToken _))
            {
                // Backend status events (eg model load progress) — forward verbatim to the UI.
                await SendJson(socket, chunk, lane, sendLock);
            }
            else if (chunk.TryGetValue("stopReason", out JToken stopReasonToken))
            {
                stopReason = stopReasonToken.ToString();
            }
        }, linked.Token);
        if (SocketGone(socket))
        {
            return;
        }
        PersistAssistantMessage(session, threadId, fullText.ToString(), [], input.Model, startTime, stopReason: stopReason, clientMessageId: clientAssistantMessageId, parentMessageId: parentMessageId, compareGroupId: compareGroupId, lane: lane, deviceLabel: deviceLabel, setActiveLeaf: setActiveLeaf);
        await SendJson(socket, new JObject
        {
            ["done"] = true,
            ["full_text"] = fullText.ToString(),
            ["stopReason"] = stopReason
        }, lane, sendLock);
    }

    /// <summary>True if the socket is no longer in the Open state — client gone, server should bail.</summary>
    private static bool SocketGone(WebSocket socket)
    {
        return socket.State != WebSocketState.Open;
    }

    /// <summary>Strips <c>&lt;tool_call&gt;</c> and <c>&lt;tool_result&gt;</c> blocks from raw model output to
    /// produce the cleaned, user-facing assistant text. The <c>toolCalls</c> array on the saved
    /// message preserves the structured tool data separately.</summary>
    private static readonly System.Text.RegularExpressions.Regex ToolTagRegex =
        new(@"<tool_call>[\s\S]*?</tool_call>|<tool_result\b[^>]*>[\s\S]*?</tool_result>",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Removes unpaired UTF-16 surrogates (lone high/low halves) that would otherwise fail to
    /// encode when the thread is persisted. Valid surrogate pairs (real emoji) are preserved.</summary>
    private static string StripLoneSurrogates(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        int firstBad = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
                firstBad = i;
                break;
            }
            if (char.IsLowSurrogate(c)) { firstBad = i; break; }
        }
        if (firstBad < 0)
        {
            return text;
        }
        StringBuilder sb = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(c).Append(text[i + 1]); i++; }
                continue;
            }
            if (char.IsLowSurrogate(c)) { continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Appends the just-generated assistant message to the saved thread.
    /// No-ops if user/threadId are missing (eg non-chat callers).</summary>
    private static void PersistAssistantMessage(Session session, string threadId, string rawText, JArray toolEvents, string model, DateTime startTime, bool truncated = false, string reason = null, string stopReason = null, string clientMessageId = null,
        string parentMessageId = null, string compareGroupId = null, int lane = -1, string deviceLabel = null, bool setActiveLeaf = true)
    {
        if (session?.User is null || string.IsNullOrEmpty(threadId))
        {
            return;
        }
        try
        {
            // Streamed emoji/CJK can split across chunk boundaries, leaving a lone UTF-16 surrogate that
            // the storage layer can't encode ("Unable to translate Unicode character …"). Scrub both fields.
            rawText = StripLoneSurrogates(rawText);
            string clean = ToolTagRegex.Replace(rawText ?? "", "").Trim();
            double genSeconds = (DateTime.UtcNow - startTime).TotalSeconds;
            JObject meta = new()
            {
                ["model"] = model ?? "",
                ["genTime"] = Math.Round(genSeconds, 2),
                ["truncated"] = truncated,
                ["reason"] = reason,
                // "length" = the provider cut the reply off at the max-tokens cap rather than a natural stop.
                ["stopReason"] = stopReason
            };
            if (!string.IsNullOrEmpty(deviceLabel))
            {
                meta["device"] = deviceLabel;
            }
            JObject msg = new()
            {
                ["role"] = "assistant",
                ["content"] = clean,
                ["rawContent"] = rawText ?? "",
                ["toolCalls"] = toolEvents ?? [],
                ["meta"] = meta
            };
            if (!string.IsNullOrEmpty(clientMessageId))
            {
                msg["id"] = clientMessageId;
            }
            // Compare-mode tag: same group → rendered as side-by-side columns; lane → column index.
            if (!string.IsNullOrEmpty(compareGroupId))
            {
                msg["groupId"] = compareGroupId;
            }
            if (lane >= 0)
            {
                msg["lane"] = lane;
            }
            // Compare replies hang off the explicit user-turn parent (so the lanes are siblings); only
            // lane 0 advances the active leaf. Normal replies fall back to the active-leaf append.
            if (!string.IsNullOrEmpty(parentMessageId))
            {
                ThreadStorageService.AppendChild(session.User, threadId, parentMessageId, msg, setActiveLeaf);
            }
            else
            {
                ThreadStorageService.AppendMessage(session.User, threadId, msg);
            }
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Failed to persist assistant message to thread {threadId}: {ex.Message}");
        }
    }

    /// <summary>Serializes one event to the socket. In compare mode every event is tagged with its
    /// <paramref name="lane"/> (so the client can route it to the right column) and all writes are
    /// serialized through <paramref name="sendLock"/> — concurrent <c>SendAsync</c> on one socket would
    /// interleave frames and corrupt the stream.</summary>
    private static async Task SendJson(WebSocket socket, JObject data, int lane = -1, SemaphoreSlim sendLock = null)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        if (lane >= 0)
        {
            data["lane"] = lane;
        }
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data.ToString(Newtonsoft.Json.Formatting.None));
        if (sendLock is not null)
        {
            await sendLock.WaitAsync();
        }
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        finally
        {
            sendLock?.Release();
        }
    }
}
