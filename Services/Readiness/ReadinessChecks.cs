namespace Hartsy.Extensions.LLMAssistant.Services.Readiness;

/// <summary>How one part of the setup is doing.</summary>
public enum CheckStatus
{
    /// <summary>Working, or nothing to do.</summary>
    Ok,
    /// <summary>Works, but something is likely not what was meant.</summary>
    Warn,
    /// <summary>Does not work.</summary>
    Fail,
    /// <summary>Not set up, which is fine: said so that the report is complete.</summary>
    Info,
}

/// <summary>One line of the report: what was looked at, how it is, and what to do about it.</summary>
/// <param name="Id">A stable name for the check (<c>backends</c>, <c>mcp</c>, ...), for tools that read the report.</param>
/// <param name="Title">What it is, for people.</param>
/// <param name="Status">How it is.</param>
/// <param name="Detail">What was found.</param>
/// <param name="Fix">What to do, when something is wrong.</param>
public sealed record ReadinessCheck(string Id, string Title, CheckStatus Status, string Detail, string Fix = null);

/// <summary>Everything the checks look at, gathered into plain numbers and names so that deciding what each means is a pure function that tests can feed. Gathering it is
/// <see cref="ReadinessService"/>'s job, and only reads: nothing here starts a connection, loads a model or changes a setting. Names and error texts that only an administrator may
/// see (an MCP server's command, a hook's reason) arrive here already left out for anyone else, so a check cannot leak what it was not given.</summary>
public sealed class ReadinessInputs
{
    /// <summary>How many LLM backends are registered.</summary>
    public int Providers;
    /// <summary>How many models they offer in all.</summary>
    public int Models;
    /// <summary>The model the user prefers, or empty.</summary>
    public string PreferredModel = "";
    /// <summary>Whether the preferred model is offered.</summary>
    public bool PreferredModelFound;
    /// <summary>How many tools are switched on.</summary>
    public int ToolsEnabled;
    /// <summary>Switched-on tools whose handler is not registered (a custom tool pointing at nothing).</summary>
    public List<string> ToolsWithoutHandler = [];
    /// <summary>Switched-on tools the user may not run.</summary>
    public int ToolsDenied;
    /// <summary>MCP servers by state.</summary>
    public int McpTotal, McpReady, McpFailed, McpConnecting, McpDisabled;
    /// <summary>Failed MCP servers and why, when the user may see that (empty otherwise).</summary>
    public List<(string Name, string Error)> McpErrors = [];
    /// <summary>Hooks the user can see, and how many of them could not be read.</summary>
    public int HooksTotal, HooksInvalid;
    /// <summary>Skills and commands the user can use.</summary>
    public int PromptAssets;
    /// <summary>Documents in the knowledge library.</summary>
    public int KnowledgeDocuments;
    /// <summary>Whether an embedding model is set for the library.</summary>
    public bool EmbeddingSet;
    /// <summary>Whether the embedding model was tried this time, and the reason it failed (null when it worked or was not tried).</summary>
    public bool EmbeddingProbed;
    public string EmbeddingError;
    /// <summary>Whether the scheduler loop is running.</summary>
    public bool SchedulerRunning;
    /// <summary>The user's schedules that are switched on, and how many of them skipped a run in the last day.</summary>
    public int SchedulesEnabled, SchedulesSkipping;
    /// <summary>The user's sessions working now, and waiting for a place.</summary>
    public int SessionsWorking, SessionsQueued;
    /// <summary>Whether the extension's data folder can be written, and why not.</summary>
    public bool StorageWritable = true;
    public string StorageError;
    /// <summary>Parts that could not be looked at (a failure while gathering), reported as lines of their own.</summary>
    public List<ReadinessCheck> Unchecked = [];
    /// <summary>Whether the check was asked to try things over the network (the model list and the embedding model).</summary>
    public bool Deep;
}

