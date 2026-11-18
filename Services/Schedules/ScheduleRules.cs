using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Schedules;

/// <summary>What a stored schedule contains. Kept in one JSON object per schedule (see <see cref="ScheduleStore"/>): what the user or assistant set, and what the scheduler
/// recorded as it ran.</summary>
public static class ScheduleFields
{
    /// <summary>The schedule's id.</summary>
    public const string Id = "id";
    /// <summary>A short name.</summary>
    public const string Title = "title";
    /// <summary>The message a run starts from.</summary>
    public const string Prompt = "prompt";
    /// <summary>Whether it runs.</summary>
    public const string Enabled = "enabled";
    /// <summary>Who made it: <see cref="ScheduleSources.User"/> or <see cref="ScheduleSources.Assistant"/>.</summary>
    public const string Source = "source";
    /// <summary>For one made by the assistant, the chat it was made in.</summary>
    public const string CreatedBy = "createdBy";
    /// <summary>The assistant a run is made as ("" for the user's active one).</summary>
    public const string AssistantId = "assistantId";
    /// <summary>The model a run uses ("" for the first available).</summary>
    public const string Model = "model";
    /// <summary>How a run's tool calls are approved (<c>auto</c>, <c>ask</c> or <c>plan</c>).</summary>
    public const string ApprovalMode = "approvalMode";
    /// <summary>The tool ids a run may use, fixed when the schedule is saved.</summary>
    public const string ToolIds = "toolIds";
    /// <summary>The trigger: <c>{kind, everyMinutes?, cron?, at?, anchor?}</c>.</summary>
    public const string Trigger = "trigger";
    /// <summary>The time zone a cron trigger is read in.</summary>
    public const string TimeZone = "timezone";
    /// <summary>When it next runs (UTC), or absent when it will not.</summary>
    public const string NextRunAt = "nextRunAt";
    /// <summary>When it last started a run (UTC).</summary>
    public const string LastRunAt = "lastRunAt";
    /// <summary>The chat of its last run.</summary>
    public const string LastThreadId = "lastThreadId";
    /// <summary>Why the last due time did not start a run (for example the previous run was still working), with <see cref="LastSkippedAt"/>.</summary>
    public const string LastSkipped = "lastSkipped";
    /// <summary>When a due time was last skipped (UTC).</summary>
    public const string LastSkippedAt = "lastSkippedAt";
    /// <summary>How many runs it has started.</summary>
    public const string RunCount = "runCount";
    /// <summary>The chat its runs are listed under.</summary>
    public const string HostThreadId = "threadId";
    /// <summary>When it was made (UTC).</summary>
    public const string CreatedAt = "createdAt";
    /// <summary>The field the host chat carries, naming the schedule it belongs to.</summary>
    public const string HostMarker = "scheduleId";
}

/// <summary>Who made a schedule.</summary>
public static class ScheduleSources
{
    /// <summary>The user, in Settings.</summary>
    public const string User = "user";
    /// <summary>The assistant, with <c>schedule_create</c>. It starts switched off: a recurring job that runs with no one watching is something the user turns on.</summary>
    public const string Assistant = "assistant";
}

/// <summary>What the scheduler does with a schedule at a given time.</summary>
public enum ScheduleDecision
{
    /// <summary>Nothing yet.</summary>
    NotDue,
    /// <summary>Start a run.</summary>
    Run,
    /// <summary>It is later than the catch-up window: do not run it, move on to the next time.</summary>
    Missed,
}

/// <summary>The rules for schedules with no storage, user or clock in them, so each can be tested alone: how a request becomes a stored schedule, when one is due, and what
/// its runs are called.</summary>
public static class ScheduleRules
{
    /// <summary>The ids that mean "a tool that makes or manages schedules or sessions". A run never has them, so a scheduled run cannot make more runs.</summary>
    public static readonly string[] BarredFromRuns = [.. ToolConstants.ScheduleIds];

