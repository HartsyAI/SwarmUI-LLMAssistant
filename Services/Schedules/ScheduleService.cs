using System.Collections.Concurrent;
using Hartsy.Extensions.LLMAssistant.LLMs;
using Hartsy.Extensions.LLMAssistant.Services.Sessions;
using Hartsy.Extensions.LLMAssistant.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.LLMAssistant.Services.Schedules;

/// <summary>What happened when a schedule was asked to run: it started a run (with the chat of that run and the status it is in), or it did not, and why.</summary>
public sealed record ScheduleRunResult(bool Started, string ThreadId, string Status, string Reason);

/// <summary>Runs schedules on time. A background loop (started from <c>OnInit</c>, like the file sweeper) looks every few seconds at the users that have schedules and starts
/// a run for each one that is due. A run is a <b>session</b> (see <see cref="SessionManager"/>) under a chat of the schedule's own, so everything a session has comes with it:
/// the per-user limit and queue, approvals that wait for the user, a report, the status dots in the chat list, and a Stop button. The schedule's chat lists its runs; only the
/// last few are kept.
/// <list type="bullet">
/// <item><b>Moving on first.</b> The next time is saved before the run starts, so a run that takes the server down is not started again; and after downtime a schedule runs
/// once for the time it missed (if within <see cref="ScheduleLimits.CatchUpWindow"/>), never once for each.</item>
/// <item><b>No overlap.</b> A time that comes while the previous run is still working is skipped, and the schedule says so.</item>
/// <item><b>Unattended.</b> A run can use only the tools saved on the schedule, never the schedule tools, with the approval mode saved on it; a call that needs the user waits
/// (without holding one of the user's places) and is refused after the usual wait.</item>
/// </list></summary>
public static class ScheduleService
{
    /// <summary>Set once the loop has been started.</summary>
    private static int _started;

    /// <summary>Set while a pass is running, so a slow one is not overlapped by the next.</summary>
    private static int _ticking;

    /// <summary>The session each user's runs are made under, kept so every run does not open (and leak) another.</summary>
    private static readonly ConcurrentDictionary<string, Session> RunSessions = new();

    /// <summary>Whether the loop has been started.</summary>
    public static bool IsRunning => Volatile.Read(ref _started) != 0;

