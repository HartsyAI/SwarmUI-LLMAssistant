using System.Collections.Concurrent;
using Hartsy.Extensions.LLMAssistant.Tools;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>The tools a user has allowed for the rest of a chat ("Allow for this chat"). Kept in memory, so a restart asks again.</summary>
public sealed class ToolApprovalGrants
{
    /// <summary>Allowed tool ids by user and chat.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _grants = new();

    /// <summary>The key one user's chat is stored under.</summary>
    private static string Key(string owner, string threadId) => $"{owner}|{threadId}";

    /// <summary>Whether the tool is allowed for the chat.</summary>
    public bool Has(string owner, string threadId, string toolId)
        => _grants.TryGetValue(Key(owner, threadId), out ConcurrentDictionary<string, byte> tools) && tools.ContainsKey(toolId);

    /// <summary>Allows the tool for the rest of the chat.</summary>
    public void Add(string owner, string threadId, string toolId)
        => _grants.GetOrAdd(Key(owner, threadId), _ => new()).TryAdd(toolId, 0);

    /// <summary>Forgets every allowance for a chat, for example when it is deleted.</summary>
    public void Clear(string owner, string threadId) => _grants.TryRemove(Key(owner, threadId), out _);
}

/// <summary>The process-wide approval state: who is waiting to be asked, and what has been allowed.</summary>
public static class ToolApprovals
{
    /// <summary>Tool calls waiting for the user. The answer is <c>allow</c>, <c>allow_chat</c> or <c>deny</c>.</summary>
    public static readonly PendingRequestRegistry<string> Pending = new();

    /// <summary>Tools allowed for the rest of a chat.</summary>
    public static readonly ToolApprovalGrants Grants = new();

    /// <summary>The answers a user can give.</summary>
    public static readonly string[] Answers = ["allow", "allow_chat", "deny"];
}

/// <summary>Applies an approval mode to one turn's tool calls: lets a call through, refuses it, or asks the user and waits.
/// <para>The mode logic is <see cref="ApprovalPolicy"/>; this class does the asking. It asks through the turn's sink as a
/// <c>tool_approval</c> event and waits on <see cref="ToolApprovals.Pending"/> for the answer, which arrives from a separate request
/// (<c>LLMAssistantResolveToolApproval</c>) so the chat socket's one outstanding receive is never shared.</para></summary>
public sealed class ToolApprovalGate(string mode, string owner, string threadId, IToolTurnSink sink, PendingRequestRegistry<string> pending = null, ToolApprovalGrants grants = null)
{
    /// <summary>Calls waiting for the user.</summary>
    private readonly PendingRequestRegistry<string> _pending = pending ?? ToolApprovals.Pending;

    /// <summary>Tools already allowed for the chat.</summary>
    private readonly ToolApprovalGrants _grants = grants ?? ToolApprovals.Grants;

    /// <summary>The mode this turn runs in.</summary>
    public string Mode { get; } = ApprovalPolicy.Normalize(mode);

    /// <summary>Checks one call. Returns null when it may run, or the result to give the model instead when it may not. Waits while the user
    /// decides. A Stop or closed tab while waiting cancels the wait (<see cref="OperationCanceledException"/>).</summary>
    public async Task<JObject> CheckAsync(string toolId, ToolRisk risk, JObject args, string callId, CancellationToken ct)
    {
        bool allowedForChat = !string.IsNullOrEmpty(threadId) && _grants.Has(owner, threadId, toolId);
        switch (ApprovalPolicy.Decide(Mode, risk, allowedForChat))
        {
            case ApprovalDecision.Allow:
                return null;
            case ApprovalDecision.Deny:
                return Denied(toolId, "plan");
        }
        if (!sink.CanApprove || !sink.IsOpen)
        {
            return Denied(toolId, "unavailable");
        }
        using PendingRequestRegistry<string>.Request request = _pending.Begin(owner, threadId, ct);
        await sink.ToolApproval(new JObject
        {
            ["approvalId"] = request.Id,
            ["id"] = callId,
            ["name"] = toolId,
            ["arguments"] = args ?? [],
            ["risk"] = risk.ToString().ToLowerInvariant(),
            ["mode"] = Mode,
        });
        string answer = await request.Answer;
        if (answer == "allow_chat" && !string.IsNullOrEmpty(threadId))
        {
            _grants.Add(owner, threadId, toolId);
            return null;
        }
        return answer == "allow" || answer == "allow_chat" ? null : Denied(toolId, "user");
    }

    /// <summary>The result of a refused call.</summary>
    private static JObject Denied(string toolId, string reason) => new()
    {
        ["success"] = false,
        ["denied"] = true,
        ["error"] = ApprovalPolicy.DenialMessage(toolId, reason),
    };
}
