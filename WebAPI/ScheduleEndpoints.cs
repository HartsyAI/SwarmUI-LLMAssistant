using Hartsy.Extensions.LLMAssistant.Services;
using Hartsy.Extensions.LLMAssistant.Services.Schedules;
using Hartsy.Extensions.LLMAssistant.Services.Sessions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.WebAPI;

/// <summary>The Schedules tab's routes: list, save, delete, run now, and a preview of when a trigger would run. All need <c>llm_schedules</c> and work on the caller's own schedules.
/// A schedule the user saves is the user's own deliberate act; one the assistant made is listed too (marked, and off until the user turns it on here).</summary>
public static class ScheduleEndpoints
{
    /// <summary>The caller's schedules, each with its trigger in words and how its last run went, and the tools a schedule can be given.</summary>
    public static Task<JObject> LLMAssistantGetSchedules(Session session)
    {
        User user = session.User;
        string userId = user.UserID;
        SessionManager.Reconcile(user, id => BackgroundTurnService.IsLive(userId, id) || GenerationCancellationRegistry.IsActive(id));
        List<JObject> index = [.. ThreadStorageService.GetThreadIndex(user).OfType<JObject>()];
        DateTime now = DateTime.UtcNow;
        JArray schedules = [];
        foreach (JObject stored in ScheduleStore.List(user).OfType<JObject>())
        {
            JObject schedule = (JObject)stored.DeepClone();
            ScheduleTrigger.TryFromJson(schedule[ScheduleFields.Trigger] as JObject, schedule[ScheduleFields.TimeZone]?.ToString(), now, out ScheduleTrigger trigger, out _);
            schedule["triggerText"] = trigger?.Describe();
            string last = schedule[ScheduleFields.LastThreadId]?.ToString();
            JObject lastRow = string.IsNullOrEmpty(last) ? null : index.FirstOrDefault(t => t["id"]?.ToString() == last);
            if (lastRow is not null)
            {
                schedule["lastStatus"] = lastRow[SessionFields.Status];
            }
            schedules.Add(schedule);
        }
        JObject settings = SettingsService.GetMergedSettings(user);
        string assistantId = AssistantService.GetActiveAssistantId(settings, user);
        JArray choices = [.. ToolRegistryService.GetEnabledTools(assistantId, settings, user)
            .Where(t => !ScheduleRules.BarredFromRuns.Contains(t["id"]?.ToString()) && !ToolConstants.SessionIds.Contains(t["id"]?.ToString()))
            .Select(t => new JObject { ["id"] = t["id"], ["name"] = t["name"] })];
        return Task.FromResult(new JObject { ["success"] = true, ["schedules"] = schedules, ["toolChoices"] = choices, ["max"] = ScheduleLimits.MaxPerUser });
    }

    /// <summary>Saves a schedule: a new one, or (with its <c>id</c>) a change to one. Request: <c>{schedule}</c> (an object or JSON string) with <c>title</c>, <c>prompt</c>,
    /// <c>trigger</c> (<c>{kind: every|cron|at, everyMinutes?, cron?, at?}</c>), <c>timezone?</c>, <c>enabled?</c>, <c>approvalMode?</c>, <c>model?</c>, <c>assistantId?</c> and
    /// <c>toolIds?</c> (all the assistant's tools when left out). A schedule the assistant made keeps its source when the user changes it.</summary>
    public static Task<JObject> LLMAssistantSaveSchedule(Session session, JObject rawInput)
    {
        User user = session.User;
        JObject request = rawInput["schedule"] as JObject;
        if (request is null && rawInput["schedule"]?.Type == JTokenType.String)
        {
            try
            {
                request = JObject.Parse(rawInput["schedule"].ToString());
            }
            catch (Newtonsoft.Json.JsonException)
            {
                request = null;
            }
        }
        if (request is null)
        {
            return Task.FromResult(Fail("schedule must be an object or a JSON string."));
        }
        string id = request[ScheduleFields.Id]?.ToString();
        JObject existing = string.IsNullOrEmpty(id) ? null : ScheduleStore.Get(user, id);
        if (!string.IsNullOrEmpty(id) && existing is null)
        {
            return Task.FromResult(Fail("There is no such schedule."));
        }
        JObject settings = SettingsService.GetMergedSettings(user);
        string assistantId = request[ScheduleFields.AssistantId]?.ToString()?.Trim();
        if (string.IsNullOrEmpty(assistantId))
        {
            assistantId = AssistantService.GetActiveAssistantId(settings, user);
        }
        else if ((settings["assistants"] as JObject)?[assistantId] is null)
        {
            return Task.FromResult(Fail($"There is no assistant '{assistantId}'."));
        }
        (JObject schedule, string error) = ScheduleRules.Build(request, existing, ScheduleSources.User, "", null, SessionManager.EffectiveToolIds(user, null, assistantId), DateTime.UtcNow);
        if (schedule is null)
        {
            return Task.FromResult(Fail(error));
        }
        if (!ScheduleStore.Save(user, schedule))
        {
            return Task.FromResult(Fail($"You can have at most {ScheduleLimits.MaxPerUser} schedules."));
        }
        return Task.FromResult(new JObject { ["success"] = true, ["id"] = schedule[ScheduleFields.Id] });
    }

