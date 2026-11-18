using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Hartsy.Extensions.LLMAssistant.Services;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;

/// <summary>What the code tools have in common: finding the chat's workspace, keeping paths inside it, and the shape of an error.</summary>
public abstract class CodeToolBase : ToolHandler
{
    /// <summary>The largest file the tools will read or change.</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;

    /// <summary>Folders a search never goes into: version control's own data and dependency folders, which are large and never what is meant.</summary>
    protected static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules" };

    /// <summary>The standard failure result.</summary>
    protected static JObject Fail(string message) => new() { ["success"] = false, ["error"] = message };

    /// <summary>The workspace the call works in: the chat's own, or the user's default when the chat has none (or the call was not made from a chat).
    /// Returns null with the failure to give back when there is none the user may use.</summary>
    protected static Workspace GetWorkspace(ToolExecutionContext ctx, out JObject failure)
    {
        failure = null;
        User user = ctx.Session?.User;
        if (user is null)
        {
            failure = Fail("The code tools need an authenticated user.");
            return null;
        }
        JObject thread = string.IsNullOrEmpty(ctx.ThreadId) ? null : Services.ThreadStorageService.GetThread(user, ctx.ThreadId);
        Workspace workspace = WorkspaceService.ResolveForThread(user, thread);
        if (workspace is null)
        {
            failure = Fail("This chat's workspace is not available (it was removed, or you may no longer use it). Pick another workspace for the chat.");
        }
        return workspace;
    }

    /// <summary>Resolves a path argument to a full path inside the workspace, or null with the failure to give back.</summary>
    protected static string ResolvePath(Workspace workspace, string requested, out JObject failure)
    {
        failure = null;
        if (PathSandbox.TryResolve(workspace.Root, requested, out string full, out string error))
        {
            return full;
        }
        failure = Fail(error);
        return null;
    }

    /// <summary>Whether a path is somewhere the tools never change: inside a <c>.git</c> folder, where a hook is a way to run a program.</summary>
    protected static bool IsProtected(Workspace workspace, string fullPath)
        => PathSandbox.Relative(workspace.Root, fullPath).Split('/').Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the start of a file looks like binary data (a NUL byte), which a text tool should not read or change.</summary>
    protected static bool LooksBinary(string fullPath)
    {
        byte[] head = new byte[8192];
        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int read = stream.Read(head, 0, head.Length);
        return Array.IndexOf(head, (byte)0, 0, read) >= 0;
    }

    /// <summary>Every file under <paramref name="start"/> (a folder), skipping <see cref="SkippedFolders"/> and anything reached through a link, as full paths in a stable order.
    /// At most <paramref name="maxVisited"/> files are looked at.</summary>
    protected static IEnumerable<string> WalkFiles(string start, int maxVisited, CancellationToken ct)
    {
        Stack<string> pending = new();
        pending.Push(start);
        int visited = 0;
        while (pending.Count > 0 && visited < maxVisited)
        {
            ct.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            IEnumerable<string> subfolders;
            IEnumerable<string> files;
            try
            {
                subfolders = Directory.EnumerateDirectories(directory).OrderByDescending(d => d, StringComparer.Ordinal).ToList();
                files = Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (string file in files)
            {
                if (++visited > maxVisited)
                {
                    yield break;
                }
                // A link to a file may lead outside the workspace, and a search reads what it finds; only real files are searched.
                if (new FileInfo(file).LinkTarget is not null)
                {
                    continue;
                }
                yield return file;
            }
            foreach (string subfolder in subfolders)
            {
                if (!SkippedFolders.Contains(Path.GetFileName(subfolder)) && new DirectoryInfo(subfolder).LinkTarget is null)
                {
                    pending.Push(subfolder);
                }
            }
        }
    }
}

/// <summary>code_read: read a text file in the chat's workspace, a range of lines at a time.</summary>
public sealed class CodeReadTool : CodeToolBase
{
    /// <summary>Most lines one call returns, and the longest single result in characters.</summary>
    public const int DefaultLines = 2000, MaxLines = 5000, MaxChars = 120_000;

    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.CodeRead;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    /// <inheritdoc/>
    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        Workspace workspace = GetWorkspace(ctx, out JObject failure);
        if (workspace is null)
        {
            return failure;
        }
        return await Run(workspace, ctx.Args, ctx.Ct);
    }

