using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>The definitions of the session tools, as the tool list stores them. All are <b>off</b> by default and need the <c>llm_tool_sessions</c> permission, so using them takes
/// three switches: the permission, the tool enabled on the Tools tab, and the tool listed on an assistant.</summary>
public static class SessionToolDefinitions
{
    /// <summary>The definitions keyed by tool id.</summary>
    public static JObject Build(string now)
    {
        JObject tools = [];
        Add(tools, now, ToolConstants.SessionStart, "Start Session",
            "Start a session: a chat of its own that works on one job in the background, with your tools (or fewer), and reports back when it is done. Use it for a job that can go on beside your own work, or several at once. Give it everything it needs in the prompt, because it cannot see this conversation. Returns its id; use session_wait to get its report.",
            Props(("title", "string", "A short name for the session, shown in the chat list."), ("prompt", "string", "The whole job, written as if to someone who has not seen this conversation."),
                ("model", "string", "The model to run it on (default: this chat's)."), ("assistantId", "string", "The assistant to run it as (default: this chat's)."),
                ("approvalMode", "string", "How its tool calls are approved: auto, ask or plan. It is never less careful than this chat.")),
            ["title", "prompt"],
            ("tools", new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "The tool ids it may use, out of your own. Leave out for all of them." }));
        Add(tools, now, ToolConstants.SessionSend, "Message Session",
            "Send a message to a session you started. If it is working, it reads the message when its current turn ends; if it has finished or was stopped, the message starts a new turn.",
            Props(("id", "string", "The session's id."), ("message", "string", "What to tell it.")), ["id", "message"]);
        Add(tools, now, ToolConstants.SessionList, "List Sessions",
            "List the sessions you started and how each is doing (queued, running, awaiting_approval, idle, done, failed or interrupted), with its report once it has one.",
            [], []);
        Add(tools, now, ToolConstants.SessionRead, "Read Session",
            "Read a session you started: its status, its report if it has one, and the end of its conversation.",
            Props(("id", "string", "The session's id."), ("last", "integer", "How many of its latest messages to return (default 6, at most 50).")), ["id"]);
        Add(tools, now, ToolConstants.SessionWait, "Wait for Session",
            "Wait for a session you started to finish, up to timeoutSeconds, and return its report. Returns sooner if the session needs the user to allow a tool call, which only the user can do.",
            Props(("id", "string", "The session's id."), ("timeoutSeconds", "integer", "The longest to wait (default 120, at most 600).")), ["id"]);
        Add(tools, now, ToolConstants.SessionStop, "Stop Session",
            "Stop a session you started that is working. What it had done is kept, and you can message it again later.",
            Props(("id", "string", "The session's id.")), ["id"]);
        Add(tools, now, ToolConstants.SessionArchive, "Archive Session",
            "Put a session you started away: it is stopped and leaves the chat list, and no longer counts toward the limit on how many sessions you can have.",
            Props(("id", "string", "The session's id.")), ["id"]);
        return tools;
    }

    /// <summary>An object schema's <c>properties</c> from (name, type, description) triples.</summary>
    private static JObject Props(params (string Name, string Type, string Description)[] properties)
    {
        JObject result = [];
        foreach ((string name, string type, string description) in properties)
        {
            result[name] = new JObject { ["type"] = type, ["description"] = description };
        }
        return result;
    }

    /// <summary>Adds one definition; <paramref name="extra"/> adds properties that are not plain strings, integers or booleans.</summary>
    private static void Add(JObject tools, string now, string id, string name, string description, JObject properties, string[] required, params (string Name, JObject Schema)[] extra)
    {
        foreach ((string propertyName, JObject schema) in extra)
        {
            properties[propertyName] = schema;
        }
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
