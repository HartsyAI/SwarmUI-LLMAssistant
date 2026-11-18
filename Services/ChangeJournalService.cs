using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Remembers what the code tools changed in a chat's workspace, so the interface can show it as diffs and undo it.
/// <para>The first time a chat's tools change a file, its content from before is copied aside (or noted as "did not exist"). What the user sees as a change
/// is that saved baseline compared with the file as it is now, so several edits to one file are one change and a file put back as it was is no change at all.
/// Reverting puts the baseline back (or deletes a file the assistant created).</para>
/// <para>Only the file tools (<c>code_write</c>, <c>code_edit</c>) are journaled. A command run by <c>code_bash</c> can change anything, and is not tracked.</para>
/// <para>The journal of a chat lives beside its workspace data under <c>{output}/llm_assistant/changes/{threadId}/</c> and goes when the chat is deleted.</para></summary>
public static class ChangeJournalService
{
    /// <summary>Most files tracked per chat; a change to a file beyond that is still made, just not undoable.</summary>
    public const int MaxTrackedFiles = 300;

    /// <summary>The largest file whose previous content is kept (a larger one cannot be reverted), and the most kept per chat in all.</summary>
    public const long MaxBaselineBytes = 5L * 1024 * 1024, MaxTotalBytes = 100L * 1024 * 1024;

    /// <summary>The largest file a diff is drawn for.</summary>
    public const long MaxDiffBytes = 1L * 1024 * 1024;

    /// <summary>One lock for every journal: edits are infrequent and short, so a finer one would buy nothing.</summary>
    private static readonly object JournalLock = new();

    /// <summary>The name of the journal file inside a chat's folder.</summary>
    private const string JournalFile = "journal.json";

    /// <summary>Where a chat's journal is kept, or null when the id is not one that is safe as a folder name.</summary>
    public static string FolderFor(User user, string threadId)
    {
        if (user is null || string.IsNullOrEmpty(threadId) || !Regex.IsMatch(threadId, "^[A-Za-z0-9_-]{1,64}$"))
        {
            return null;
        }
        return Path.GetFullPath(Path.Combine(user.OutputDirectory, FileWriteTool.SandboxSubdir, "changes", threadId));
    }

    /// <summary>The recorder for a chat's workspace, or null when nothing can be recorded (no chat, so a call made outside one).</summary>
    public static ChangeRecorder RecorderFor(User user, string threadId, Workspace workspace)
        => FolderFor(user, threadId) is not { } folder || workspace is null ? null : new ChangeRecorder(folder, workspace);

