using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Schedules;
using Hartsy.Extensions.LLMAssistant.Services.Sessions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>What the schedule tools share. A tool works only on schedules the calling chat made, so a model cannot see or cancel the user's own, or another chat's. What a model makes
/// is always saved switched off: a job that runs later with no one watching is something the user turns on, which also means a model that was misled by a hostile page cannot
/// start one that runs on its own.</summary>
public abstract class ScheduleToolBase : ToolHandler
{
    /// <summary>The calling user and chat, or a failed result when the call is not from a saved chat.</summary>
    protected static (User User, JObject Chat, JObject Fail) Caller(ToolExecutionContext ctx)
    {
        User user = ctx.Session?.User;
        if (user is null || string.IsNullOrEmpty(ctx.ThreadId))
        {
            return (null, null, Failed("Schedules can only be used from a saved chat."));
        }
        // The tools are the assistant's way to the same thing the Schedules tab does, so they need the same permission (and the scheduler checks it again when a run is due).
        if (!user.HasPermission(WebAPI.LLMAssistantAPI.PermSchedules))
        {
            return (null, null, Failed("The user does not have permission to use schedules."));
        }
        JObject chat = ThreadStorageService.GetThread(user, ctx.ThreadId);
        return chat is null ? (null, null, Failed("This chat could not be found.")) : (user, chat, null);
    }

    /// <summary>A failed result.</summary>
    protected static JObject Failed(string error) => new() { ["success"] = false, ["error"] = error };

    /// <summary>The schedules the calling chat made.</summary>
    protected static List<JObject> Mine(User user, ToolExecutionContext ctx)
        => [.. ScheduleStore.List(user).OfType<JObject>().Where(s => s[ScheduleFields.Source]?.ToString() == ScheduleSources.Assistant && s[ScheduleFields.CreatedBy]?.ToString() == ctx.ThreadId)];

    /// <summary>What a schedule looks like to the model.</summary>
    protected static JObject Describe(JObject schedule, DateTime nowUtc)
    {
        ScheduleTrigger.TryFromJson(schedule[ScheduleFields.Trigger] as JObject, schedule[ScheduleFields.TimeZone]?.ToString(), nowUtc, out ScheduleTrigger trigger, out _);
        JObject info = new()
        {
            ["id"] = schedule[ScheduleFields.Id],
            ["title"] = schedule[ScheduleFields.Title],
            ["enabled"] = schedule[ScheduleFields.Enabled],
            ["when"] = trigger?.Describe(),
            ["approvalMode"] = schedule[ScheduleFields.ApprovalMode],
            ["runs"] = schedule[ScheduleFields.RunCount],
        };
        foreach (string field in new[] { ScheduleFields.NextRunAt, ScheduleFields.LastRunAt, ScheduleFields.LastSkipped })
        {
            if (schedule[field] is { Type: not JTokenType.Null } value)
            {
                info[field] = value;
            }
        }
        return info;
    }
}

/// <summary>Makes a schedule: a prompt that runs on a timetable as a session. It is saved switched off, and the result says so.</summary>
public sealed class ScheduleCreateTool : ScheduleToolBase
{
    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.ScheduleCreate;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Exec;

    /// <inheritdoc/>
    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        (User user, JObject chat, JObject fail) = Caller(ctx);
        if (fail is not null)
        {
            return Task.FromResult(fail);
        }
        // Exactly one way of saying when.
        JObject trigger = new();
        int given = 0;
        if (ctx.Args["everyMinutes"] is { Type: not JTokenType.Null } every)
        {
            trigger["kind"] = ScheduleTrigger.Every;
            // Models often send a number as text ("30"); that is still a number.
            trigger["everyMinutes"] = every.Type == JTokenType.String && int.TryParse(every.ToString().Trim(), out int minutes) ? minutes : every;
            given++;
        }
        if (ctx.Args["cron"]?.ToString()?.Trim() is { Length: > 0 } cron)
        {
            trigger["kind"] = ScheduleTrigger.Cron;
            trigger["cron"] = cron;
            given++;
        }
        if (ctx.Args["at"]?.ToString()?.Trim() is { Length: > 0 } at)
        {
            trigger["kind"] = ScheduleTrigger.At;
            trigger["at"] = at;
            given++;
        }
        if (given != 1)
        {
            return Task.FromResult(Failed("Say when with exactly one of everyMinutes, cron or at."));
        }
        DateTime now = DateTime.UtcNow;
        JObject request = new()
        {
            [ScheduleFields.Title] = ctx.Args["title"],
            [ScheduleFields.Prompt] = ctx.Args["prompt"],
            [ScheduleFields.Trigger] = trigger,
            [ScheduleFields.TimeZone] = ctx.Args["timezone"],
            [ScheduleFields.ApprovalMode] = ctx.Args["approvalMode"],
            [ScheduleFields.ToolIds] = ctx.Args["tools"],
            [ScheduleFields.Model] = ctx.Args["model"]?.ToString()?.Trim() is { Length: > 0 } model ? model : ctx.ModelId,
        };
        // A schedule can use only tools this chat could, can be no looser about approvals than this chat is, and starts off.
        (JObject schedule, string error) = ScheduleRules.Build(request, null, ScheduleSources.Assistant, ctx.ThreadId, chat["approvalMode"]?.ToString(), SessionManager.EffectiveToolIds(user, chat), now);
        if (schedule is null)
        {
            return Task.FromResult(Failed(error));
        }
        if (!ScheduleStore.Save(user, schedule))
        {
            return Task.FromResult(Failed($"You can have at most {ScheduleLimits.MaxPerUser} schedules. Cancel one first."));
        }
        JObject result = Describe(schedule, now);
        result["success"] = true;
        result["note"] = "Saved, but switched off. Nothing runs until the user turns it on in Settings > Schedules. Tell them it is waiting for them.";
        return Task.FromResult(result);
    }
}

/// <summary>Lists the schedules the calling chat made.</summary>
public sealed class ScheduleListTool : ScheduleToolBase
{
    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.ScheduleList;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    /// <inheritdoc/>
    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        (User user, _, JObject fail) = Caller(ctx);
        if (fail is not null)
        {
            return Task.FromResult(fail);
        }
        DateTime now = DateTime.UtcNow;
        return Task.FromResult(new JObject { ["success"] = true, ["schedules"] = new JArray(Mine(user, ctx).Select(s => Describe(s, now))) });
    }
}

/// <summary>Cancels a schedule the calling chat made: it is deleted, and runs it already started are left to finish.</summary>
public sealed class ScheduleCancelTool : ScheduleToolBase
{
    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.ScheduleCancel;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Write;

    /// <inheritdoc/>
    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        (User user, _, JObject fail) = Caller(ctx);
        if (fail is not null)
        {
            return Task.FromResult(fail);
        }
        string id = ctx.Args["id"]?.ToString()?.Trim();
        // An id that is missing, the user's own schedule, or another chat's all give the same answer.
        JObject schedule = string.IsNullOrEmpty(id) ? null : Mine(user, ctx).FirstOrDefault(s => s[ScheduleFields.Id]?.ToString() == id);
        if (schedule is null)
        {
            return Task.FromResult(Failed($"There is no schedule '{id}' made by this chat. Use schedule_list to see its schedules."));
        }
        ScheduleStore.Delete(user, id);
        return Task.FromResult(new JObject { ["success"] = true, ["id"] = id });
    }
}