    /// <summary>Starts the loop once. Safe to call from the extension's <c>OnInit</c>.</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }
        Utilities.RunCheckedTask(async () =>
        {
            while (!Program.GlobalProgramCancel.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(ScheduleLimits.TickSeconds), Program.GlobalProgramCancel);
                    await TickAsync(DateTime.UtcNow);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Logs.Error($"[LLMAssistant] The scheduler's pass failed: {ex.Message}");
                }
            }
        }, "LLMAssistant.Scheduler");
    }

    /// <summary>One pass over the users that have schedules. A failure for one user does not stop the others, and a pass that finds the last one still running does nothing.</summary>
    public static async Task TickAsync(DateTime nowUtc)
    {
        if (Interlocked.Exchange(ref _ticking, 1) != 0)
        {
            return;
        }
        try
        {
            foreach (string userId in ScheduleStore.UserIds())
            {
                try
                {
                    // makeNew is false: a user that was deleted must not be created again by looking for it.
                    User user = Program.Sessions.GetUser(userId, makeNew: false);
                    if (user is null)
                    {
                        ScheduleStore.Forget(userId);
                        continue;
                    }
                    await TickUserAsync(user, nowUtc);
                }
                catch (Exception ex)
                {
                    Logs.Error($"[LLMAssistant] The scheduler failed for user {userId}: {ex.Message}");
                }
            }
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>Starts a run for each of a user's schedules that is due at <paramref name="nowUtc"/>. Returns how many it started.</summary>
    public static async Task<int> TickUserAsync(User user, DateTime nowUtc)
    {
        int started = 0;
        foreach (JObject schedule in ScheduleStore.List(user).OfType<JObject>().ToList())
        {
            ScheduleDecision decision = ScheduleRules.Decide(schedule, nowUtc);
            if (decision == ScheduleDecision.NotDue)
            {
                continue;
            }
            string id = schedule[ScheduleFields.Id].ToString();
            // Move on before running: the next time is saved first, so this one is not repeated whatever happens to the run.
            ScheduleStore.Mutate(user, id, s =>
            {
                ScheduleRules.Advance(s, nowUtc);
                return true;
            });
            if (decision == ScheduleDecision.Missed)
            {
                RecordSkip(user, id, "It was due while the server was down or busy, longer ago than a missed run is made up.", nowUtc);
                continue;
            }
            if ((await RunAsync(user, id, nowUtc)).Started)
            {
                started++;
            }
        }
        return started;
    }

    /// <summary>Starts a run of a schedule now, whatever the time (the Run now button). It does not change when the schedule runs next.</summary>
    public static Task<ScheduleRunResult> RunNowAsync(User user, string scheduleId) => RunAsync(user, scheduleId, DateTime.UtcNow);

    /// <summary>Starts a run: makes the schedule's chat if it has none, makes room among its runs, and starts a session there with the schedule's prompt. Says why when it does not.</summary>
    private static async Task<ScheduleRunResult> RunAsync(User user, string scheduleId, DateTime nowUtc)
    {
        JObject schedule = ScheduleStore.Get(user, scheduleId);
        if (schedule is null)
        {
            return new ScheduleRunResult(false, null, null, "There is no such schedule.");
        }
        if (!user.HasPermission(LLMAssistantAPI.PermSchedules))
        {
            return Skipped(user, scheduleId, "The user no longer has permission to run schedules.", nowUtc);
        }
        // What the server forgot at a restart is marked first, so a run that died with it does not look like one that is still working.
        string userId = user.UserID;
        SessionManager.Reconcile(user, id => BackgroundTurnService.IsLive(userId, id) || GenerationCancellationRegistry.IsActive(id));
        string last = schedule[ScheduleFields.LastThreadId]?.ToString();
        if (!string.IsNullOrEmpty(last) && (BackgroundTurnService.IsLive(userId, last) || GenerationCancellationRegistry.IsActive(last)))
        {
            return Skipped(user, scheduleId, "The previous run is still working.", nowUtc);
        }
        JObject settings = SettingsService.GetMergedSettings(user);
        string model = schedule[ScheduleFields.Model]?.ToString();
        if (string.IsNullOrWhiteSpace(model))
        {
            model = settings["preferredModel"]?.ToString();
        }
        Session session = SessionFor(user);
        if (session is null)
        {
            return Skipped(user, scheduleId, "Could not open a session to run in.", nowUtc);
        }
        if (string.IsNullOrWhiteSpace(model) || await LLMModelLookup.GetByIdAsync(model, session) is null)
        {
            return Skipped(user, scheduleId, string.IsNullOrWhiteSpace(model) ? "No model is set for this schedule." : $"The model '{model}' is not available.", nowUtc);
        }
        SessionLimits limits = SessionLimits.From(settings);
        JObject host = EnsureHost(user, schedule, settings);
        string hostId = host["id"].ToString();
        int keep = Math.Min(ScheduleLimits.KeepRuns, limits.MaxChildrenPerParent);
        if (!MakeRoom(user, hostId, keep))
        {
            return Skipped(user, scheduleId, "Too many of its runs are still working.", nowUtc);
        }
        // The saved list is a ceiling, and the host chat's assistant is another: a run gets what is in both.
        List<string> tools = [.. schedule[ScheduleFields.ToolIds] is JArray saved ? saved.Select(t => t.ToString()) : []];
        (JObject run, string error) = SessionManager.CreateChild(user, host, model, new SessionManager.SpawnRequest(ScheduleRules.RunTitle(schedule[ScheduleFields.Title].ToString(), nowUtc), null, model,
            schedule[ScheduleFields.ApprovalMode]?.ToString(), tools), limits, settings);
        if (run is null)
        {
            return Skipped(user, scheduleId, error, nowUtc);
        }
        string runId = run["id"].ToString();
        string status = BackgroundTurnService.Start(session, runId, schedule[ScheduleFields.Prompt].ToString(), limits) ?? SessionStatuses.Running;
        ScheduleStore.Mutate(user, scheduleId, s =>
        {
            s[ScheduleFields.LastRunAt] = nowUtc.ToString("o");
            s[ScheduleFields.LastThreadId] = runId;
            s[ScheduleFields.RunCount] = (s[ScheduleFields.RunCount]?.Value<int>() ?? 0) + 1;
            s.Remove(ScheduleFields.LastSkipped);
            s.Remove(ScheduleFields.LastSkippedAt);
            return true;
        });
        return new ScheduleRunResult(true, runId, status, null);
    }

    /// <summary>The chat a schedule's runs are listed under, made the first time it is needed (and again if the user deleted it). Its approval mode follows the schedule's.</summary>
    private static JObject EnsureHost(User user, JObject schedule, JObject settings)
    {
        string scheduleId = schedule[ScheduleFields.Id].ToString();
        JObject host = ThreadStorageService.GetThread(user, schedule[ScheduleFields.HostThreadId]?.ToString());
        string mode = ApprovalPolicy.Normalize(schedule[ScheduleFields.ApprovalMode]?.ToString());
        if (host is null)
        {
            string assistant = schedule[ScheduleFields.AssistantId]?.ToString();
            if (string.IsNullOrWhiteSpace(assistant))
            {
                assistant = AssistantService.GetActiveAssistantId(settings, user);
            }
            host = ThreadStorageService.CreateThread(user, assistant, $"Schedule: {schedule[ScheduleFields.Title]}");
            string hostId = host["id"].ToString();
            ScheduleStore.Mutate(user, scheduleId, s =>
            {
                s[ScheduleFields.HostThreadId] = hostId;
                return true;
            });
        }
        // Everything the host is lives on the chat itself; the title is the schedule's, so a generated one would not replace it.
        return ThreadStorageService.Mutate(user, host["id"].ToString(), thread =>
        {
            thread[ScheduleFields.HostMarker] = scheduleId;
            thread["approvalMode"] = mode;
            ThreadStorageService.MarkTitleClaimed(thread);
            return true;
        });
    }

    /// <summary>Deletes the oldest finished runs so that fewer than <paramref name="keep"/> remain, ready for another. False when that many are still working.</summary>
    private static bool MakeRoom(User user, string hostId, int keep)
    {
        List<JObject> runs = [.. ThreadStorageService.GetThreadIndex(user).OfType<JObject>()
            .Where(t => t[SessionFields.Parent]?.ToString() == hostId)
            .OrderBy(t => t["createdAt"]?.ToString(), StringComparer.Ordinal)];
        List<JObject> finished = [.. runs.Where(t => !SessionStatuses.IsActive(t[SessionFields.Status]?.ToString()))];
        if (runs.Count - finished.Count >= keep)
        {
            return false;
        }
        for (int i = 0; runs.Count - i >= keep && i < finished.Count; i++)
        {
            string id = finished[i]["id"].ToString();
            ToolApprovals.Pending.CancelScope(user.UserID, id);
            ToolApprovals.Grants.Clear(user.UserID, id);
            ThreadStorageService.DeleteThread(user, id);
        }
        return true;
    }

    /// <summary>The session a user's scheduled runs are made under: kept, and replaced only when it has gone. Null when one cannot be opened.</summary>
    private static Session SessionFor(User user)
    {
        if (RunSessions.TryGetValue(user.UserID, out Session existing) && Program.Sessions.Sessions.ContainsKey(existing.ID))
        {
            return existing;
        }
        try
        {
            Session session = Program.Sessions.CreateSession("llmassistant-schedule", user.UserID, persist: false);
            RunSessions[user.UserID] = session;
            return session;
        }
        catch (Exception ex)
        {
            Logs.Warning($"[LLMAssistant] Could not open a session for {user.UserID}'s scheduled runs: {ex.Message}");
            return null;
        }
    }

    /// <summary>Records that a due time did not start a run, and why.</summary>
    private static ScheduleRunResult Skipped(User user, string scheduleId, string reason, DateTime nowUtc)
    {
        RecordSkip(user, scheduleId, reason, nowUtc);
        return new ScheduleRunResult(false, null, null, reason);
    }

    /// <summary>Saves why the schedule did not run, so its list can say so.</summary>
    private static void RecordSkip(User user, string scheduleId, string reason, DateTime nowUtc)
        => ScheduleStore.Mutate(user, scheduleId, s =>
        {
            s[ScheduleFields.LastSkipped] = reason;
            s[ScheduleFields.LastSkippedAt] = nowUtc.ToString("o");
            return true;
        });
}
