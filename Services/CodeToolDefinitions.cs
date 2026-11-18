using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>The definitions of the code tools, as the tool list stores them. All are <b>off</b> by default (like <c>shell_exec</c>) and need the
/// <c>llm_code_mode</c> permission to run, so using them takes three switches: the permission, the tool enabled globally, and the tool enabled on an assistant.
/// The descriptions say "in the chat's workspace" so a model does not mistake <c>code_read</c> for <c>file_read</c>.</summary>
public static class CodeToolDefinitions
{
    /// <summary>The definitions keyed by tool id.</summary>
    public static JObject Build(string now)
    {
        JObject tools = [];
        Add(tools, now, ToolConstants.CodeRead, "Read File (workspace)",
            "Read a text file in the chat's workspace, a range of lines at a time. Returns the text, the line range returned and the file's total line count. Use offset and limit to page through a long file.",
            Props(("path", "string", "Path of the file, relative to the workspace."), ("offset", "integer", "First line to return, counting from 1 (default 1)."), ("limit", "integer", "Most lines to return (default 2000, at most 5000).")), ["path"]);
        Add(tools, now, ToolConstants.CodeWrite, "Write File (workspace)",
            "Create a file in the chat's workspace, creating folders as needed. To replace a file that exists, pass overwrite=true; to change part of one, use code_edit instead.",
            Props(("path", "string", "Path of the file, relative to the workspace."), ("content", "string", "The whole text of the file."), ("overwrite", "boolean", "Replace the file if it exists (default false).")), ["path", "content"]);
        Add(tools, now, ToolConstants.CodeEdit, "Edit File (workspace)",
            "Change a file in the chat's workspace by replacing exact text. oldText must match the file exactly (spaces, tabs and blank lines included) and must appear once, unless replaceAll is true; include enough surrounding lines to make it unique. Line endings follow the file's own. Read the file first so oldText is copied, not remembered.",
            Props(("path", "string", "Path of the file, relative to the workspace."), ("oldText", "string", "The exact text to replace."), ("newText", "string", "The text to put in its place (empty to delete)."), ("replaceAll", "boolean", "Replace every occurrence (default false).")), ["path", "oldText", "newText"]);
        Add(tools, now, ToolConstants.CodeGlob, "Find Files (workspace)",
            "List files in the chat's workspace whose path matches a pattern: * is any run of characters within a folder name, ** crosses folders, ? is one character. A pattern with no / matches the file name anywhere (*.cs). Skips .git and node_modules.",
            Props(("pattern", "string", "For example **/*.cs or src/*.js."), ("path", "string", "Folder to search, relative to the workspace (default: the whole workspace).")), ["pattern"]);
        Add(tools, now, ToolConstants.CodeGrep, "Search Files (workspace)",
            "Search the text of files in the chat's workspace with a regular expression. Returns each matching line with its file and line number, at most 200. Skips binary and very large files, .git and node_modules.",
            Props(("pattern", "string", "The regular expression to look for."), ("path", "string", "A file or folder to search, relative to the workspace (default: the whole workspace)."), ("glob", "string", "Only search files whose path matches this pattern, for example *.cs."), ("ignoreCase", "boolean", "Match without regard to case (default false).")), ["pattern"]);
        Add(tools, now, ToolConstants.CodeBash, "Run Command (workspace)",
            "Run a shell command with the chat's workspace as its starting folder (cmd.exe on Windows, /bin/sh elsewhere) and return its exit code, standard output and standard error. The workspace is where it starts, not a fence: the command runs as the SwarmUI user and can reach anything that user can. Long output is cut.",
            Props(("command", "string", "The command line."), ("workingDirectory", "string", "Folder to start in, relative to the workspace (default: the workspace)."), ("timeoutSeconds", "integer", "Stop the command after this long (default 120, at most 600).")), ["command"]);
        Add(tools, now, ToolConstants.CodeTodo, "Task List",
            "Keep the task list for the job being done in the chat's workspace. Call it with the whole list each time: when you plan a job, and whenever a task starts or finishes. Each task has content and a status of pending, in_progress or completed. Use it for jobs with several steps.",
            new JObject
            {
                ["todos"] = new JObject
                {
                    ["type"] = "array",
                    ["description"] = "The whole task list.",
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["content"] = new JObject { ["type"] = "string", ["description"] = "What the task is." },
                            ["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray("pending", "in_progress", "completed") },
                        },
                        ["required"] = new JArray("content", "status"),
                    },
                },
            }, ["todos"]);
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
