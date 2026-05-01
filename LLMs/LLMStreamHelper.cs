using System.Net.WebSockets;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Extensions.LLMAssistant.Services;
using SwarmUI.LLMs;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>Bridges GenerateLive callback output to WebSocket streaming.
/// Supports an agentic tool-calling loop: when the model emits &lt;tool_call&gt; blocks,
/// the stream is interrupted, tools execute, results are injected into history, and
/// generation resumes for up to <see cref="ToolConstants.MaxAgenticIterations"/> rounds.</summary>
public static class LLMStreamHelper
{
    /// <summary>Streams LLM generation output over a WebSocket connection.</summary>
    public static async Task StreamToWebSocket(WebSocket socket, ExtendedLLMInput input, Session session = null, CancellationToken ct = default)
    {
        bool hasTools = input.Tools is not null && input.Tools.Count > 0;
        if (!hasTools)
        {
            await StreamSingleRound(socket, input, ct);
            return;
        }
        // Agentic loop
        StringBuilder fullResponse = new();
        for (int iteration = 0; iteration < ToolConstants.MaxAgenticIterations; iteration++)
        {
            if (iteration > 0)
            {
                await SendJson(socket, new JObject { ["iteration"] = iteration + 1 });
            }
            StringBuilder roundBuffer = new();
            bool interrupted = false;
            await LLMDispatcher.GenerateStreaming(input, chunk =>
            {
                if (interrupted)
                {
                    return;
                }
                if (chunk.TryGetValue("chunk", out JToken chunkToken))
                {
                    string text = chunkToken.ToString();
                    roundBuffer.Append(text);
                    SendJson(socket, new JObject { ["chunk"] = text }).Wait(ct);
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
            }, ct);
            string roundText = roundBuffer.ToString();
            List<ToolPromptService.ParsedToolCall> toolCalls = ToolPromptService.ParseToolCalls(roundText);
            if (toolCalls.Count == 0)
            {
                fullResponse.Append(roundText);
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
                    result = await ToolExecutorService.ExecuteTool(call.Name, call.Arguments, session, ct);
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
                // Inject formatted result back into history as a user-role message so the model sees it
                string formatted = ToolPromptService.FormatToolResult(call.Name, result);
                input.Messages.Add(new LLMMessage() { Role = LLMRoles.User, Content = formatted });
            }
            // Loop: regenerate with extended history
        }
        // Hit max iterations without finishing
        await SendJson(socket, new JObject
        {
            ["done"] = true,
            ["truncated"] = true,
            ["reason"] = "max_iterations",
            ["full_text"] = fullResponse.ToString()
        });
    }

    /// <summary>Streams a single round of generation with no tool processing.</summary>
    private static async Task StreamSingleRound(WebSocket socket, ExtendedLLMInput input, CancellationToken ct)
    {
        StringBuilder fullText = new();
        await LLMDispatcher.GenerateStreaming(input, chunk =>
        {
            if (chunk.TryGetValue("chunk", out JToken chunkToken))
            {
                string text = chunkToken.ToString();
                fullText.Append(text);
                SendJson(socket, new JObject { ["chunk"] = text }).Wait(ct);
            }
            else if (chunk.TryGetValue("result", out JToken resultToken))
            {
                fullText.Clear();
                fullText.Append(resultToken.ToString());
            }
        }, ct);
        await SendJson(socket, new JObject
        {
            ["done"] = true,
            ["full_text"] = fullText.ToString()
        });
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
