using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Extensions.LLMAssistant.Tools.BuiltIn;
using SwarmUI.Utils;

namespace SwarmUI.Extensions.LLMAssistant.Services;

/// <summary>Background sweeper for files written by the file_write tool that are no longer
/// referenced by any of their owner's saved threads.
///
/// <para><b>What it does:</b> once per day, for every user with a per-user llm_assistant
/// sandbox folder, walks the folder, builds the set of file paths still referenced in the
/// user's saved threads (via <c>tool_result</c> entries on assistant messages with name
/// <c>file_write</c>), and deletes any file in the folder that's both (a) not in that set
/// and (b) older than the grace window. The grace window prevents racing in-flight writes
/// that haven't been added to a thread yet (eg user closed the tab mid-stream).</para>
///
/// <para><b>Started from</b> <see cref="LLMAssistantExtension.OnInit"/> via
/// <see cref="Utilities.RunCheckedTask"/>; cooperative shutdown via
/// <see cref="Program.GlobalProgramCancel"/>.</para>
///
/// <para><b>Safety:</b> only operates on files inside <see cref="FileWriteTool.GetSandboxRoot"/>
/// for each user — never touches anything outside that path. Logs every deletion.</para></summary>
public static class OrphanedFileGC
{
    /// <summary>How often the sweep runs.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    /// <summary>Minimum age a file must reach before it's eligible for deletion. Avoids racing
    /// in-flight writes that haven't been associated with a thread yet.</summary>
    private static readonly TimeSpan FileGracePeriod = TimeSpan.FromHours(24);

    /// <summary>Initial delay before the first sweep, so startup logs aren't cluttered with GC.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    private static int _started;

