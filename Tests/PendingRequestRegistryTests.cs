using Hartsy.Extensions.LLMAssistant.Services;
using Xunit;

namespace Hartsy.Extensions.LLMAssistant.Tests;

/// <summary>Requests that wait for an answer from the user: only the owner can answer, once, and a turn ending drops them.</summary>
public class PendingRequestRegistryTests
{
    [Fact]
    public async Task TheOwnerAnswersAndTheAskerGetsTheAnswer()
    {
        PendingRequestRegistry<string> registry = new();
        using PendingRequestRegistry<string>.Request request = registry.Begin("alice", "thread1");

        Assert.True(registry.TryResolve("alice", request.Id, "allow"));

        Assert.Equal("allow", await request.Answer);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void SomeoneElseCannotAnswer_AndTheRequestStaysWaiting()
    {
        PendingRequestRegistry<string> registry = new();
        using PendingRequestRegistry<string>.Request request = registry.Begin("alice", "thread1");

        Assert.False(registry.TryResolve("mallory", request.Id, "allow"));

        Assert.False(request.Answer.IsCompleted);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void AnUnknownOrAlreadyAnsweredIdIsRefused()
    {
        PendingRequestRegistry<string> registry = new();
        using PendingRequestRegistry<string>.Request request = registry.Begin("alice", "t");
        Assert.True(registry.TryResolve("alice", request.Id, "deny"));

        Assert.False(registry.TryResolve("alice", request.Id, "allow"));
        Assert.False(registry.TryResolve("alice", "no-such-id", "allow"));
        Assert.False(registry.TryResolve("alice", null, "allow"));
    }

    [Fact]
    public async Task TheTurnEndingCancelsTheRequestAndDropsIt()
    {
        PendingRequestRegistry<string> registry = new();
        using CancellationTokenSource turn = new();
        using PendingRequestRegistry<string>.Request request = registry.Begin("alice", "t", turn.Token);

        turn.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.Answer);
        Assert.Equal(0, registry.Count);
        Assert.False(registry.TryResolve("alice", request.Id, "allow"));
    }

    [Fact]
    public async Task CancellingAScopeEndsOnlyThatUsersRequestsUnderIt()
    {
        PendingRequestRegistry<string> registry = new();
        using PendingRequestRegistry<string>.Request mine = registry.Begin("alice", "chat1");
        using PendingRequestRegistry<string>.Request otherChat = registry.Begin("alice", "chat2");
        using PendingRequestRegistry<string>.Request otherUser = registry.Begin("bob", "chat1");

        Assert.Equal(1, registry.CancelScope("alice", "chat1"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mine.Answer);
        Assert.False(otherChat.Answer.IsCompleted);
        Assert.False(otherUser.Answer.IsCompleted);
    }

    [Fact]
    public void DisposingARequestLeavesTheRegistry()
    {
        PendingRequestRegistry<string> registry = new();
        PendingRequestRegistry<string>.Request request = registry.Begin("alice", "t");
        request.Dispose();
        Assert.Equal(0, registry.Count);
    }
}
