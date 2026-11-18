using System.Text;
using System.Threading.Channels;
using Newtonsoft.Json.Linq;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Carries what one running tool prints to the chat as it goes. The tool's reader threads call <see cref="Enqueue"/> and never wait; one task sends the
/// lines on in order, joining those that piled up while the last frame was being written, so a noisy command costs a few frames rather than one per line.
/// <see cref="CompleteAsync"/> waits until everything queued was sent, so the output always reaches the client before the call's result.</summary>
public sealed class ToolOutputPump
{
    /// <summary>The most text streamed for one call; the result still carries what the tool kept, and the live view says it was cut.</summary>
    public const int MaxStreamedChars = 200_000;

    /// <summary>The longest single line streamed; a longer one is cut.</summary>
    public const int MaxLineChars = 4000;

    /// <summary>The queue between the tool's threads and the sender.</summary>
    private readonly Channel<(string Stream, string Text)> _queue = Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>The sender task.</summary>
    private readonly Task _sender;

    /// <summary>The call the output belongs to.</summary>
    private readonly string _callId, _toolName;

    /// <summary>Characters queued so far, and whether the cut note was queued.</summary>
    private long _queued;
    private int _cut;

    /// <summary>Starts a pump for the call <paramref name="callId"/> that sends frames through <paramref name="sink"/>.</summary>
    public ToolOutputPump(IToolTurnSink sink, string callId, string toolName)
    {
        _callId = callId;
        _toolName = toolName;
        _sender = Task.Run(() => SendLoopAsync(sink));
    }

    /// <summary>Queues one line of output. Safe to call from any thread, and never blocks.</summary>
    public void Enqueue(string stream, string line)
    {
        if (line is null)
        {
            return;
        }
        if (line.Length > MaxLineChars)
        {
            line = line[..MaxLineChars] + "…";
        }
        long total = Interlocked.Add(ref _queued, line.Length + 1);
        if (total > MaxStreamedChars)
        {
            if (Interlocked.Exchange(ref _cut, 1) == 0)
            {
                _queue.Writer.TryWrite(("stderr", "[live output stopped: it is long. The result keeps what fits.]"));
            }
            return;
        }
        _queue.Writer.TryWrite((stream, line));
    }

    /// <summary>Ends the stream and waits until what was queued has been sent.</summary>
    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await _sender;
    }

    /// <summary>Sends queued lines in order, each frame holding the consecutive lines of one stream that were waiting.</summary>
    private async Task SendLoopAsync(IToolTurnSink sink)
    {
        ChannelReader<(string Stream, string Text)> reader = _queue.Reader;
        (string Stream, string Text)? held = null;
        while (held is not null || await reader.WaitToReadAsync())
        {
            (string Stream, string Text) first = held ?? default;
            if (held is null && !reader.TryRead(out first))
            {
                continue;
            }
            held = null;
            StringBuilder text = new();
            text.Append(first.Text).Append('\n');
            while (reader.TryRead(out (string Stream, string Text) next))
            {
                if (next.Stream != first.Stream)
                {
                    held = next;
                    break;
                }
                text.Append(next.Text).Append('\n');
            }
            if (!sink.IsOpen)
            {
                continue;
            }
            try
            {
                await sink.ToolOutput(new JObject { ["id"] = _callId, ["name"] = _toolName, ["stream"] = first.Stream, ["text"] = text.ToString() });
            }
            catch (Exception ex)
            {
                // A live view is a convenience: a send that fails must not take the tool or the turn down with it.
                Logs.Debug($"[LLMAssistant] Streaming tool output failed: {ex.Message}");
            }
        }
    }
}