    /// <summary>Builds the stored schedule for a request, or says why it cannot be. <paramref name="existing"/> is the schedule being changed (null for a new one): what the
    /// scheduler recorded on it is kept. <paramref name="allowedTools"/> is every tool the maker's chat or assistant could use; the schedule gets those it asks for out of
    /// them (all of them when it asks for none in particular), never a schedule tool. <paramref name="maxMode"/> is the loosest approval mode the maker may give (the creating
    /// chat's own for the assistant; null for the user, who may choose any). The assistant's schedules are always saved switched off.</summary>
    public static (JObject Schedule, string Error) Build(JObject request, JObject existing, string source, string createdBy, string maxMode, IReadOnlyCollection<string> allowedTools, DateTime nowUtc)
    {
        if (request is null)
        {
            return (null, "A schedule needs its details.");
        }
        string title = CleanTitle(request[ScheduleFields.Title]?.ToString());
        string prompt = request[ScheduleFields.Prompt]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(prompt))
        {
            return (null, "title and prompt are required.");
        }
        if (prompt.Length > ScheduleLimits.MaxPromptChars)
        {
            return (null, $"The prompt is too long ({prompt.Length} characters; at most {ScheduleLimits.MaxPromptChars}).");
        }
        string zone = request[ScheduleFields.TimeZone]?.ToString()?.Trim();
        JObject triggerJson = request[ScheduleFields.Trigger] as JObject;
        // The cadence of "every" counts from when the schedule was made, and keeps counting when it is edited without changing that.
        if (triggerJson is not null && triggerJson["anchor"] is null && existing?[ScheduleFields.Trigger]?["kind"]?.ToString() == triggerJson["kind"]?.ToString()
            && existing[ScheduleFields.Trigger]["anchor"] is JToken anchor)
        {
            triggerJson = (JObject)triggerJson.DeepClone();
            triggerJson["anchor"] = anchor;
        }
        if (!ScheduleTrigger.TryFromJson(triggerJson, zone, nowUtc, out ScheduleTrigger trigger, out string error))
        {
            return (null, error);
        }
        string requestedMode = request[ScheduleFields.ApprovalMode]?.ToString();
        string mode = ApprovalPolicy.IsValid((requestedMode ?? "").Trim().ToLowerInvariant()) ? ApprovalPolicy.Normalize(requestedMode) : ApprovalModes.Ask;
        if (maxMode is not null)
        {
            mode = ApprovalPolicy.Stricter(maxMode, mode);
        }
        HashSet<string> allowed = [.. allowedTools];
        allowed.ExceptWith(BarredFromRuns);
        if (request[ScheduleFields.ToolIds] is JArray asked)
        {
            allowed.IntersectWith(asked.Select(t => t.ToString().Trim()));
        }
        // What the assistant makes starts off. The user's own switch decides for a schedule the user saves, and the user turning on one the assistant made is their own
        // act (the schedule keeps its source, so the list can still say where it came from).
        bool? switchedBy = request[ScheduleFields.Enabled]?.Type == JTokenType.Boolean ? request[ScheduleFields.Enabled].Value<bool>() : null;
        bool enabled = source != ScheduleSources.Assistant && (switchedBy ?? existing?[ScheduleFields.Enabled]?.Value<bool>() ?? true);
        DateTime? next = enabled ? trigger.NextAfter(nowUtc) : null;
        // A one-time schedule with no future time has nothing to switch on.
        if (enabled && next is null)
        {
            return (null, trigger.Kind == ScheduleTrigger.At ? "That time has already passed." : "That schedule never runs.");
        }
        JObject schedule = existing is null ? new JObject() : (JObject)existing.DeepClone();
        schedule[ScheduleFields.Id] = existing?[ScheduleFields.Id]?.ToString() ?? Guid.NewGuid().ToString("N");
        schedule[ScheduleFields.Title] = title;
        schedule[ScheduleFields.Prompt] = prompt;
        schedule[ScheduleFields.Enabled] = enabled;
        schedule[ScheduleFields.Source] = existing?[ScheduleFields.Source]?.ToString() ?? source;
        schedule[ScheduleFields.CreatedBy] = existing?[ScheduleFields.CreatedBy]?.ToString() ?? createdBy ?? "";
        schedule[ScheduleFields.AssistantId] = request[ScheduleFields.AssistantId]?.ToString()?.Trim() ?? "";
        schedule[ScheduleFields.Model] = request[ScheduleFields.Model]?.ToString()?.Trim() ?? "";
        schedule[ScheduleFields.ApprovalMode] = mode;
        schedule[ScheduleFields.ToolIds] = new JArray(allowed.Order(StringComparer.Ordinal));
        schedule[ScheduleFields.Trigger] = trigger.ToJson();
        schedule[ScheduleFields.TimeZone] = trigger.ZoneId;
        schedule[ScheduleFields.CreatedAt] ??= nowUtc.ToString("o");
        schedule[ScheduleFields.RunCount] ??= 0;
        if (next is not null)
        {
            schedule[ScheduleFields.NextRunAt] = next.Value.ToString("o");
        }
        else
        {
            schedule.Remove(ScheduleFields.NextRunAt);
        }
        return (schedule, null);
    }

    /// <summary>A schedule title the way lists show it: one line, bounded, never empty.</summary>
    public static string CleanTitle(string title)
    {
        string clean = Regex.Replace(title ?? "", @"\s+", " ").Trim();
        return clean.Length > ScheduleLimits.MaxTitleChars ? clean[..ScheduleLimits.MaxTitleChars].TrimEnd() : clean;
    }

    /// <summary>Whether a schedule is due at <paramref name="nowUtc"/>: not yet, run it, or too late to run (it is skipped, never made up). A schedule that is off, or has no
    /// next time, is never due.</summary>
    public static ScheduleDecision Decide(JObject schedule, DateTime nowUtc)
    {
        if (schedule?[ScheduleFields.Enabled]?.Type != JTokenType.Boolean || !schedule[ScheduleFields.Enabled].Value<bool>()
            || ScheduleTrigger.ReadDate(schedule[ScheduleFields.NextRunAt]) is not DateTime next || next > nowUtc)
        {
            return ScheduleDecision.NotDue;
        }
        return nowUtc - next > ScheduleLimits.CatchUpWindow ? ScheduleDecision.Missed : ScheduleDecision.Run;
    }

    /// <summary>The name of one run: the schedule's title and when it started, so the chat list tells runs apart.</summary>
    public static string RunTitle(string scheduleTitle, DateTime nowUtc) => CleanTitle($"{scheduleTitle} · {nowUtc:yyyy-MM-dd HH:mm} UTC");

    /// <summary>Moves a schedule on after a due time, in place: its next run becomes the first one after <paramref name="nowUtc"/>, or it is switched off when there is none (a
    /// one-time schedule that has run). Done before the run starts, so a run that takes the server down is not started a second time.</summary>
    public static void Advance(JObject schedule, DateTime nowUtc)
    {
        ScheduleTrigger.TryFromJson(schedule[ScheduleFields.Trigger] as JObject, schedule[ScheduleFields.TimeZone]?.ToString(), nowUtc, out ScheduleTrigger trigger, out _);
        DateTime? next = trigger?.NextAfter(nowUtc);
        if (next is null)
        {
            schedule[ScheduleFields.Enabled] = false;
            schedule.Remove(ScheduleFields.NextRunAt);
        }
        else
        {
            schedule[ScheduleFields.NextRunAt] = next.Value.ToString("o");
        }
    }
}