    /// <summary>Does the work in <paramref name="workspace"/>; separate from <see cref="Execute"/> so it can be run without a session.</summary>
    internal static async Task<JObject> Run(Workspace workspace, JObject args, CancellationToken ct)
    {
        JObject failure;
        string full = ResolvePath(workspace, args["path"]?.ToString(), out failure);
        if (full is null)
        {
            return failure;
        }
        if (!File.Exists(full))
        {
            return Fail(Directory.Exists(full) ? "That is a folder, not a file. Use code_glob to list what is in it." : $"File not found: {args["path"]}");
        }
        FileInfo info = new(full);
        if (info.Length > MaxFileBytes)
        {
            return Fail($"The file is too large to read ({info.Length / (1024 * 1024)} MB). Use code_grep to find the part you need.");
        }
        if (LooksBinary(full))
        {
            return Fail("That looks like a binary file, which this tool does not read.");
        }
        int offset = Math.Max(1, args["offset"]?.Value<int?>() ?? 1);
        int limit = Math.Clamp(args["limit"]?.Value<int?>() ?? DefaultLines, 1, MaxLines);
        StringBuilder content = new();
        int line = 0;
        int taken = 0;
        bool cut = false;
        // Shared with writers, so a file another program has open (a build log) can still be read.
        using FileStream stream = new(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(ct) is { } text)
        {
            line++;
            if (line < offset)
            {
                continue;
            }
            if (cut || taken >= limit || content.Length + text.Length + 1 > MaxChars)
            {
                // Once a line does not fit, no later line is taken: the range returned stays unbroken. Keep counting, so the model learns how long the file is.
                cut = true;
                continue;
            }
            content.Append(text).Append('\n');
            taken++;
        }
        return new JObject
        {
            ["success"] = true,
            ["path"] = PathSandbox.Relative(workspace.Root, full),
            ["content"] = content.ToString(),
            ["startLine"] = offset,
            ["endLine"] = offset + taken - 1,
            ["totalLines"] = line,
            ["truncated"] = cut,
        };
    }
}

/// <summary>code_write: create a file in the chat's workspace, or replace one.</summary>
public sealed class CodeWriteTool : CodeToolBase
{
    /// <summary>The most text one write may hold.</summary>
    public const int MaxChars = 2 * 1024 * 1024;

    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.CodeWrite;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Write;

