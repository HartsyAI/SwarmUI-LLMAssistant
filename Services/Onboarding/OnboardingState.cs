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
/// (<see cref="Pending"/>), until they skip or finish it. A reset makes the user new again.</summary>
public static class OnboardingState
{
    /// <summary>The statuses.</summary>
    public const string New = "new", Pending = "pending", Skipped = "skipped", Completed = "completed";

    /// <summary>What a caller may do to the state: leave the guide, finish it, run it again (it shows even to someone with chats), or reset it to how a new user has it.</summary>
    public const string ActionSkip = "skip", ActionComplete = "complete", ActionRun = "run", ActionReset = "reset";

    /// <summary>Held while the state is read and written.</summary>
    private static readonly object Lock = new();

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

    /// <summary>Called before something that makes chats for a user in their first run: a new user with no chats is kept in the guide, which the chats would otherwise close.</summary>
    public static void KeepOpenIfFirstRun(IOnboardingStorage storage, bool hasChats)
    {
        lock (Lock)
        {
            if (Load(storage) == New && !hasChats)
            {
                Save(storage, Pending);
            }
        }
    }

    /// <summary>Writes the status.</summary>
    private static void Save(IOnboardingStorage storage, string status)
        => storage.Write(new JObject { ["version"] = 1, ["status"] = status, ["updatedAt"] = DateTime.UtcNow.ToString("o") }.ToString(Formatting.None));
}