    /// <summary>Idempotent: starts the background sweeper once. Safe to call from extension OnInit.</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }
        Utilities.RunCheckedTask(async () =>
        {
            try
            {
                await Task.Delay(StartupDelay, Program.GlobalProgramCancel);
            }
            catch (OperationCanceledException) { return; }
            while (!Program.GlobalProgramCancel.IsCancellationRequested)
            {
                try
                {
                    SweepAllUsers();
                }
                catch (Exception ex)
                {
                    Logs.Error($"[LLMAssistant] OrphanedFileGC sweep failed: {ex.Message}");
                }
                try
                {
                    await Task.Delay(SweepInterval, Program.GlobalProgramCancel);
                }
                catch (OperationCanceledException) { return; }
            }
        }, "LLMAssistant.OrphanedFileGC");
    }

    /// <summary>Walks every loaded user and runs <see cref="SweepUser"/>. Users not currently in
    /// memory are not swept this cycle — they'll be picked up when they next log in (which loads
    /// the User into <c>Program.Sessions.Users</c>).</summary>
    private static void SweepAllUsers()
    {
        // Snapshot to avoid mutating-during-enumeration if a user logs in mid-sweep.
        User[] users = [.. Program.Sessions.Users.Values];
        int totalDeleted = 0;
        foreach (User user in users)
        {
            try
            {
                totalDeleted += SweepUser(user);
            }
            catch (Exception ex)
            {
                Logs.Error($"[LLMAssistant] OrphanedFileGC failed for user {user.UserID}: {ex.Message}");
            }
            if (Program.GlobalProgramCancel.IsCancellationRequested)
            {
                return;
            }
        }
        if (totalDeleted > 0)
        {
            Logs.Info($"[LLMAssistant] OrphanedFileGC: deleted {totalDeleted} orphaned file(s) across {users.Length} user(s).");
        }
    }

    /// <summary>Sweeps one user. Returns the number of files deleted across both the file_write
    /// sandbox and the assistant-avatars folder.</summary>
    private static int SweepUser(User user)
    {
        int deleted = 0;
        HashSet<string> referenced = CollectReferencedPaths(user);
        DateTime cutoff = DateTime.UtcNow - FileGracePeriod;
        // file_write sandbox
        string fileWriteRoot = FileWriteTool.GetSandboxRoot(user);
        deleted += SweepRoot(user, fileWriteRoot, referenced, cutoff);
        // Assistant avatars
        string avatarRoot = Path.GetFullPath(Path.Combine(user.OutputDirectory, WebAPI.AssistantEndpoints.AvatarSubdir));
        deleted += SweepRoot(user, avatarRoot, referenced, cutoff);
        // Chat-attached image uploads (per-thread subfolders).
        string uploadsRoot = MediaStorageService.GetAllUploadsRoot(user);
        deleted += SweepRoot(user, uploadsRoot, referenced, cutoff);
        return deleted;
    }

    /// <summary>Sweeps one root directory. Files older than the cutoff that aren't in the
    /// referenced set are deleted; empty subdirs are pruned afterward.</summary>
    private static int SweepRoot(User user, string root, HashSet<string> referenced, DateTime cutoff)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return 0;
        }
        int deleted = 0;
        foreach (string filePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            FileInfo info;
            try { info = new FileInfo(filePath); }
            catch { continue; }
            if (info.LastWriteTimeUtc > cutoff)
            {
                continue;
            }
            string normalized = Path.GetFullPath(filePath).Replace('\\', '/');
            if (referenced.Contains(normalized))
            {
                continue;
            }
            try
            {
                File.Delete(filePath);
                deleted++;
                Logs.Info($"[LLMAssistant] OrphanedFileGC deleted: {filePath} (user={user.UserID})");
            }
            catch (Exception ex)
            {
                Logs.Warning($"[LLMAssistant] OrphanedFileGC could not delete {filePath}: {ex.Message}");
            }
        }
        foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch { /* ignore */ }
        }
        return deleted;
    }

    /// <summary>Builds the set of full file paths still referenced by either:
    /// <list type="bullet">
    /// <item>any <c>file_write</c> tool_result on any of the user's saved threads (using
    /// the result's <c>fullPath</c>, with <c>url</c> as fallback for older entries), OR</item>
    /// <item>any <c>avatar</c> URL on any of the user's assistants (per-user and shared), OR</item>
    /// <item>any <c>media[].url</c> on any user message in any of the user's saved threads
    /// (chat-attached image uploads).</item>
    /// </list>
    /// Anything else in the swept folders is fair game for deletion.</summary>
    private static HashSet<string> CollectReferencedPaths(User user)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        string outputRoot = Path.GetFullPath(Program.ServerSettings.Paths.OutputPath);
        // Avatar refs from every assistant the user can see (shared + personal). A shared
        // assistant's avatar shouldn't be deleted just because nobody chats with it.
        JObject settings = SettingsService.GetMergedSettings(user);
        if (settings["assistants"] is JObject assistants)
        {
            foreach (KeyValuePair<string, JToken> kv in assistants)
            {
                string avatar = (kv.Value as JObject)?["avatar"]?.ToString();
                if (!string.IsNullOrEmpty(avatar) && avatar.StartsWith("Output/", StringComparison.OrdinalIgnoreCase))
                {
                    string rel = avatar["Output/".Length..];
                    result.Add(Path.GetFullPath(Path.Combine(outputRoot, rel)).Replace('\\', '/'));
                }
            }
        }
        JArray index = ThreadStorageService.GetThreadIndex(user);
        foreach (JToken summary in index)
        {
            string threadId = summary["id"]?.ToString();
            if (string.IsNullOrEmpty(threadId))
            {
                continue;
            }
            JObject thread = ThreadStorageService.GetThread(user, threadId);
            if (thread?["messages"] is not JArray messages)
            {
                continue;
            }
            foreach (JToken msg in messages)
            {
                // Chat-attached uploads — referenced via msg.media[].url.
                if (msg?["media"] is JArray mediaArr)
                {
                    foreach (JToken m in mediaArr)
                    {
                        string url = m?["url"]?.ToString();
                        if (!string.IsNullOrEmpty(url) && url.StartsWith("Output/", StringComparison.OrdinalIgnoreCase))
                        {
                            string rel = url["Output/".Length..];
                            result.Add(Path.GetFullPath(Path.Combine(outputRoot, rel)).Replace('\\', '/'));
                        }
                    }
                }
                if (msg?["toolCalls"] is not JArray toolCalls)
                {
                    continue;
                }
                foreach (JToken tc in toolCalls)
                {
                    if (tc?["name"]?.ToString() != ToolConstants.FileWrite)
                    {
                        continue;
                    }
                    JToken res = tc?["result"];
                    if (res is null) continue;
                    string full = res["fullPath"]?.ToString();
                    if (!string.IsNullOrEmpty(full))
                    {
                        result.Add(Path.GetFullPath(full).Replace('\\', '/'));
                        continue;
                    }
                    // Fallback for older entries that only have url (eg "Output/{userId}/llm_assistant/foo.md")
                    string url = res["url"]?.ToString();
                    if (!string.IsNullOrEmpty(url) && url.StartsWith("Output/", StringComparison.OrdinalIgnoreCase))
                    {
                        string rel = url["Output/".Length..];
                        result.Add(Path.GetFullPath(Path.Combine(outputRoot, rel)).Replace('\\', '/'));
                    }
                }
            }
        }
        return result;
    }
}