    /// <inheritdoc/>
    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        Workspace workspace = GetWorkspace(ctx, out JObject failure);
        if (workspace is null)
        {
            return Task.FromResult(failure);
        }
        return Run(workspace, ctx.Args, ctx.Ct);
    }

    /// <summary>Does the work in <paramref name="workspace"/>; separate from <see cref="Execute"/> so it can be run without a session.</summary>
    internal static Task<JObject> Run(Workspace workspace, JObject args, CancellationToken ct)
    {
        JObject failure;
        string content = args["content"]?.ToString();
        if (content is null)
        {
            return Task.FromResult(Fail("content is required."));
        }
        if (content.Length > MaxChars)
        {
            return Task.FromResult(Fail($"The content is too large (over {MaxChars / (1024 * 1024)} MB). Write it in parts, or as several files."));
        }
        string full = ResolvePath(workspace, args["path"]?.ToString(), out failure);
        if (full is null)
        {
            return Task.FromResult(failure);
        }
        if (IsProtected(workspace, full))
        {
            return Task.FromResult(Fail("Files inside .git are not changed by this tool."));
        }
        if (Directory.Exists(full))
        {
            return Task.FromResult(Fail("That is a folder, not a file."));
        }
        bool exists = File.Exists(full);
        bool overwrite = args["overwrite"]?.Value<bool>() ?? false;
        if (exists && !overwrite)
        {
            return Task.FromResult(Fail("The file already exists. To replace it, call again with overwrite=true; to change part of it, use code_edit."));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        WriteAtomically(full, content, bom: false);
        return Task.FromResult(new JObject
        {
            ["success"] = true,
            ["path"] = PathSandbox.Relative(workspace.Root, full),
            ["created"] = !exists,
            ["bytes"] = Encoding.UTF8.GetByteCount(content),
        });
    }

    /// <summary>Writes through a temporary file in the same folder and moves it over the target, so a failure part-way never leaves half a file.</summary>
    internal static void WriteAtomically(string fullPath, string content, bool bom)
    {
        string temp = Path.Combine(Path.GetDirectoryName(fullPath)!, $".llma-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(bom));
            // A file that is replaced keeps its permissions (the executable bit of a script, for one).
            if (!OperatingSystem.IsWindows() && File.Exists(fullPath))
            {
                File.SetUnixFileMode(temp, File.GetUnixFileMode(fullPath));
            }
            File.Move(temp, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}

/// <summary>code_edit: replace exact text in a file in the chat's workspace.</summary>
public sealed class CodeEditTool : CodeToolBase
{
    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.CodeEdit;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Write;

    /// <inheritdoc/>
    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        Workspace workspace = GetWorkspace(ctx, out JObject failure);
        if (workspace is null)
        {
            return Task.FromResult(failure);
        }
        return Run(workspace, ctx.Args, ctx.Ct);
    }

    /// <summary>Does the work in <paramref name="workspace"/>; separate from <see cref="Execute"/> so it can be run without a session.</summary>
    internal static Task<JObject> Run(Workspace workspace, JObject args, CancellationToken ct)
    {
        JObject failure;
        string oldText = args["oldText"]?.ToString();
        string newText = args["newText"]?.ToString();
        bool replaceAll = args["replaceAll"]?.Value<bool>() ?? false;
        if (string.IsNullOrEmpty(oldText))
        {
            return Task.FromResult(Fail("oldText is required: the exact text to replace. To create a file use code_write."));
        }
        if (newText is null)
        {
            return Task.FromResult(Fail("newText is required (it can be empty to delete the text)."));
        }
        string full = ResolvePath(workspace, args["path"]?.ToString(), out failure);
        if (full is null)
        {
            return Task.FromResult(failure);
        }
        if (IsProtected(workspace, full))
        {
            return Task.FromResult(Fail("Files inside .git are not changed by this tool."));
        }
        if (!File.Exists(full))
        {
            return Task.FromResult(Fail($"File not found: {args["path"]}"));
        }
        if (new FileInfo(full).Length > MaxFileBytes)
        {
            return Task.FromResult(Fail("The file is too large to edit."));
        }
        if (LooksBinary(full))
        {
            return Task.FromResult(Fail("That looks like a binary file, which this tool does not change."));
        }
        byte[] raw = File.ReadAllBytes(full);
        bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        string content = new UTF8Encoding(false).GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
        // The file's own line endings win: the model writes "\n" and a Windows file holds "\r\n", and the edit must neither fail on that nor
        // turn some of the file's lines into the other ending.
        string eol = content.Contains("\r\n") ? "\r\n" : "\n";
        oldText = NormalizeEndings(oldText, eol);
        newText = NormalizeEndings(newText, eol);
        if (oldText == newText)
        {
            return Task.FromResult(Fail("oldText and newText are the same, so nothing would change."));
        }
        int count = CountOccurrences(content, oldText);
        if (count == 0)
        {
            return Task.FromResult(Fail("oldText was not found in the file. It must match exactly, including spaces, tabs and blank lines; read the file again and copy the text."));
        }
        if (count > 1 && !replaceAll)
        {
            return Task.FromResult(Fail($"oldText appears {count} times, so it is not clear which to change. Include more of the surrounding text to make it unique, or set replaceAll=true to change every one."));
        }
        string updated = replaceAll ? content.Replace(oldText, newText, StringComparison.Ordinal) : ReplaceFirst(content, oldText, newText);
        if (updated.Length > CodeWriteTool.MaxChars * 4)
        {
            return Task.FromResult(Fail("The result would be too large."));
        }
        CodeWriteTool.WriteAtomically(full, updated, bom);
        return Task.FromResult(new JObject
        {
            ["success"] = true,
            ["path"] = PathSandbox.Relative(workspace.Root, full),
            ["replacements"] = replaceAll ? count : 1,
            ["diff"] = SimpleDiff(content, updated),
        });
    }

    /// <summary>The text with every line ending turned into <paramref name="eol"/>.</summary>
    internal static string NormalizeEndings(string text, string eol) => text.Replace("\r\n", "\n").Replace("\n", eol);

    /// <summary>How many times <paramref name="needle"/> occurs in <paramref name="haystack"/> without overlapping.</summary>
    internal static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>The text with its first occurrence of <paramref name="oldText"/> replaced.</summary>
    internal static string ReplaceFirst(string content, string oldText, string newText)
    {
        int at = content.IndexOf(oldText, StringComparison.Ordinal);
        return content[..at] + newText + content[(at + oldText.Length)..];
    }

    /// <summary>A short account of what changed, in the <c>-removed</c> and <c>+added</c> form with two lines of context, for the model and for a diff view.
    /// It covers the span from the first changed line to the last; a very long change is cut.</summary>
    internal static string SimpleDiff(string before, string after)
    {
        string[] a = before.Split('\n');
        string[] b = after.Split('\n');
        int start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start])
        {
            start++;
        }
        int endA = a.Length - 1;
        int endB = b.Length - 1;
        while (endA >= start && endB >= start && a[endA] == b[endB])
        {
            endA--;
            endB--;
        }
        StringBuilder sb = new();
        sb.Append($"@@ line {start + 1} @@\n");
        for (int i = Math.Max(0, start - 2); i < start; i++)
        {
            sb.Append(' ').Append(a[i].TrimEnd('\r')).Append('\n');
        }
        int shown = 0;
        for (int i = start; i <= endA; i++)
        {
            sb.Append(shown++ < 40 ? "-" + a[i].TrimEnd('\r') + "\n" : "");
        }
        shown = 0;
        for (int i = start; i <= endB; i++)
        {
            sb.Append(shown++ < 40 ? "+" + b[i].TrimEnd('\r') + "\n" : "");
        }
        for (int i = endA + 1; i < Math.Min(a.Length, endA + 3); i++)
        {
            sb.Append(' ').Append(a[i].TrimEnd('\r')).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}

/// <summary>code_glob: list files in the chat's workspace by name pattern.</summary>
public sealed class CodeGlobTool : CodeToolBase
{
    /// <summary>Most results returned, and most files looked at.</summary>
    public const int MaxResults = 500, MaxVisited = 100_000;

    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.CodeGlob;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    /// <inheritdoc/>
    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        Workspace workspace = GetWorkspace(ctx, out JObject failure);
        if (workspace is null)
        {
            return Task.FromResult(failure);
        }
        return Run(workspace, ctx.Args, ctx.Ct);
    }

    /// <summary>Does the work in <paramref name="workspace"/>; separate from <see cref="Execute"/> so it can be run without a session.</summary>
    internal static Task<JObject> Run(Workspace workspace, JObject args, CancellationToken ct)
    {
        JObject failure;
        Regex pattern = PathGlob.Compile(args["pattern"]?.ToString(), out string error);
        if (pattern is null)
        {
            return Task.FromResult(Fail(error));
        }
        string start = ResolvePath(workspace, args["path"]?.ToString(), out failure);
        if (start is null)
        {
            return Task.FromResult(failure);
        }
        if (!Directory.Exists(start))
        {
            return Task.FromResult(Fail("The folder to search does not exist."));
        }
        List<string> matches = [];
        bool truncated = false;
        foreach (string file in WalkFiles(start, MaxVisited, ct))
        {
            if (!PathGlob.IsMatch(pattern, PathSandbox.Relative(start, file)))
            {
                continue;
            }
            if (matches.Count >= MaxResults)
            {
                truncated = true;
                break;
            }
            matches.Add(PathSandbox.Relative(workspace.Root, file));
        }
        return Task.FromResult(new JObject { ["success"] = true, ["files"] = new JArray(matches), ["count"] = matches.Count, ["truncated"] = truncated });
    }
}

/// <summary>code_grep: search the text of files in the chat's workspace with a regular expression.</summary>
public sealed class CodeGrepTool : CodeToolBase
{
    /// <summary>Most matching lines returned, files looked at, and the largest file searched.</summary>
    public const int MaxMatches = 200, MaxVisited = 50_000;

    /// <summary>The largest file searched.</summary>
    public const long MaxSearchedBytes = 2L * 1024 * 1024;

    /// <summary>Longest a regular expression may take on one line.</summary>
    private static readonly TimeSpan LineTimeout = TimeSpan.FromMilliseconds(200);

    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.CodeGrep;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Read;

    /// <inheritdoc/>
    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        Workspace workspace = GetWorkspace(ctx, out JObject failure);
        if (workspace is null)
        {
            return Task.FromResult(failure);
        }
        return Run(workspace, ctx.Args, ctx.Ct);
    }

    /// <summary>Does the work in <paramref name="workspace"/>; separate from <see cref="Execute"/> so it can be run without a session.</summary>
    internal static Task<JObject> Run(Workspace workspace, JObject args, CancellationToken ct)
    {
        JObject failure;
        string text = args["pattern"]?.ToString();
        if (string.IsNullOrEmpty(text) || text.Length > 500)
        {
            return Task.FromResult(Fail("pattern is required: a regular expression of up to 500 characters."));
        }
        Regex regex;
        try
        {
            regex = new Regex(text, ((args["ignoreCase"]?.Value<bool>() ?? false) ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, LineTimeout);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(Fail($"The pattern is not a valid regular expression: {ex.Message}"));
        }
        Regex fileFilter = null;
        string filterText = args["glob"]?.ToString();
        if (!string.IsNullOrWhiteSpace(filterText))
        {
            fileFilter = PathGlob.Compile(filterText, out string error);
            if (fileFilter is null)
            {
                return Task.FromResult(Fail(error));
            }
        }
        string start = ResolvePath(workspace, args["path"]?.ToString(), out failure);
        if (start is null)
        {
            return Task.FromResult(failure);
        }
        IEnumerable<string> files = File.Exists(start) ? [start] : Directory.Exists(start) ? WalkFiles(start, MaxVisited, ct) : [];
        string searchRoot = File.Exists(start) ? Path.GetDirectoryName(start)! : start;
        JArray matches = [];
        bool truncated = false;
        int filesWithMatches = 0;
        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (fileFilter is not null && !PathGlob.IsMatch(fileFilter, PathSandbox.Relative(searchRoot, file)))
            {
                continue;
            }
            FileInfo info = new(file);
            if (info.Length > MaxSearchedBytes || LooksBinary(file))
            {
                continue;
            }
            bool any = false;
            int number = 0;
            foreach (string line in File.ReadLines(file))
            {
                number++;
                bool hit;
                try
                {
                    hit = regex.IsMatch(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    return Task.FromResult(Fail("The pattern takes too long to match; make it simpler."));
                }
                if (!hit)
                {
                    continue;
                }
                if (matches.Count >= MaxMatches)
                {
                    truncated = true;
                    break;
                }
                any = true;
                matches.Add(new JObject { ["file"] = PathSandbox.Relative(workspace.Root, file), ["line"] = number, ["text"] = line.Length > 300 ? line[..300] + "…" : line });
            }
            if (any)
            {
                filesWithMatches++;
            }
            if (truncated)
            {
                break;
            }
        }
        return Task.FromResult(new JObject { ["success"] = true, ["matches"] = matches, ["count"] = matches.Count, ["files"] = filesWithMatches, ["truncated"] = truncated });
    }
}

/// <summary>code_bash: run a shell command in the chat's workspace. The folder is where it starts, not a fence: a command can do anything the SwarmUI
/// process's user can, which is why the whole code toolset is behind <c>llm_code_mode</c> and Exec-risk approval.</summary>
public sealed class CodeBashTool : CodeToolBase
{
    /// <summary>Default and longest time a command may run, and the most output kept from each of standard output and standard error.</summary>
    public const int DefaultSeconds = 120, MaxSeconds = 600, MaxOutputChars = 30_000;

    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.CodeBash;

    /// <inheritdoc/>
    public override ToolRisk Risk => ToolRisk.Exec;

    /// <inheritdoc/>
    public override TimeSpan TimeoutFor(JObject toolDef) => TimeSpan.FromSeconds(MaxSeconds + 10);

    /// <inheritdoc/>
    public override async Task<JObject> Execute(ToolExecutionContext ctx)
    {
        Workspace workspace = GetWorkspace(ctx, out JObject failure);
        if (workspace is null)
        {
            return failure;
        }
        return await Run(workspace, ctx.Args, ctx.Ct);
    }

    /// <summary>Does the work in <paramref name="workspace"/>; separate from <see cref="Execute"/> so it can be run without a session.</summary>
    internal static async Task<JObject> Run(Workspace workspace, JObject args, CancellationToken ct)
    {
        JObject failure;
        string command = args["command"]?.ToString();
        if (string.IsNullOrWhiteSpace(command))
        {
            return Fail("command is required.");
        }
        string folder = ResolvePath(workspace, args["workingDirectory"]?.ToString(), out failure);
        if (folder is null)
        {
            return failure;
        }
        if (!Directory.Exists(folder))
        {
            return Fail("The working directory does not exist.");
        }
        int seconds = Math.Clamp(args["timeoutSeconds"]?.Value<int?>() ?? DefaultSeconds, 1, MaxSeconds);
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        ProcessResult result = windows
            ? await ProcessRunner.RunAsync("cmd.exe", [], folder, null, TimeSpan.FromSeconds(seconds), MaxOutputChars, ct, rawArguments: $"/d /s /c \"{command}\"")
            : await ProcessRunner.RunAsync("/bin/sh", ["-c", command], folder, null, TimeSpan.FromSeconds(seconds), MaxOutputChars, ct);
        if (result.StartError is not null)
        {
            return Fail($"The command could not be started: {result.StartError}");
        }
        return new JObject
        {
            ["success"] = !result.TimedOut,
            ["exitCode"] = result.ExitCode,
            ["stdout"] = result.Stdout,
            ["stderr"] = result.Stderr,
            ["timedOut"] = result.TimedOut,
            ["truncated"] = result.Truncated,
            ["workingDirectory"] = PathSandbox.Relative(workspace.Root, folder),
            ["error"] = result.TimedOut ? $"The command was stopped after {seconds} seconds." : null,
        };
    }
}

/// <summary>code_todo: the chat's task list. The model replaces it as it plans and as it finishes steps, and the interface can show it.</summary>
public sealed class CodeTodoTool : CodeToolBase
{
    /// <summary>The statuses a task can have.</summary>
    public static readonly string[] Statuses = ["pending", "in_progress", "completed"];

    /// <summary>Most tasks, and the longest one.</summary>
    public const int MaxItems = 50, MaxChars = 300;

    /// <inheritdoc/>
    public override string HandlerId => ToolConstants.CodeTodo;

    /// <summary>Changing the list changes nothing outside the chat, and plan mode is exactly when it is wanted.</summary>
    public override ToolRisk Risk => ToolRisk.Read;

    /// <inheritdoc/>
    public override Task<JObject> Execute(ToolExecutionContext ctx)
    {
        User user = ctx.Session?.User;
        if (user is null || string.IsNullOrEmpty(ctx.ThreadId))
        {
            return Task.FromResult(Fail("The task list belongs to a chat."));
        }
        if (ctx.Args["todos"] is not JArray items)
        {
            return Task.FromResult(Fail("todos is required: the whole task list, each with content and status."));
        }
        if (items.Count > MaxItems)
        {
            return Task.FromResult(Fail($"Up to {MaxItems} tasks."));
        }
        JArray cleaned = [];
        foreach (JToken item in items)
        {
            string content = item["content"]?.ToString()?.Trim() ?? "";
            string status = (item["status"]?.ToString() ?? "pending").Trim().ToLowerInvariant();
            if (content.Length is 0 or > MaxChars || !Statuses.Contains(status))
            {
                return Task.FromResult(Fail($"Each task needs content (1 to {MaxChars} characters) and a status of {string.Join(", ", Statuses)}."));
            }
            cleaned.Add(new JObject { ["content"] = content, ["status"] = status });
        }
        if (Services.ThreadStorageService.SetTodos(user, ctx.ThreadId, cleaned) is null)
        {
            return Task.FromResult(Fail("The chat was not found."));
        }
        return Task.FromResult(new JObject { ["success"] = true, ["todos"] = cleaned });
    }
}