/// <summary>Deciding what a gathered setup means: one check per part, each with a status and, when something is wrong, what to do. Pure, so every decision is tested with made-up inputs
/// and none of them needs a broken backend to be reproduced.</summary>
public static class ReadinessChecks
{
    /// <summary>The checks, in the order they are shown.</summary>
    public static List<ReadinessCheck> Evaluate(ReadinessInputs i)
    {
        List<ReadinessCheck> checks = [Backends(i), Model(i), Tools(i), Mcp(i), Hooks(i), Skills(i), Knowledge(i), Schedules(i), Sessions(i), Storage(i)];
        // A part that could not be looked at replaces whatever its empty inputs would have said.
        foreach (ReadinessCheck missed in i.Unchecked)
        {
            int at = checks.FindIndex(c => c.Id == missed.Id);
            if (at >= 0)
            {
                checks[at] = missed;
            }
            else
            {
                checks.Add(missed);
            }
        }
        return checks;
    }

    /// <summary>The worst status in a report: a fail if any check failed, else a warning if any, else ok.</summary>
    public static CheckStatus Overall(IEnumerable<ReadinessCheck> checks)
    {
        List<ReadinessCheck> list = [.. checks];
        return list.Any(c => c.Status == CheckStatus.Fail) ? CheckStatus.Fail : list.Any(c => c.Status == CheckStatus.Warn) ? CheckStatus.Warn : CheckStatus.Ok;
    }

    /// <summary>Plural or singular.</summary>
    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>Is there something to chat with.</summary>
    private static ReadinessCheck Backends(ReadinessInputs i)
    {
        if (i.Providers == 0)
        {
            return new("backends", "LLM backends", CheckStatus.Fail, "No LLM backend is set up.", "Add one under Server > Backends: a local model folder, or an OpenAI-compatible address.");
        }
        if (i.Models == 0)
        {
            return new("backends", "LLM backends", CheckStatus.Fail, $"{Count(i.Providers, "backend")}, but no model is offered.", "Check that a backend is running and has a model: Server > Backends shows each one's status.");
        }
        return new("backends", "LLM backends", CheckStatus.Ok, $"{Count(i.Providers, "backend")}, {Count(i.Models, "model")}.");
    }

    /// <summary>Is the model the user asked for there.</summary>
    private static ReadinessCheck Model(ReadinessInputs i)
    {
        if (string.IsNullOrWhiteSpace(i.PreferredModel))
        {
            return new("model", "Preferred model", CheckStatus.Info, "None chosen; a request that names no model uses the backend's default model, else the first one offered.");
        }
        return i.Models > 0 && !i.PreferredModelFound
            ? new("model", "Preferred model", CheckStatus.Warn, $"'{i.PreferredModel}' is not offered by any backend.", "Pick another model in the chat's model list, or start the backend that has it.")
            : new("model", "Preferred model", i.Models == 0 ? CheckStatus.Info : CheckStatus.Ok, i.Models == 0 ? $"'{i.PreferredModel}' (no models are offered yet to check it against)." : $"'{i.PreferredModel}'.");
    }

    /// <summary>Do the switched-on tools have something to run them.</summary>
    private static ReadinessCheck Tools(ReadinessInputs i)
    {
        if (i.ToolsWithoutHandler.Count > 0)
        {
            return new("tools", "Tools", CheckStatus.Fail, $"{Count(i.ToolsWithoutHandler.Count, "tool")} point at nothing that can run them: {string.Join(", ", i.ToolsWithoutHandler.Take(5))}.", "Edit or delete them in Settings > Tools.");
        }
        string detail = $"{Count(i.ToolsEnabled, "tool")} switched on";
        return i.ToolsDenied > 0
            ? new("tools", "Tools", CheckStatus.Warn, $"{detail}; {i.ToolsDenied} of them this user may not run.", "An administrator can grant the permission for each one.")
            : new("tools", "Tools", CheckStatus.Ok, detail + ".");
    }

    /// <summary>Are the MCP servers up.</summary>
    private static ReadinessCheck Mcp(ReadinessInputs i)
    {
        if (i.McpTotal == 0)
        {
            return new("mcp", "MCP servers", CheckStatus.Info, "None set up.");
        }
        string detail = $"{i.McpReady} of {i.McpTotal} ready";
        if (i.McpDisabled > 0)
        {
            detail += $", {i.McpDisabled} switched off";
        }
        if (i.McpConnecting > 0)
        {
            detail += $", {i.McpConnecting} connecting";
        }
        if (i.McpFailed == 0)
        {
            return new("mcp", "MCP servers", CheckStatus.Ok, detail + ".");
        }
        string why = i.McpErrors.Count > 0 ? " " + string.Join(" ", i.McpErrors.Take(3).Select(e => $"{e.Name}: {e.Error}")) : "";
        return new("mcp", "MCP servers", CheckStatus.Warn, $"{detail}, {i.McpFailed} failed.{why}", "Open Settings > MCP Servers and use Reconnect, or fix the server's command or address.");
    }

