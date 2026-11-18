using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;

namespace Hartsy.Extensions.LLMAssistant.Services.Schedules;

/// <summary>Where schedules are kept: one JSON list per user in the user's own generic data (the way chats are), and a short list of which users have any, in the shared user's
/// data, so the scheduler can find them after a restart without anyone logging in. Every change goes through <see cref="Mutate"/> or <see cref="Save"/> under one lock, so a
/// run recording itself and a user editing the schedule at the same time cannot lose each other's changes.</summary>
public static class ScheduleStore
{
    /// <summary>The generic data name both lists are kept under.</summary>
    public const string DataName = "llmassistant_schedules";

    /// <summary>The key of a user's list of schedules.</summary>
    private const string ListKey = "list";

    /// <summary>The key, in the shared user's data, of the list of users that have schedules.</summary>
    private const string UsersKey = "users";

    /// <summary>Serializes every change.</summary>
    private static readonly object WriteLock = new();

    /// <summary>A user's schedules, oldest first.</summary>
    public static JArray List(User user)
    {
        string raw = user?.GetGenericData(DataName, ListKey);
        if (string.IsNullOrEmpty(raw))
        {
            return [];
        }
        try
        {
            return JArray.Parse(raw);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>One schedule, or null.</summary>
    public static JObject Get(User user, string id)
        => string.IsNullOrEmpty(id) ? null : List(user).OfType<JObject>().FirstOrDefault(s => s[ScheduleFields.Id]?.ToString() == id);

    /// <summary>Adds a schedule, or replaces the one with its id. False when it would be the user's 21st.</summary>
    public static bool Save(User user, JObject schedule)
    {
        lock (WriteLock)
        {
            JArray list = List(user);
            string id = schedule[ScheduleFields.Id].ToString();
            JObject existing = list.OfType<JObject>().FirstOrDefault(s => s[ScheduleFields.Id]?.ToString() == id);
            if (existing is null && list.Count >= ScheduleLimits.MaxPerUser)
            {
                return false;
            }
            if (existing is null)
            {
                list.Add(schedule);
            }
            else
            {
                existing.Replace(schedule);
            }
            Write(user, list);
            return true;
        }
    }

    /// <summary>Changes one schedule under the lock: <paramref name="change"/> edits it and says whether to save. Returns the schedule as it now is, or null when there is none.</summary>
    public static JObject Mutate(User user, string id, Func<JObject, bool> change)
    {
        lock (WriteLock)
        {
            JArray list = List(user);
            JObject schedule = list.OfType<JObject>().FirstOrDefault(s => s[ScheduleFields.Id]?.ToString() == id);
            if (schedule is null)
            {
                return null;
            }
            if (change(schedule))
            {
                Write(user, list);
            }
            return schedule;
        }
    }

    /// <summary>Removes a schedule. False when there was none.</summary>
    public static bool Delete(User user, string id)
    {
        lock (WriteLock)
        {
            JArray list = List(user);
            JToken schedule = list.FirstOrDefault(s => s[ScheduleFields.Id]?.ToString() == id);
            if (schedule is null)
            {
                return false;
            }
            schedule.Remove();
            Write(user, list);
            return true;
        }
    }

    /// <summary>Saves a user's list and keeps the list of users in step with it.</summary>
    private static void Write(User user, JArray list)
    {
        user.SaveGenericData(DataName, ListKey, list.ToString(Formatting.None));
        SetHasSchedules(user.UserID, list.Count > 0);
    }

    /// <summary>The ids of the users that have schedules.</summary>
    public static List<string> UserIds()
    {
        lock (WriteLock)
        {
            return ReadUsers();
        }
    }

    /// <summary>Forgets a user that no longer exists (or has none), so the scheduler does not look for it again.</summary>
    public static void Forget(string userId)
    {
        lock (WriteLock)
        {
            SetHasSchedules(userId, false);
        }
    }

    /// <summary>The list of users, from the shared user's data. Call with the lock held.</summary>
    private static List<string> ReadUsers()
    {
        string raw = Program.Sessions?.GenericSharedUser?.GetGenericData(DataName, UsersKey);
        try
        {
            return string.IsNullOrEmpty(raw) ? [] : [.. JArray.Parse(raw).Select(t => t.ToString())];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Adds a user to the list of users with schedules, or removes it. Call with the lock held.</summary>
    private static void SetHasSchedules(string userId, bool has)
    {
        if (Program.Sessions?.GenericSharedUser is not User shared)
        {
            return;
        }
        List<string> users = ReadUsers();
        if (has == users.Contains(userId))
        {
            return;
        }
        if (has)
        {
            users.Add(userId);
        }
        else
        {
            users.Remove(userId);
        }
        shared.SaveGenericData(DataName, UsersKey, new JArray(users).ToString(Formatting.None));
    }
}
