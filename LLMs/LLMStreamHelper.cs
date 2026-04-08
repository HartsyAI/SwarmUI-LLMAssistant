using System.Net.WebSockets;
using Newtonsoft.Json.Linq;
using SwarmUI.WebAPI;

namespace SwarmUI.Extensions.LLMAssistant.LLMs;

/// <summary>Bridges GenerateLive callback output to WebSocket streaming.</summary>
public static class LLMStreamHelper
{
    /// <summary>Streams LLM generation output over a WebSocket connection.</summary>
    public static async Task StreamToWebSocket(WebSocket socket, ExtendedLLMInput input, int backendId = -1, CancellationToken ct = default)
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
        }, backendId, ct);
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