    /// <summary>Removes a chat's journal and everything saved with it.</summary>
    public static void DeleteForThread(User user, string threadId)
    {
        string folder = FolderFor(user, threadId);
        if (folder is null)
        {
            return;
        }
        lock (JournalLock)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logs.Warning($"[LLMAssistant] Could not remove the change journal of chat {threadId}: {ex.Message}");
            }
        }
    }

    /// <summary>The key a path is kept under: the workspace-relative path, lowercased where the file system ignores case.</summary>
    internal static string KeyFor(string relative) => OperatingSystem.IsWindows() ? relative.ToLowerInvariant() : relative;

    /// <summary>The content hash of a file, as hex.</summary>
    internal static string HashFile(string fullPath)
    {
        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Reads a chat's journal for the workspace it is in. A journal made for another workspace is discarded: its paths mean something else now.</summary>
    private static JObject Load(string folder, Workspace workspace)
    {
        string path = Path.Combine(folder, JournalFile);
        if (File.Exists(path))
        {
            try
            {
                JObject journal = JObject.Parse(File.ReadAllText(path));
                if (journal["workspace"]?.ToString() == workspace.Id && journal["files"] is JObject)
                {
                    return journal;
                }
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
            {
                Logs.Warning($"[LLMAssistant] Change journal unreadable, starting a new one: {ex.Message}");
            }
        }
        return new JObject { ["workspace"] = workspace.Id, ["total"] = 0L, ["files"] = new JObject() };
    }

    /// <summary>Writes the journal back.</summary>
    private static void Save(string folder, JObject journal)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, JournalFile), journal.ToString(Newtonsoft.Json.Formatting.None));
    }

    /// <summary>Saves the state of a file before the tools first change it. Does nothing when the file is already tracked.</summary>
    internal static void RecordBefore(string folder, Workspace workspace, string fullPath)
    {
        lock (JournalLock)
        {
            JObject journal = Load(folder, workspace);
            JObject files = (JObject)journal["files"];
            string relative = PathSandbox.Relative(workspace.Root, fullPath);
            string key = KeyFor(relative);
            if (files.ContainsKey(key))
            {
                return;
            }
            JObject entry = new() { ["path"] = relative, ["existed"] = File.Exists(fullPath), ["baseline"] = null, ["baselineHash"] = null, ["lastHash"] = null };
            if (files.Count >= MaxTrackedFiles)
            {
                entry["untracked"] = true;
            }
            else if (File.Exists(fullPath))
            {
                long size = new FileInfo(fullPath).Length;
                long total = journal["total"]?.Value<long>() ?? 0;
                if (size > MaxBaselineBytes || total + size > MaxTotalBytes)
                {
                    entry["untracked"] = true;
                }
                else
                {
                    string blob = $"{files.Count + 1}-{Guid.NewGuid():N}.bin";
                    Directory.CreateDirectory(folder);
                    File.Copy(fullPath, Path.Combine(folder, blob), overwrite: true);
                    entry["baseline"] = blob;
                    entry["baselineHash"] = HashFile(fullPath);
                    journal["total"] = total + size;
                }
            }
            files[key] = entry;
            Save(folder, journal);
        }
    }

    /// <summary>Notes what a file holds right after the tools wrote it, so a later change by someone else can be told apart.</summary>
    internal static void RecordAfter(string folder, Workspace workspace, string fullPath)
    {
        lock (JournalLock)
        {
            JObject journal = Load(folder, workspace);
            if (((JObject)journal["files"])[KeyFor(PathSandbox.Relative(workspace.Root, fullPath))] is JObject entry && File.Exists(fullPath))
            {
                entry["lastHash"] = HashFile(fullPath);
                Save(folder, journal);
            }
        }
    }

    /// <summary>One file's change as it stands now.</summary>
    /// <param name="Path">The workspace-relative path.</param>
    /// <param name="Status"><c>created</c>, <c>modified</c> or <c>deleted</c>.</param>
    /// <param name="Added">Lines added, or null when no diff is drawn (binary or large).</param>
    /// <param name="Removed">Lines removed, or null.</param>
    /// <param name="Binary">Not text.</param>
    /// <param name="Large">Too large for a diff.</param>
    /// <param name="Revertable">The previous content was kept (or there was none to keep), so it can be put back.</param>
    /// <param name="Conflict">The file changed since the assistant last wrote it (by the user or by a command), so a revert would also undo that.</param>
    /// <param name="Diff">The unified diff, when asked for.</param>
    /// <param name="DiffTruncated">The diff was cut.</param>
    public sealed record FileChange(string Path, string Status, int? Added, int? Removed, bool Binary, bool Large, bool Revertable, bool Conflict, string Diff, bool DiffTruncated);

    /// <summary>The files the chat's tools changed that now differ from before, in path order. <paramref name="withDiff"/> adds each one's diff.</summary>
    public static List<FileChange> List(User user, string threadId, Workspace workspace, bool withDiff = false, string onlyPath = null)
        => FolderFor(user, threadId) is { } folder ? List(folder, workspace, withDiff, onlyPath) : [];

    /// <summary>The same, for the journal kept in <paramref name="folder"/>.</summary>
    internal static List<FileChange> List(string folder, Workspace workspace, bool withDiff = false, string onlyPath = null)
    {
        List<FileChange> changes = [];
        lock (JournalLock)
        {
            if (!File.Exists(Path.Combine(folder, JournalFile)))
            {
                return changes;
            }
            JObject journal = Load(folder, workspace);
            foreach (JProperty property in ((JObject)journal["files"]).Properties().OrderBy(p => p.Value["path"]?.ToString(), StringComparer.Ordinal))
            {
                JObject entry = (JObject)property.Value;
                string relative = entry["path"]!.ToString();
                if (onlyPath is not null && KeyFor(relative) != KeyFor(onlyPath))
                {
                    continue;
                }
                FileChange change = Describe(folder, workspace, entry, withDiff);
                if (change is not null)
                {
                    changes.Add(change);
                }
            }
        }
        return changes;
    }

    /// <summary>The text of a journal value, or null when it is missing or JSON null (which <c>ToString</c> would turn into an empty string).</summary>
    private static string Text(JToken token) => token is null || token.Type == JTokenType.Null ? null : token.ToString();

    /// <summary>One journal entry compared with the file now, or null when the file is as it was before the tools touched it.</summary>
    private static FileChange Describe(string folder, Workspace workspace, JObject entry, bool withDiff)
    {
        string relative = entry["path"]!.ToString();
        bool existed = entry["existed"]?.Value<bool>() ?? false;
        bool untracked = entry["untracked"]?.Value<bool>() ?? false;
        string full = Path.GetFullPath(Path.Combine(workspace.Root, relative));
        bool exists = File.Exists(full);
        string baselineName = Text(entry["baseline"]);
        bool revertable = !untracked && (!existed || baselineName is not null);
        if (!existed && !exists)
        {
            return null;
        }
        string currentHash = exists ? HashFile(full) : null;
        if (existed && exists && !untracked && currentHash == Text(entry["baselineHash"]))
        {
            return null;
        }
        string status = !existed ? "created" : !exists ? "deleted" : "modified";
        string lastHash = Text(entry["lastHash"]);
        // Changed since the assistant's last write: a user's edit, or a command. (A file the assistant never wrote has no hash to compare.)
        bool conflict = lastHash is not null && currentHash != lastHash;
        string baselinePath = baselineName is null ? null : Path.Combine(folder, baselineName);
        long currentSize = exists ? new FileInfo(full).Length : 0;
        long baselineSize = baselinePath is not null && File.Exists(baselinePath) ? new FileInfo(baselinePath).Length : 0;
        bool binary = (exists && LooksBinary(full)) || (baselinePath is not null && File.Exists(baselinePath) && LooksBinary(baselinePath));
        bool large = currentSize > MaxDiffBytes || baselineSize > MaxDiffBytes;
        if (binary || large || (untracked && existed))
        {
            return new FileChange(relative, status, null, null, binary, large, revertable, conflict, null, false);
        }
        string before = baselinePath is not null && File.Exists(baselinePath) ? File.ReadAllText(baselinePath) : "";
        string after = exists ? File.ReadAllText(full) : "";
        TextDiff.Result diff = TextDiff.Compare(before, after);
        return new FileChange(relative, status, diff.Added, diff.Removed, false, false, revertable, conflict, withDiff ? diff.Text : null, diff.Truncated);
    }

    /// <summary>Whether the start of a file holds a NUL byte.</summary>
    private static bool LooksBinary(string path)
    {
        byte[] head = new byte[8192];
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int read = stream.Read(head, 0, head.Length);
        return Array.IndexOf(head, (byte)0, 0, read) >= 0;
    }

    /// <summary>What a revert did.</summary>
    /// <param name="Reverted">Paths put back.</param>
    /// <param name="Conflicts">Paths left alone because they changed since the assistant wrote them and <c>force</c> was not set.</param>
    /// <param name="Failed">Paths that could not be put back, with why.</param>
    public sealed record RevertResult(List<string> Reverted, List<string> Conflicts, List<(string Path, string Error)> Failed);

    /// <summary>Puts a file back as it was before the chat's tools changed it, or every changed file when <paramref name="onlyPath"/> is null.
    /// A file that changed since the assistant last wrote it is left alone unless <paramref name="force"/> is set.</summary>
    public static RevertResult Revert(User user, string threadId, Workspace workspace, string onlyPath, bool force)
        => FolderFor(user, threadId) is { } folder ? Revert(folder, workspace, onlyPath, force) : new RevertResult([], [], []);

    /// <summary>The same, for the journal kept in <paramref name="folder"/>.</summary>
    internal static RevertResult Revert(string folder, Workspace workspace, string onlyPath, bool force)
    {
        RevertResult result = new([], [], []);
        lock (JournalLock)
        {
            if (!File.Exists(Path.Combine(folder, JournalFile)))
            {
                return result;
            }
            JObject journal = Load(folder, workspace);
            JObject files = (JObject)journal["files"];
            foreach (JProperty property in files.Properties().ToList())
            {
                JObject entry = (JObject)property.Value;
                string relative = entry["path"]!.ToString();
                if (onlyPath is not null && KeyFor(relative) != KeyFor(onlyPath))
                {
                    continue;
                }
                FileChange change = Describe(folder, workspace, entry, withDiff: false);
                if (change is null)
                {
                    continue;
                }
                if (!change.Revertable)
                {
                    result.Failed.Add((relative, "The earlier content of this file was too large to keep, so it cannot be put back."));
                    continue;
                }
                if (change.Conflict && !force)
                {
                    result.Conflicts.Add(relative);
                    continue;
                }
                try
                {
                    string baselineName = Text(entry["baseline"]);
                    long kept = baselineName is not null && File.Exists(Path.Combine(folder, baselineName)) ? new FileInfo(Path.Combine(folder, baselineName)).Length : 0;
                    PutBack(folder, workspace, entry);
                    journal["total"] = Math.Max(0, (journal["total"]?.Value<long>() ?? 0) - kept);
                    files.Remove(property.Name);
                    result.Reverted.Add(relative);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Failed.Add((relative, ex.Message));
                }
            }
            Save(folder, journal);
        }
        return result;
    }

    /// <summary>Restores one tracked file: the baseline's bytes back over it, or the file deleted when the assistant created it.</summary>
    private static void PutBack(string folder, Workspace workspace, JObject entry)
    {
        // The journal is the extension's own, but a path is only ever acted on after the same check every tool path goes through.
        if (!PathSandbox.TryResolve(workspace.Root, entry["path"]!.ToString(), out string full, out string error))
        {
            throw new IOException(error);
        }
        string baselineName = Text(entry["baseline"]);
        if (baselineName is null)
        {
            if (File.Exists(full))
            {
                File.Delete(full);
            }
            return;
        }
        string temp = Path.Combine(Path.GetDirectoryName(full)!, $".llma-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Copy(Path.Combine(folder, baselineName), temp, overwrite: true);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        File.Delete(Path.Combine(folder, baselineName));
    }
}

/// <summary>Records the file changes one tool call makes into a chat's journal. A journal that cannot be written never stops the change itself: it is logged and the
/// change is just not undoable.</summary>
public sealed class ChangeRecorder(string folder, Workspace workspace)
{
    /// <summary>Called right before the file at <paramref name="fullPath"/> is changed.</summary>
    public void Before(string fullPath)
    {
        Guard(() => ChangeJournalService.RecordBefore(folder, workspace, fullPath));
    }

    /// <summary>Called right after the file at <paramref name="fullPath"/> was changed.</summary>
    public void After(string fullPath)
    {
        Guard(() => ChangeJournalService.RecordAfter(folder, workspace, fullPath));
    }

    /// <summary>Runs a journal step, logging what goes wrong instead of letting it reach the tool.</summary>
    private static void Guard(Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            Logs.Warning($"[LLMAssistant] Could not record a file change: {ex.Message}");
        }
    }
}
