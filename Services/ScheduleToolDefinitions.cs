using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>The definitions of the schedule tools, as the tool list stores them. All are <b>off</b> by default and need the <c>llm_tool_schedules</c> permission, so using them takes three
/// switches: the permission, the tool enabled on the Tools tab, and the tool listed on an assistant. What they make is saved switched off.</summary>
public static class ScheduleToolDefinitions
{
    /// <summary>The definitions keyed by tool id.</summary>
    public static JObject Build(string now)
    {
        JObject tools = [];
        Add(tools, now, ToolConstants.ScheduleCreate, "Schedule a Task",
            "Make a schedule: a prompt that runs later, on a timetable, in a session of its own, with no one watching. Say when with exactly one of everyMinutes (at least 5), cron (five fields, for example \"0 9 * * 1-5\" for 9:00 on weekdays) or at (a date and time, for example 2026-12-01T09:00:00Z). The prompt must say everything the run needs, because it cannot see this conversation. The schedule is saved switched off: tell the user it is waiting for them to turn it on in Settings > Schedules. A run can use only your tools, and is never less careful about approvals than this chat.",
            new JObject
            {
                ["title"] = Prop("string", "A short name, shown in the chat list."),
                ["prompt"] = Prop("string", "The whole job, written as if to someone who has not seen this conversation."),
                ["everyMinutes"] = Prop("integer", "Run every this many minutes (5 or more)."),
                ["cron"] = Prop("string", "Five fields: minute hour day-of-month month day-of-week."),
                ["at"] = Prop("string", "Run once at this date and time."),
                ["timezone"] = Prop("string", "The time zone cron is read in, for example Europe/Paris (default UTC)."),
                ["approvalMode"] = Prop("string", "How the run's tool calls are approved: auto, ask or plan (default ask). Never looser than this chat's."),
                ["model"] = Prop("string", "The model to run it on (default: this chat's)."),
                ["tools"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "The tool ids the run may use, out of your own. Leave out for all of them." },
            }, ["title", "prompt"]);
        Add(tools, now, ToolConstants.ScheduleList, "List Schedules",
            "List the schedules this chat made: when each runs, whether the user has turned it on, and how its last run went.", [], []);
        Add(tools, now, ToolConstants.ScheduleCancel, "Cancel a Schedule",
            "Delete a schedule this chat made. A run it already started is left to finish.",
            new JObject { ["id"] = Prop("string", "The schedule's id.") }, ["id"]);
        return tools;
    }

    /// <summary>A property schema.</summary>
    private static JObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    /// <summary>Adds one definition.</summary>
    private static void Add(JObject tools, string now, string id, string name, string description, JObject properties, string[] required)
    {
        tools[id] = new JObject
        {
            ["id"] = id,
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray(required) },
            ["handlerType"] = ToolConstants.HandlerBuiltIn,
            ["handlerId"] = id,
            ["enabled"] = false,
            ["isBuiltIn"] = true,
            ["created"] = now,
            ["updated"] = now,
        };
    }
}
