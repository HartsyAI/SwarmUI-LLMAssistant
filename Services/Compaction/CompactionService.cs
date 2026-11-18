using Hartsy.Extensions.LLMAssistant.LLMs;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Compaction;

/// <summary>Shortens a long conversation: asks a model to summarize the start of the active branch, and saves the summary on the thread
/// (<see cref="CompactionRecord"/>). The decisions live in <see cref="CompactionPlanner"/>; this adds the model call and the save.</summary>
public static class CompactionService
{
    /// <summary>The token cap for a summary. A summary that needs more than this is not short enough to be worth sending.</summary>
    public const int SummaryMaxTokens = 1500;

    /// <summary>The temperature for a summary: low, so it stays faithful to what was said.</summary>
    public const double SummaryTemperature = 0.3;

    /// <summary>Threads being compacted now, so two requests (an auto-compaction and a <c>/compact</c>) cannot summarize the same chat at once.</summary>
    private static readonly HashSet<string> Running = [];

    /// <summary>Runs the summarizer for a request; replaced in tests.</summary>
    public delegate Task<LLMGenerateResult> Summarizer(ExtendedLLMInput input, CancellationToken ct);

    /// <summary>Compacts the thread's active branch and saves the summary. Returns the new record, or null when there is nothing worth compacting
    /// (a short conversation, or one already compacted as far as it can be). Throws when the model fails or writes nothing, and when the
    /// thread is already being compacted, so the caller can report it; the thread is not changed in that case.</summary>
    public static async Task<CompactionRecord> CompactAsync(Session session, string threadId, string model, string extraInstructions, CancellationToken ct,
        Summarizer summarize = null)
    {
        string key = $"{session.User.UserID}|{threadId}";
        lock (Running)
        {
            if (!Running.Add(key))
            {
                throw new SwarmReadableErrorException("This chat is already being compacted.");
            }
        }
        try
        {
            JObject thread = ThreadStorageService.GetThread(session.User, threadId) ?? throw new SwarmReadableErrorException($"Chat '{threadId}' not found.");
            CompactionRecord record = await SummarizeAsync(thread, session, model, extraInstructions, ct, summarize);
            if (record is not null)
            {
                ThreadStorageService.AddCompaction(session.User, threadId, record.ToJson());
            }
            return record;
        }
        finally
        {
            lock (Running)
            {
                Running.Remove(key);
            }
        }
    }

    /// <summary>Plans a compaction of <paramref name="thread"/>'s active branch and has the model write the summary, without saving it.
    /// Null when there is nothing to compact.</summary>
    public static async Task<CompactionRecord> SummarizeAsync(JObject thread, Session session, string model, string extraInstructions, CancellationToken ct,
        Summarizer summarize = null)
    {
        List<JObject> path = ThreadStorageService.GetActivePath(thread);
        CompactionRecord active = CompactionPlanner.FindActive(thread, path);
        CompactionPlan plan = CompactionPlanner.Plan(path, active);
        if (plan is null)
        {
            return null;
        }
        ExtendedLLMInput input = ExtendedLLMInput.Create(CompactionPlanner.BuildPrompt(plan, extraInstructions), CompactionPlanner.SummarizerSystemPrompt, model);
        input.MaxTokens = SummaryMaxTokens;
        input.Temperature = SummaryTemperature;
        input.RequestSession = session;
        // No conversation id: the summarizer's prompt is not the chat's, and must not evict the chat's prefix cache.
        input.ConversationId = null;
        LLMGenerateResult result = await (summarize ?? LLMDispatcher.GenerateDetailed)(input, ct);
        string summary = result?.Response?.Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new SwarmReadableErrorException("The model did not write a summary, so the chat was left as it is."
                + (result?.FinishReason == LLMGenerateResult.Length ? " It ran out of tokens first; try a model that thinks less." : ""));
        }
        (long before, _) = CompactionPlanner.EstimateContext(path, active);
        return new CompactionRecord(Guid.NewGuid().ToString("N"), path[plan.AnchorIndex]["id"]?.ToString(), CompactionPlanner.Fingerprint(path.Take(plan.AnchorIndex + 1)),
            summary, plan.AnchorIndex + 1, before, model ?? "", DateTime.UtcNow.ToString("o"), path[^1]["id"]?.ToString() ?? "");
    }
}
