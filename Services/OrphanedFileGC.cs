using System.IO;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using Hartsy.Extensions.LLMAssistant.Tools.BuiltIn;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services;

/// <summary>Background sweeper (started from <see cref="LLMAssistantExtension.OnInit"/>, runs
/// roughly once a day) that deletes files under each user's file_write sandbox, avatar, and
/// upload folders which are no longer referenced by any of that user's saved threads and are
/// older than the grace period. Only ever touches paths inside those known roots; every
/// deletion is logged.</summary>
public static class OrphanedFileGC
{
    /// <summary>Default sweep interval — overridable via the <see cref="IntervalSettingKey"/>
    /// admin flag (range clamped to <see cref="MinIntervalHours"/>–<see cref="MaxIntervalHours"/>).</summary>
    private static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromHours(24);

    /// <summary>Minimum age a file must reach before it's eligible for deletion. Avoids racing
    /// in-flight writes that haven't been associated with a thread yet.</summary>
    private static readonly TimeSpan FileGracePeriod = TimeSpan.FromHours(24);

    /// <summary>Initial delay before the first sweep, so startup logs aren't cluttered with GC.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    /// <summary>Hard floor / ceiling for the sweep interval setting. The floor stops a typo
    /// from burning CPU; the ceiling keeps the value reasonable.</summary>
    public const double MinIntervalHours = 1.0;
    public const double MaxIntervalHours = 24.0 * 7; // one week

    /// <summary>GenericData key on the shared user where the configured interval lives.</summary>
    public const string IntervalDataName = "llmassistant_gc";
    public const string IntervalSettingKey = "intervalHours";

    private static int Started;

    /// <summary>Reads the configured sweep interval. Falls back to the default on any error
    /// (parse failure, missing setting, etc.) and clamps to the allowed range.</summary>
    public static TimeSpan GetCurrentInterval()
    {
        try
        {
            string raw = Program.Sessions?.GenericSharedUser?.GetGenericData(IntervalDataName, IntervalSettingKey);
            if (string.IsNullOrEmpty(raw)) { return DefaultSweepInterval; }
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hours))
            {
                return DefaultSweepInterval;
            }
            hours = Math.Clamp(hours, MinIntervalHours, MaxIntervalHours);
            return TimeSpan.FromHours(hours);
        }
        catch
        {
            return DefaultSweepInterval;
        }
    }

    /// <summary>Sets the sweep interval. Admin-facing helper — clamps to the allowed range and
    /// persists. The next iteration of the sweep loop picks up the new value automatically; no
    /// restart needed.</summary>
    public static void SetInterval(double hours)
    {
        hours = Math.Clamp(hours, MinIntervalHours, MaxIntervalHours);
        try
        {
            Program.Sessions?.GenericSharedUser?.SaveGenericData(IntervalDataName, IntervalSettingKey,
                hours.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] GC interval write failed: {ex.Message}");
        }
    }

    /// <summary>Idempotent: starts the background sweeper once. Safe to call from extension OnInit.</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref Started, 1) != 0)
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
                    // Re-read the interval each iteration so admins can retune without restart.
                    // Add 0–25% jitter so multi-instance deployments don't sweep in lockstep.
                    TimeSpan interval = GetCurrentInterval();
                    double jitterMs = interval.TotalMilliseconds * (Random.Shared.NextDouble() * 0.25);
                    await Task.Delay(interval + TimeSpan.FromMilliseconds(jitterMs), Program.GlobalProgramCancel);
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