    /// <summary>Deletes a schedule. The chat that listed its runs, and the runs, stay.</summary>
    public static Task<JObject> LLMAssistantDeleteSchedule(Session session, string scheduleId)
        => Task.FromResult(ScheduleStore.Delete(session.User, scheduleId) ? new JObject { ["success"] = true } : Fail("There is no such schedule."));

    /// <summary>Starts a run of a schedule now (the Run now button). It does not change when the schedule runs next. Reply: <c>{success, started, threadId?, status?, reason?}</c>.</summary>
    public static async Task<JObject> LLMAssistantRunSchedule(Session session, string scheduleId)
    {
        if (ScheduleStore.Get(session.User, scheduleId) is null)
        {
            return Fail("There is no such schedule.");
        }
        ScheduleRunResult result = await ScheduleService.RunNowAsync(session.User, scheduleId);
        JObject reply = new() { ["success"] = true, ["started"] = result.Started };
        if (result.Started)
        {
            reply["threadId"] = result.ThreadId;
            reply["status"] = result.Status;
        }
        else
        {
            reply["reason"] = result.Reason;
        }
        return reply;
    }

    /// <summary>When a trigger would run next, for the editor. Request: <c>{trigger, timezone?, count?}</c> (up to 10). Reply: <c>{success, times: [{utc, local}]}</c> with <c>local</c>
    /// in the trigger's time zone, or the reason it is not allowed.</summary>
    public static Task<JObject> LLMAssistantPreviewSchedule(Session session, JObject rawInput)
    {
        JObject triggerJson = rawInput["trigger"] as JObject;
        if (triggerJson is null && rawInput["trigger"]?.Type == JTokenType.String)
        {
            try
            {
                triggerJson = JObject.Parse(rawInput["trigger"].ToString());
            }
            catch (Newtonsoft.Json.JsonException)
            {
                triggerJson = null;
            }
        }
        DateTime now = DateTime.UtcNow;
        if (!ScheduleTrigger.TryFromJson(triggerJson, rawInput["timezone"]?.ToString(), now, out ScheduleTrigger trigger, out string error))
        {
            return Task.FromResult(Fail(error));
        }
        int count = rawInput["count"]?.Type == JTokenType.Integer ? Math.Clamp(rawInput["count"].Value<int>(), 1, 10) : 5;
        JArray times = [.. trigger.Preview(now, count).Select(t => new JObject
        {
            ["utc"] = t.ToString("o"),
            ["local"] = trigger.ToLocal(t).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        })];
        return Task.FromResult(new JObject { ["success"] = true, ["times"] = times, ["zone"] = trigger.ZoneId });
    }

    /// <summary>A failed reply.</summary>
    private static JObject Fail(string error) => new() { ["success"] = false, ["error"] = error };
}
