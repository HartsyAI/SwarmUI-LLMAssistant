using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.LLMAssistant.Services.Onboarding;

/// <summary>Where one user's onboarding state is kept.</summary>
public interface IOnboardingStorage
{
    /// <summary>The saved text, or null when nothing was saved.</summary>
    string Read();

    /// <summary>Saves the text.</summary>
    void Write(string value);
}

/// <summary>A user's onboarding state in their own generic data. It is a key of its own, not part of the settings layer, so a settings reset does not bring the first-run guide back and no settings
/// route can read or write it.</summary>
public sealed class UserOnboardingStorage(User user) : IOnboardingStorage
{
    /// <summary>The key under the extension's data name.</summary>
    public const string Key = "onboarding";

    /// <inheritdoc/>
    public string Read() => GenericDataCache.Get(user, SettingsService.DataName, Key);

    /// <inheritdoc/>
    public void Write(string value) => GenericDataCache.Save(user, SettingsService.DataName, Key, value);
}

/// <summary>Whether the first-run guide has been seen. A user starts as <see cref="New"/>; the guide shows to a new user who has no chats yet, and to one who asked to run it again
/// (<see cref="Pending"/>), until they skip or finish it, or send a message. A new user who is seen with chats is recorded as <see cref="Completed"/>, so deleting every chat later does not
/// start a first run. A reset makes the user new again.</summary>
public static class OnboardingState
{
    /// <summary>The statuses.</summary>
    public const string New = "new", Pending = "pending", Skipped = "skipped", Completed = "completed";

    /// <summary>What a caller may do to the state: leave the guide, finish it, run it again (it shows even to someone with chats), or reset it to how a new user has it.</summary>
    public const string ActionSkip = "skip", ActionComplete = "complete", ActionRun = "run", ActionReset = "reset";

    /// <summary>Held while the state is read and written.</summary>
    private static readonly object Lock = new();

    /// <summary>Held for the whole of <see cref="DuringInstall"/>. Not <see cref="Lock"/>, which a message being sent takes: it does not wait for an install.</summary>
    private static readonly object InstallRuns = new();

    /// <summary>The saved status; <see cref="New"/> when nothing was saved or what was saved cannot be read.</summary>
    public static string Load(IOnboardingStorage storage)
    {
        string raw = storage.Read();
        if (string.IsNullOrEmpty(raw))
        {
            return New;
        }
        try
        {
            string status = JObject.Parse(raw)["status"]?.ToString();
            return status is Pending or Skipped or Completed ? status : New;
        }
        catch (JsonException)
        {
            return New;
        }
    }

    /// <summary>Whether the guide should open by itself.</summary>
    public static bool ShouldShow(string status, bool hasChats) => status == Pending || (status == New && !hasChats);

    /// <summary>Does <paramref name="action"/> and gives the status afterwards. Doing the same one twice changes nothing. False (with <paramref name="error"/>) for an unknown action.</summary>
    public static bool Apply(IOnboardingStorage storage, string action, out string status, out string error)
    {
        error = null;
        string wanted = action switch { ActionSkip => Skipped, ActionComplete => Completed, ActionRun => Pending, ActionReset => New, _ => null };
        lock (Lock)
        {
            status = Load(storage);
            if (wanted is null)
            {
                error = $"action must be {ActionSkip}, {ActionComplete}, {ActionRun} or {ActionReset}.";
                return false;
            }
            if (status != wanted)
            {
                Save(storage, wanted);
                status = wanted;
            }
        }
        return true;
    }

    /// <summary>The status now that it is known whether the user has chats: a new user who has some is past their first run, and is saved as <see cref="Completed"/>.</summary>
    public static string Observe(IOnboardingStorage storage, bool hasChats)
    {
        lock (Lock)
        {
            string status = Load(storage);
            if (status == New && hasChats)
            {
                Save(storage, Completed);
                return Completed;
            }
            return status;
        }
    }

    /// <summary>The user sent a message: a guide that is new or running is finished. One they skipped stays skipped.</summary>
    public static void OnMessage(IOnboardingStorage storage)
    {
        lock (Lock)
        {
            if (Load(storage) is New or Pending)
            {
                Save(storage, Completed);
            }
        }
    }

    /// <summary>Runs <paramref name="install"/> (true when it added something), which may make chats. A new user with no chats is kept in the guide, which the chats would otherwise close;
    /// when nothing was added, they are left new, as they were.</summary>
    public static void DuringInstall(IOnboardingStorage storage, bool hadChats, Func<bool> install)
    {
        // One at a time: an install that found nothing left to add, because another was adding it, would put the user back to new while their chats were arriving.
        lock (InstallRuns)
        {
            bool opened = false;
            lock (Lock)
            {
                if (Load(storage) == New && !hadChats)
                {
                    Save(storage, Pending);
                    opened = true;
                }
            }
            bool added = false;
            try
            {
                added = install();
            }
            finally
            {
                if (opened && !added)
                {
                    lock (Lock)
                    {
                        if (Load(storage) == Pending)
                        {
                            Save(storage, New);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Writes the status.</summary>
    private static void Save(IOnboardingStorage storage, string status)
        => storage.Write(new JObject { ["version"] = 1, ["status"] = status, ["updatedAt"] = DateTime.UtcNow.ToString("o") }.ToString(Formatting.None));
}
