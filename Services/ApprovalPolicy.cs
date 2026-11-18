using Hartsy.Extensions.LLMAssistant.Tools;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>What to do with a tool call under an approval mode.</summary>
public enum ApprovalDecision
{
    /// <summary>Run it.</summary>
    Allow,
    /// <summary>Ask the user first.</summary>
    Ask,
    /// <summary>Refuse it; the model is told why.</summary>
    Deny,
}

/// <summary>The rules for approving tool calls, with no I/O so they can be tested alone.
/// <list type="bullet">
/// <item><b>auto</b>: everything runs. This is the default, so existing chats behave as before.</item>
/// <item><b>ask</b>: calls that only read run; anything that changes something asks first, unless the user allowed that tool for the chat.</item>
/// <item><b>plan</b>: only reads run; everything else is refused with a message that tells the model to describe the step instead.</item>
/// </list></summary>
public static class ApprovalPolicy
{
    /// <summary>The mode to use for a missing or unknown value.</summary>
    public const string DefaultMode = ApprovalModes.Auto;

    /// <summary>Whether <paramref name="mode"/> is one of the known modes.</summary>
    public static bool IsValid(string mode) => mode is ApprovalModes.Ask or ApprovalModes.Auto or ApprovalModes.Plan;

    /// <summary>The known mode for <paramref name="mode"/>, case-insensitively, or the default when it is missing or unknown.</summary>
    public static string Normalize(string mode)
    {
        string lower = (mode ?? "").Trim().ToLowerInvariant();
        return IsValid(lower) ? lower : DefaultMode;
    }

    /// <summary>The stricter of two modes (plan, then ask, then auto), for a chat that may not be looser than the one that started it.</summary>
    public static string Stricter(string a, string b)
    {
        static int Rank(string mode) => Normalize(mode) switch { ApprovalModes.Plan => 0, ApprovalModes.Ask => 1, _ => 2 };
        return Rank(a) <= Rank(b) ? Normalize(a) : Normalize(b);
    }

    /// <summary>Decides one call. <paramref name="allowedForChat"/> is whether the user already allowed this tool for the rest of the chat.</summary>
    public static ApprovalDecision Decide(string mode, ToolRisk risk, bool allowedForChat)
    {
        switch (Normalize(mode))
        {
            case ApprovalModes.Plan:
                return risk == ToolRisk.Read ? ApprovalDecision.Allow : ApprovalDecision.Deny;
            case ApprovalModes.Ask:
                return risk == ToolRisk.Read || allowedForChat ? ApprovalDecision.Allow : ApprovalDecision.Ask;
            default:
                return ApprovalDecision.Allow;
        }
    }

    /// <summary>The result a refused call gives the model.</summary>
    /// <param name="toolId">The tool that was refused.</param>
    /// <param name="reason">Why: <c>plan</c> (plan mode), <c>user</c> (the user said no) or <c>unavailable</c> (it needed approval and nobody could give it).</param>
    public static string DenialMessage(string toolId, string reason) => reason switch
    {
        "plan" => $"Plan mode: '{toolId}' would change something, so it was not run. Describe what you would do with it instead, and the user can switch out of plan mode to let it happen.",
        "user" => $"The user declined to run '{toolId}'. Do not try it again unless they ask; carry on without it or ask what they would like instead.",
        _ => $"'{toolId}' needs the user's approval, and this request cannot ask for it, so it was not run.",
    };
}
