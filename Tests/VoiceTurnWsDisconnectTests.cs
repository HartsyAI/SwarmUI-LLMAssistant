using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Pins <see cref="ChatEndpoints.WatchForDisconnectAsync"/> — the fix for a real, independent-review-found
/// bug in <see cref="ChatEndpoints.LLMAssistantVoiceTurnWS"/>'s WebSocket lifecycle. The earlier version started
/// the background "watch for a disconnect" receive with a token tied to the turn's own lifetime and cancelled it
/// once the turn finished, intending to "clean up" the dangling receive. Cancelling a pending
/// <c>WebSocket.ReceiveAsync</c> instead aborts the whole socket (undocumented behavior, not a guess — this file
/// reproduces it against a real loopback socket, not a mock), and SwarmUI's own request handler calls
/// <c>socket.CloseAsync(...)</c> unconditionally right after every WS route returns; an <c>Aborted</c> socket is
/// not a state <c>CloseAsync</c> accepts. The reviewer's empirical repro found this threw on roughly 41% of
/// otherwise-completely-normal turns — a coin-flip failure rate that unit tests against a fake/mocked
/// <see cref="WebSocket"/> would never catch, because the bug lives entirely in the real
/// <c>ManagedWebSocket</c>'s state machine, not in this extension's own code. Every test here therefore runs a
/// real <see cref="HttpListener"/> server socket against a real <see cref="ClientWebSocket"/> client over
/// loopback TCP — the same shape the reviewer's repro used — rather than a fake or mocked socket.
///
/// <para>Covers the three scenarios the fix needs to get right: (a) 200 repetitions of a normal
/// turn-finishes-then-framework-closes completion, asserting zero exceptions and that the socket ends
/// <see cref="WebSocketState.Closed"/> (not merely "doesn't throw once" — the original bug was intermittent,
/// so one passing iteration proves nothing); (b) an ungraceful client abort mid-turn cancels the turn's own
/// cancellation token promptly; (c) the same abort, while something is actively awaiting that exact token
/// (standing in for a long-running tool handler — <c>ChatEndpoints.cs:514</c> hands <c>turnCancel.Token</c>
/// straight into <c>StreamToolLoopAsync</c>, which forwards it hop by hop into every dispatched tool call; see
/// that test's own comment for the full chain), also observes cancellation. All three run in well under the
/// 10-second budget — see each test's own timing notes.</para></summary>
public class VoiceTurnWsDisconnectTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _wsUri;

    public VoiceTurnWsDisconnectTests()
    {
        int port = GetFreeLoopbackPort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _wsUri = $"ws://127.0.0.1:{port}/";
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* already stopped/disposed */ }
        _listener.Close();
    }

    /// <summary>Binds port 0 just to let the OS hand back an unused loopback port, then immediately releases it
    /// for <see cref="_listener"/> to bind for real. Carries the same tiny reuse-race every "find a free port
    /// for a test" helper does; acceptable for a loopback-only dev-box test, same as the engine repo's other
    /// real-socket tests.</summary>
    private static int GetFreeLoopbackPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Opens one real client/server WebSocket pair over loopback: a <see cref="ClientWebSocket"/>
    /// connecting to this fixture's <see cref="HttpListener"/>, upgraded server-side via
    /// <see cref="HttpListenerContext.AcceptWebSocketAsync(string)"/>. The accept and the connect are started
    /// concurrently and interleaved deliberately: <see cref="HttpListener.GetContextAsync"/> resolves once the
    /// upgrade request's headers arrive (before any response is sent), so the 101 response that actually
    /// completes the client's <see cref="ClientWebSocket.ConnectAsync"/> task is sent by
    /// <c>AcceptWebSocketAsync</c> in between -- awaiting the connect task first would deadlock.</summary>
    private async Task<(WebSocket Server, ClientWebSocket Client)> OpenPairAsync()
    {
        ClientWebSocket client = new();
        Task<HttpListenerContext> acceptTask = _listener.GetContextAsync();
        Task connectTask = client.ConnectAsync(new Uri(_wsUri), CancellationToken.None);
        HttpListenerContext context = await acceptTask;
        HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
        await connectTask;
        return (wsContext.WebSocket, client);
    }

    /// <summary>Stands in for a real client's (eg a browser's) automatic echo of a server-initiated close
    /// frame: <see cref="ClientWebSocket"/> does not do this on its own, so without a loop like this one,
    /// nothing would ever complete the close handshake the server's <c>CloseAsync</c> is waiting on.</summary>
    private static async Task EchoCloseWhenReceivedAsync(ClientWebSocket client)
    {
        byte[] buffer = new byte[16];
        while (client.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            }
            catch
            {
                return;
            }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch { /* server side may already be gone by the time this send lands; not this test's concern */ }
                return;
            }
        }
    }

    [Fact]
    public async Task NormalCompletion_RepeatedTwoHundredTimes_NeverThrowsAndSocketEndsClosed()
    {
        // (a) The exact shape that threw ~41% of the time before the fix: WatchForDisconnectAsync's receive
        // is left outstanding (never cancelled), the "turn" is already done, and the framework calls
        // CloseAsync unconditionally right after. 200 repetitions, not 1 -- an intermittent ~41% failure needs
        // a real sample size to disprove; one green iteration would not have caught the original bug either.
        //
        // Run concurrently, not in a sequential loop: a real client's own close-echo (ClientWebSocket
        // responding to an already-received Close frame) costs a fixed ~1 second of wall clock on this
        // runtime/platform regardless of CloseAsync vs CloseOutputAsync -- measured directly, not assumed; it
        // is not CPU-bound and not specific to this fix. 200 of those sequentially would be ~200 seconds, far
        // past this test's 10-second budget; 200 concurrently pay that fixed cost once, in parallel.
        Task<(Exception Thrown, WebSocketState FinalState)>[] iterations = new Task<(Exception, WebSocketState)>[200];
        for (int i = 0; i < iterations.Length; i++)
        {
            iterations[i] = RunOneNormalCompletionAsync();
        }
        (Exception Thrown, WebSocketState FinalState)[] results = await Task.WhenAll(iterations);
        for (int i = 0; i < results.Length; i++)
        {
            Assert.Null(results[i].Thrown);
            Assert.Equal(WebSocketState.Closed, results[i].FinalState);
        }
    }

    private async Task<(Exception Thrown, WebSocketState FinalState)> RunOneNormalCompletionAsync()
    {
        (WebSocket server, ClientWebSocket client) = await OpenPairAsync();
        try
        {
            _ = ChatEndpoints.WatchForDisconnectAsync(server, () => { });
            Task clientAck = EchoCloseWhenReceivedAsync(client);
            Exception thrown = await Record.ExceptionAsync(
                () => server.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None));
            WebSocketState finalState = server.State;
            await clientAck;
            return (thrown, finalState);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task ClientAbortsMidStream_CancelsTheTurnTokenWithinTheBudget()
    {
        // (b) Unlike the normal-completion scenario, nothing calls CloseAsync here -- the turn is still
        // actively using the socket (mid-stream) when the client goes away.
        (WebSocket server, ClientWebSocket client) = await OpenPairAsync();
        try
        {
            using CancellationTokenSource turnCancel = new();
            TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            turnCancel.Token.Register(() => cancelled.TrySetResult(true));
            _ = ChatEndpoints.WatchForDisconnectAsync(server, () =>
            {
                try { turnCancel.Cancel(); }
                catch (ObjectDisposedException) { }
            });

            Stopwatch elapsed = Stopwatch.StartNew();
            client.Abort(); // ungraceful: no close handshake -- a dropped connection or a closed tab, not a clean end
            Task finished = await Task.WhenAny(cancelled.Task, Task.Delay(TimeSpan.FromMilliseconds(100)));
            elapsed.Stop();

            Assert.True(ReferenceEquals(finished, cancelled.Task),
                $"turnCancel was not cancelled within the 100ms budget (elapsed {elapsed.ElapsedMilliseconds}ms).");
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task ClientAbortsWhileToolHandlerRuns_CancelsTheHandlersOwnToken()
    {
        // (c) ChatEndpoints.cs:514 hands turnCancel.Token to StreamToolLoopAsync; HartsyLocalLLMProvider.cs's
        // StreamToolLoopAsync forwards that exact token, unchanged, into ToolLoop.RunAsync (HartsyInference.Tools'
        // ToolLoop.cs:30-31, forwarded again at :97 into registry.InvokeAsync), through to the per-tool lambda
        // registered at ChatEndpoints.cs:628 -- confirmed hop by hop, not assumed. One hop past that lambda,
        // ToolExecutorService.ExecuteTool (ToolExecutorService.cs:104/113) links it into a *new*, timeout-bearing
        // CancellationTokenSource before a concrete shell/http ToolHandler ever sees it, so the literal token
        // object does change there -- but that derived token still cancels whenever this one does, which is all
        // this test needs: it models a handler awaiting turnCancel.Token directly, standing in for the whole
        // chain, since the scope of this fix is the socket-to-token wiring, not the tool loop itself (which has
        // its own coverage in ToolLoopIntegrationTests.cs).
        (WebSocket server, ClientWebSocket client) = await OpenPairAsync();
        try
        {
            using CancellationTokenSource turnCancel = new();
            _ = ChatEndpoints.WatchForDisconnectAsync(server, () =>
            {
                try { turnCancel.Cancel(); }
                catch (ObjectDisposedException) { }
            });
            CancellationToken handlerToken = turnCancel.Token;
            TaskCompletionSource<bool> handlerObservedCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task handlerTask = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, handlerToken);
                }
                catch (OperationCanceledException)
                {
                    handlerObservedCancellation.TrySetResult(true);
                }
            });

            Stopwatch elapsed = Stopwatch.StartNew();
            client.Abort();
            Task finished = await Task.WhenAny(handlerObservedCancellation.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
            elapsed.Stop();

            Assert.True(ReferenceEquals(finished, handlerObservedCancellation.Task),
                $"the tool handler's own token was not cancelled in time (elapsed {elapsed.ElapsedMilliseconds}ms).");
            Assert.True(handlerToken.IsCancellationRequested);
            await handlerTask;
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task CancellingThePendingReceive_AbortsTheSocketSoCloseAsyncThrows_TheOriginalBug()
    {
        // The original bug this whole fix removes, reproduced directly rather than only cited: the earlier
        // version of WatchForDisconnectAsync's receive took a token tied to the turn's own lifetime and
        // cancelled it once the turn finished, intending to "clean up" the dangling receive. This is that
        // exact sequence, isolated -- no WatchForDisconnectAsync involved, just the two WebSocket calls it
        // used to make in the wrong order.
        (WebSocket server, ClientWebSocket client) = await OpenPairAsync();
        try
        {
            using CancellationTokenSource receiveCancel = new();
            byte[] buffer = new byte[16];
            Task<WebSocketReceiveResult> pendingReceive = server.ReceiveAsync(new ArraySegment<byte>(buffer), receiveCancel.Token);

            receiveCancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingReceive);
            Assert.Equal(WebSocketState.Aborted, server.State);

            Exception thrown = await Record.ExceptionAsync(
                () => server.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None));
            Assert.NotNull(thrown); // CloseAsync does not accept an Aborted socket -- this is what the 41% was
        }
        finally
        {
            client.Dispose();
        }
    }
}
