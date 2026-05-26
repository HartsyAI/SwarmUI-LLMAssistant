using System.Net.WebSockets;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.LLMs;
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
    public static async Task StreamToWebSocket(WebSocket socket, ExtendedLLMInput input, Session session = null, string threadId = null, string assistantId = null, CancellationToken ct = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(Program.GlobalProgramCancel, ct);
        bool hasTools = input.Tools is not null && input.Tools.Count > 0;
        DateTime startTime = DateTime.UtcNow;
        if (!hasTools)
        {
            await StreamSingleRound(socket, input, linked, session, threadId, startTime);
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
                await SendJson(socket, new JObject { ["iteration"] = iteration + 1 });
            }
            StringBuilder roundBuffer = new();
            bool interrupted = false;
            await LLMDispatcher.GenerateStreaming(input, chunk =>
            {
                if (interrupted || SocketGone(socket))
                {
                    return;
                }
                if (chunk.TryGetValue("chunk", out JToken chunkToken))
                {
                    string text = chunkToken.ToString();
                    roundBuffer.Append(text);
                    SendJson(socket, new JObject { ["chunk"] = text }).Wait(linked.Token);
                    if (ToolPromptService.ContainsCompleteToolCall(roundBuffer.ToString()))
                    {
                        interrupted = true;
                    }
                }
                else if (chunk.TryGetValue("result", out JToken resultToken))
                {
                    roundBuffer.Clear();
                    roundBuffer.Append(resultToken.ToString());
                }
                else if (chunk.TryGetValue("status", out JToken statusToken))
                {
                    // Forward backend-side status events (eg LlamaSharp's "loading_model" /
                    // "model_ready") to the client so the UI can show a spinner during slow loads.
                    SendJson(socket, chunk).Wait(linked.Token);
                }
            }, linked.Token);
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
                PersistAssistantMessage(session, threadId, fullResponse.ToString(), toolEvents, input.Model, startTime);
                await SendJson(socket, new JObject
                {
                    ["done"] = true,
                    ["full_text"] = fullResponse.ToString()
                });
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
                });
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
                });
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
        PersistAssistantMessage(session, threadId, fullResponse.ToString(), toolEvents, input.Model, startTime, truncated: true, reason: "max_iterations");
        await SendJson(socket, new JObject
        {
            ["done"] = true,
            ["truncated"] = true,
            ["reason"] = "max_iterations",
            ["full_text"] = fullResponse.ToString()
        });
    }

    /// <summary>Streams a single round of generation with no tool processing.</summary>
    private static async Task StreamSingleRound(WebSocket socket, ExtendedLLMInput input, CancellationTokenSource linked, Session session, string threadId, DateTime startTime)
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
                SendJson(socket, new JObject { ["chunk"] = text }).Wait(linked.Token);
            }
            else if (chunk.TryGetValue("result", out JToken resultToken))
            {
                fullText.Clear();
                fullText.Append(resultToken.ToString());
            }
            else if (chunk.TryGetValue("status", out JToken _))
            {
                // Backend status events (eg model load progress) — forward verbatim to the UI.
                SendJson(socket, chunk).Wait(linked.Token);
            }
        }, linked.Token);
        if (SocketGone(socket))
        {
            return;
        }
        PersistAssistantMessage(session, threadId, fullText.ToString(), [], input.Model, startTime);
        await SendJson(socket, new JObject
        {
            ["done"] = true,
            ["full_text"] = fullText.ToString()
        });
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

    /// <summary>Appends the just-generated assistant message to the saved thread.
    /// No-ops if user/threadId are missing (eg non-chat callers).</summary>
    private static void PersistAssistantMessage(Session session, string threadId, string rawText, JArray toolEvents, string model, DateTime startTime, bool truncated = false, string reason = null)
    {
        if (session?.User is null || string.IsNullOrEmpty(threadId))
        {
            return;
        }
        try
        {
            string clean = ToolTagRegex.Replace(rawText ?? "", "").Trim();
            double genSeconds = (DateTime.UtcNow - startTime).TotalSeconds;
            JObject msg = new()
            {
                ["role"] = "assistant",
                ["content"] = clean,
                ["rawContent"] = rawText ?? "",
                ["toolCalls"] = toolEvents ?? [],
                ["meta"] = new JObject
                {
                    ["model"] = model ?? "",
                    ["genTime"] = Math.Round(genSeconds, 2),
                    ["truncated"] = truncated,
                    ["reason"] = reason
                }
            };
            ThreadStorageService.AppendMessage(session.User, threadId, msg);
        }
        catch (Exception ex)
        {
            Logs.Error($"[LLMAssistant] Failed to persist assistant message to thread {threadId}: {ex.Message}");
        }
    }

    private static async Task SendJson(WebSocket socket, JObject data)
    {
        if (socket.State == WebSocketState.Open)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data.ToString(Newtonsoft.Json.Formatting.None));
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
