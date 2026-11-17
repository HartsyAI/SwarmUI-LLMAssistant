using System.Collections.Concurrent;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Requests that wait for an answer from the user, each owned by one user and tied to the turn that asked: a tool call waiting for
/// approval now, a plan waiting for acceptance or a question to the user later. The asker gets a task to await; the answer arrives from
/// another request (<see cref="TryResolve"/>) and only the owner can give it. A request is dropped when its turn ends, so a Stop or a
/// closed tab never leaves one behind.</summary>
/// <typeparam name="T">The kind of answer.</typeparam>
public sealed class PendingRequestRegistry<T>
{
    /// <summary>One waiting request.</summary>
    public sealed class Request : IDisposable
    {
        /// <summary>Id the answer is sent back under; unguessable.</summary>
        public string Id { get; }

        /// <summary>Completes with the answer, or is cancelled when the turn ends first.</summary>
        public Task<T> Answer => Source.Task;

        /// <summary>The user the request belongs to.</summary>
        internal string Owner;

        /// <summary>What the request belongs to (a thread id), for cancelling them together.</summary>
        internal string Scope;

        /// <summary>The completion source behind <see cref="Answer"/>.</summary>
        internal readonly TaskCompletionSource<T> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Registration that cancels the request when its turn is cancelled.</summary>
        internal CancellationTokenRegistration Registration;

        /// <summary>The registry to leave when this request is disposed.</summary>
        private readonly PendingRequestRegistry<T> _registry;

        internal Request(string id, PendingRequestRegistry<T> registry)
        {
            Id = id;
            _registry = registry;
        }

        /// <summary>Removes the request from the registry and releases its cancellation hook.</summary>
        public void Dispose()
        {
            _registry._pending.TryRemove(Id, out _);
            Registration.Dispose();
        }
    }

    /// <summary>The waiting requests by id.</summary>
    private readonly ConcurrentDictionary<string, Request> _pending = new();

    /// <summary>How many requests are waiting.</summary>
    public int Count => _pending.Count;

    /// <summary>Starts waiting for an answer. Cancelling <paramref name="ct"/> (the turn ending) cancels the request and drops it.</summary>
    public Request Begin(string owner, string scope, CancellationToken ct = default)
    {
        string id = Guid.NewGuid().ToString("N");
        Request request = new(id, this) { Owner = owner, Scope = scope };
        _pending[id] = request;
        if (ct.CanBeCanceled)
        {
            request.Registration = ct.Register(() =>
            {
                request.Source.TrySetCanceled(ct);
                _pending.TryRemove(id, out _);
            });
        }
        return request;
    }

    /// <summary>Gives a waiting request its answer. False when there is no such request, it already ended, or it belongs to someone else.</summary>
    public bool TryResolve(string owner, string id, T answer)
    {
        if (string.IsNullOrEmpty(id) || !_pending.TryGetValue(id, out Request request) || request.Owner != owner)
        {
            return false;
        }
        if (!_pending.TryRemove(id, out _))
        {
            return false;
        }
        request.Registration.Dispose();
        return request.Source.TrySetResult(answer);
    }

    /// <summary>Cancels every request one user has under a scope, for example when a chat is deleted. Returns how many.</summary>
    public int CancelScope(string owner, string scope)
    {
        int cancelled = 0;
        foreach (Request request in _pending.Values.Where(r => r.Owner == owner && r.Scope == scope).ToList())
        {
            if (_pending.TryRemove(request.Id, out _))
            {
                request.Registration.Dispose();
                request.Source.TrySetCanceled();
                cancelled++;
            }
        }
        return cancelled;
    }
}