    /// <summary>Can the hooks be read.</summary>
    private static ReadinessCheck Hooks(ReadinessInputs i)
    {
        if (i.HooksTotal == 0)
        {
            return new("hooks", "Hooks", CheckStatus.Info, "None set up.");
        }
        return i.HooksInvalid > 0
            ? new("hooks", "Hooks", CheckStatus.Warn, $"{i.HooksInvalid} of {i.HooksTotal} could not be read, so they do nothing.", "Open Settings > Hooks and check their rules.")
            : new("hooks", "Hooks", CheckStatus.Ok, $"{Count(i.HooksTotal, "hook")}.");
    }

    /// <summary>Skills and commands.</summary>
    private static ReadinessCheck Skills(ReadinessInputs i)
        => new("skills", "Skills and commands", i.PromptAssets == 0 ? CheckStatus.Info : CheckStatus.Ok, i.PromptAssets == 0 ? "None yet." : $"{i.PromptAssets} available.");

    /// <summary>The knowledge library, and its embedding model when it was tried.</summary>
    private static ReadinessCheck Knowledge(ReadinessInputs i)
    {
        if (i.KnowledgeDocuments == 0 && !i.EmbeddingSet)
        {
            return new("knowledge", "Knowledge library", CheckStatus.Info, "No documents, and no embedding model.");
        }
        string detail = $"{Count(i.KnowledgeDocuments, "document")}, searched by {(i.EmbeddingSet ? "meaning and words" : "words")}";
        if (i.EmbeddingSet && i.EmbeddingProbed && i.EmbeddingError is not null)
        {
            return new("knowledge", "Knowledge library", CheckStatus.Warn, $"{detail}. The embedding model did not answer: {i.EmbeddingError}", "Open Settings > Knowledge and save the model again to try it; searches use words until it works.");
        }
        return new("knowledge", "Knowledge library", CheckStatus.Ok, detail + (i.EmbeddingSet && !i.EmbeddingProbed ? " (the model was not tried: run a deep check)." : "."));
    }

    /// <summary>Is the scheduler going, and are the schedules running.</summary>
    private static ReadinessCheck Schedules(ReadinessInputs i)
    {
        if (i.SchedulesEnabled == 0)
        {
            return new("schedules", "Schedules", CheckStatus.Info, "None switched on.");
        }
        if (!i.SchedulerRunning)
        {
            return new("schedules", "Schedules", CheckStatus.Fail, $"{Count(i.SchedulesEnabled, "schedule")} switched on, but the scheduler is not running.", "Restart SwarmUI; the scheduler starts with the extension.");
        }
        return i.SchedulesSkipping > 0
            ? new("schedules", "Schedules", CheckStatus.Warn, $"{Count(i.SchedulesEnabled, "schedule")} switched on; {i.SchedulesSkipping} skipped a run in the last day.", "Open Settings > Schedules: each one says why it skipped.")
            : new("schedules", "Schedules", CheckStatus.Ok, $"{Count(i.SchedulesEnabled, "schedule")} switched on.");
    }

    /// <summary>Sessions working in the background.</summary>
    private static ReadinessCheck Sessions(ReadinessInputs i)
        => i.SessionsWorking + i.SessionsQueued == 0
            ? new("sessions", "Sessions", CheckStatus.Info, "None working.")
            : new("sessions", "Sessions", CheckStatus.Ok, $"{i.SessionsWorking} working, {i.SessionsQueued} waiting for a place.");

    /// <summary>Can the extension save anything.</summary>
    private static ReadinessCheck Storage(ReadinessInputs i)
        => i.StorageWritable
            ? new("storage", "Storage", CheckStatus.Ok, "The extension's data folder can be written.")
            : new("storage", "Storage", CheckStatus.Fail, $"The extension's data folder cannot be written: {i.StorageError}", "Check the folder's permissions and the disk space; skills, documents and schedules are saved there.");
}
