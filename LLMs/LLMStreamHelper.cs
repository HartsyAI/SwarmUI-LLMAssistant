using System.Net.WebSockets;
using Hartsy.Extensions.LLMAssistant.Services;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.LLMs;

/// <summary>Chat entry point: runs one reply through <see cref="ToolTurnRunner"/> and streams it to the socket. Compare lanes share the
/// socket through <paramref name="sendLock"/>.</summary>
public static class LLMStreamHelper
{
    /// <summary>Streams one reply over a WebSocket and saves it to the thread. <paramref name="lane"/> is the compare-mode column (-1 for a
    /// single reply); <paramref name="parentMessageId"/> and <paramref name="compareGroupId"/> make compare lanes siblings;
    /// <paramref name="setActiveLeaf"/> is true for lane 0 only.</summary>
    public static Task StreamToWebSocket(WebSocket socket, ExtendedLLMInput input, Session session = null, string threadId = null, string assistantId = null, CancellationToken ct = default, string clientAssistantMessageId = null,
        int lane = -1, SemaphoreSlim sendLock = null, string parentMessageId = null, string compareGroupId = null, string deviceLabel = null, bool setActiveLeaf = true)
    {
        ToolTurnContext context = new()
        {
            Session = session,
            ThreadId = threadId,
            AssistantId = assistantId,
            ClientMessageId = clientAssistantMessageId,
            Lane = lane,
            ParentMessageId = parentMessageId,
            CompareGroupId = compareGroupId,
            DeviceLabel = deviceLabel,
            SetActiveLeaf = setActiveLeaf,
        };
        return ToolTurnRunner.RunAsync(input, context, new ChatWsSink(socket, lane, sendLock), ct);
    }
}
