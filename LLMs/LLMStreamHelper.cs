using System.Net.WebSockets;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>Bridges GenerateLive callback output to WebSocket streaming.
/// Supports an agentic tool-calling loop: when the model emits &lt;tool_call&gt; blocks,
/// the stream is interrupted, tools execute, results are injected into history, and
/// generation resumes for up to <see cref="ToolConstants.MaxAgenticIterations"/> rounds.
/// <para>Cancellation: a linked CancellationTokenSource is created from
/// <c>Program.GlobalProgramCancel</c>. Between iterations and tool calls we check
/// <c>socket.State</c> and cancel if the client has gone away — adopted from T2IAPI's
/// <c>GenerateText2ImageWS</c> pattern. Tool handlers receive the linked token and must
/// honor it; long-running ones (HTTP, shell exec, file IO) all do.</para></summary>
public static class LLMStreamHelper
{
    /// <summary>Streams LLM generation output over a WebSocket connection.
    /// If <paramref name="threadId"/> is non-null and the session has a user, the assistant's
    /// reply (and any tool events that happened during the agentic loop) is appended to the
    /// stored thread when generation completes — server is authoritative for chat history.
    /// <paramref name="assistantId"/> flows through to tool execution so per-assistant tool
    /// config (eg <c>generate_image</c>'s default preset) takes effect.</summary>
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
            // A complete tool call ends the round early: cancel a round-scoped token so the provider
            // actually stops generating (reusing the cancellation path every provider already honors)
            // instead of running to its token limit while we discard the output.
            using CancellationTokenSource roundCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            bool toolCallDetected = false;
            try
            {
                await LLMDispatcher.GenerateStreaming(input, chunk =>
                {
                    if (toolCallDetected || SocketGone(socket))
                    {
                        return;
                    }
                    if (chunk.TryGetValue("chunk", out JToken chunkToken))
                    {
                        string text = chunkToken.ToString();
                        roundBuffer.Append(text);
                        // Send on `linked` (not `roundCts`) so the chunk carrying </tool_call> still
                        // reaches the UI before we cancel the round.
                        SendJson(socket, new JObject { ["chunk"] = text }, lane, sendLock).Wait(linked.Token);
                        if (ToolPromptService.ContainsCompleteToolCall(roundBuffer.ToString()))
                        {
                            toolCallDetected = true;
                            roundCts.Cancel();
                        }
                    }
                    else if (chunk.TryGetValue("result", out JToken resultToken))
                    {
                        roundBuffer.Clear();
                        roundBuffer.Append(resultToken.ToString());
                    }
                    else if (chunk.TryGetValue("status", out JToken statusToken))
                    {
                        // Forward backend-side status events (eg "loading_model" / "model_ready") to the
                        // client so the UI can show a spinner during slow loads.
                        SendJson(socket, chunk, lane, sendLock).Wait(linked.Token);
                    }
                }, roundCts.Token);
            }
            catch (OperationCanceledException) when (toolCallDetected && !linked.IsCancellationRequested)
            {
                // Expected: we cancelled the round because a complete tool call arrived — fall through to
                // parse and execute it. (A real user-Stop / socket-close cancels `linked` and is not caught.)
            }
            if (SocketGone(socket))
            {
                linked.Cancel();
                return;
            }
            string roundText = roundBuffer.ToString();
            List<ToolPromptService.ParsedToolCall> toolCalls = ToolPromptService.ParseToolCalls(roundText);
            if (toolCalls.Count == 0)
            {
                fullResponse.Append(roundText);
                PersistAssistantMessage(session, threadId, fullResponse.ToString(), toolEvents, input.Model, startTime, clientMessageId: clientAssistantMessageId, parentMessageId: parentMessageId, compareGroupId: compareGroupId, lane: lane, deviceLabel: deviceLabel, setActiveLeaf: setActiveLeaf);
                await SendJson(socket, new JObject
                {
                    ["done"] = true,
                    ["full_text"] = fullResponse.ToString()
                }, lane, sendLock);
                return;
            }
            // Append round output (with tool call tags) to the accumulated response and to history
            fullResponse.Append(roundText);
            input.Messages.Add(new LLMMessage() { Role = LLMRoles.Assistant, Content = roundText });
            // Execute each tool call and feed the result back
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
                // Record for thread persistence at end of stream
                toolEvents.Add(new JObject
                {
                    ["id"] = call.Id,
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments,
                    ["result"] = result
                });
                // Inject formatted result back into history as a user-role message so the model sees it
                string formatted = ToolPromptService.FormatToolResult(call.Name, result);
                input.Messages.Add(new LLMMessage() { Role = LLMRoles.User, Content = formatted });
            }
            // Check for terminal completion (no tool calls in roundText) and persist before returning.
            // (handled at top of loop on roundText.Count==0 path above)
        }
        // Hit max iterations without finishing
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
        await LLMDispatcher.GenerateStreaming(input, chunk =>
        {
            if (SocketGone(socket))
            {
                return;
            }
            if (chunk.TryGetValue("chunk", out JToken chunkToken))
            {
                string text = chunkToken.ToString();
                fullText.Append(text);
                SendJson(socket, new JObject { ["chunk"] = text }, lane, sendLock).Wait(linked.Token);
            }
            else if (chunk.TryGetValue("result", out JToken resultToken))
            {
                fullText.Clear();
                fullText.Append(resultToken.ToString());
            }
            else if (chunk.TryGetValue("status", out JToken _))
            {
                // Backend status events (eg model load progress) — forward verbatim to the UI.
                SendJson(socket, chunk, lane, sendLock).Wait(linked.Token);
            }
        }, linked.Token);
        if (SocketGone(socket))
        {
            return;
        }
        PersistAssistantMessage(session, threadId, fullText.ToString(), [], input.Model, startTime, clientMessageId: clientAssistantMessageId, parentMessageId: parentMessageId, compareGroupId: compareGroupId, lane: lane, deviceLabel: deviceLabel, setActiveLeaf: setActiveLeaf);
        await SendJson(socket, new JObject
        {
            ["done"] = true,
            ["full_text"] = fullText.ToString()
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
    private static void PersistAssistantMessage(Session session, string threadId, string rawText, JArray toolEvents, string model, DateTime startTime, bool truncated = false, string reason = null, string clientMessageId = null,
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
                ["reason"] = reason
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
