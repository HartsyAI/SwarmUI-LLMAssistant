using System.Runtime.CompilerServices;
using System.Threading.Channels;
using HartsyInference.Engine.Requests;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.LLMs;

/// <summary>Turns a provider's JSON frame callback into the engine's <see cref="TextChunk"/> stream, so every provider can be
/// consumed the same way. Frames map as: <c>chunk</c> to <see cref="TextChunkKind.Chunk"/>, <c>native_tool_call</c> to
/// <see cref="TextChunkKind.NativeToolCall"/>, <c>stopReason</c> to <see cref="TextChunkKind.StopReason"/>, and <c>status</c> to
/// <see cref="TextChunkKind.Status"/>. Other frames are not part of the chunk vocabulary and are dropped.</summary>
public static class LLMChunkStream
{
    /// <summary>Runs <paramref name="produce"/> and streams the frames it emits. A run that ends without a stop reason ends with
    /// <see cref="StopReason.Stop"/>; a producer exception surfaces to the consumer.</summary>
    public static async IAsyncEnumerable<TextChunk> FromFrames(Func<Func<JObject, Task>, CancellationToken, Task> produce, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(produce);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Channel<TextChunk> channel = Channel.CreateUnbounded<TextChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        Task producer = Task.Run(async () =>
        {
            try
            {
                await produce(async frame =>
                {
                    if (ToChunk(frame) is { } chunk) await channel.Writer.WriteAsync(chunk, stop.Token).ConfigureAwait(false);
                }, stop.Token).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);
        bool sawStop = false;
        bool sawToolCall = false;
        try
        {
            await foreach (TextChunk chunk in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (chunk.Kind == TextChunkKind.StopReason) sawStop = true;
                if (chunk.Kind == TextChunkKind.NativeToolCall) sawToolCall = true;
                yield return chunk;
            }
        }
        finally
        {
            // The consumer stopped early or failed: stop the provider's request rather than let it run unobserved, then wait for it to unwind.
            stop.Cancel();
            await producer.ConfigureAwait(false);
        }
        // A turn that asked for a tool ends as ToolCall, not as a plain Stop, even when the provider sent no stop reason.
        if (!sawStop) yield return new TextChunk { Kind = TextChunkKind.StopReason, Stop = sawToolCall ? StopReason.ToolCall : StopReason.Stop };
    }

    /// <summary>The chunk a JSON frame stands for, or null when the frame is not part of the chunk vocabulary.</summary>
    public static TextChunk? ToChunk(JObject frame)
    {
        if (frame is null) return null;
        if (frame["chunk"] is { } text) return new TextChunk { Kind = TextChunkKind.Chunk, Text = text.ToString() };
        if (frame["native_tool_call"] is JObject call)
        {
            return new TextChunk
            {
                Kind = TextChunkKind.NativeToolCall,
                ToolCall = new NativeToolCall
                {
                    Id = call["id"]?.ToString() ?? "",
                    Name = call["name"]?.ToString() ?? "",
                    Arguments = call["arguments"] is JObject args ? args.ToString(Newtonsoft.Json.Formatting.None) : "{}",
                },
            };
        }
        if (frame["stopReason"] is { } reason) return new TextChunk { Kind = TextChunkKind.StopReason, Stop = ToStop(reason.ToString()) };
        if (frame["status"] is { } status) return new TextChunk { Kind = TextChunkKind.Status, Status = new TextStatus(status.ToString()) };
        return null;
    }

    private static StopReason ToStop(string reason) => reason switch
    {
        "length" => StopReason.Length,
        "cancelled" => StopReason.Cancelled,
        "error" => StopReason.Error,
        "tool_call" => StopReason.ToolCall,
        _ => StopReason.Stop,
    };
}
