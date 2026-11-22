using Newtonsoft.Json.Linq;

namespace Hartsy.Extensions.LLMAssistant.Services.Memory;

/// <summary>The privacy controls over memory, read from settings: the user's own pause switches and retention, and the admin's switch that turns the automatic extractor off for everyone.
/// <para>The pause switches and retention are the user's own layer only (never the shared one, like <see cref="MemoryExtractionSettings"/>). The admin switch is the shared layer only (never
/// the user's, which a user could otherwise shadow it with). All of it is decided by <see cref="From"/> over two settings objects, so it is tested without a running server.</para></summary>
/// <param name="SavingPaused">Nothing is saved by the model or by the automatic extractor while on (the user's own edits in Settings are never paused).</param>
/// <param name="RecallPaused">No note reaches a model while on: not the prompt block, not the name, not <c>memory_read</c>.</param>
/// <param name="RetentionDays">Notes not used for this many days are forgotten (pinned and slot notes never are); 0 means never.</param>
/// <param name="AdminExtractionDisabled">An admin switched the automatic extractor off for every user, and a user cannot switch it back on.</param>
public sealed record MemoryControls(bool SavingPaused, bool RecallPaused, int RetentionDays, bool AdminExtractionDisabled)
{
    /// <summary>The user's pause block: <c>{ saving, recall }</c>.</summary>
    public const string PauseKey = "memoryPause";

    /// <summary>The user's retention block: <c>{ unusedDays }</c>.</summary>
    public const string RetentionKey = "memoryRetention";

    /// <summary>The shared layer's block: <c>{ extractionDisabled }</c>.</summary>
    public const string AdminKey = "memoryAdmin";

    /// <summary>The most days retention may be set to (ten years).</summary>
    public const int MaxRetentionDays = 3650;

    /// <summary>The fewest days a set retention may be (a shorter one would forget notes before a user could notice them).</summary>
    public const int MinRetentionDays = 7;

    /// <summary>Nothing paused, no retention, no admin switch.</summary>
    public static MemoryControls Default => new(false, false, 0, false);

    /// <summary>Reads the controls: the pause and retention from <paramref name="userLayer"/>, the admin switch from <paramref name="sharedLayer"/>. Anything of the wrong type is the default.</summary>
    public static MemoryControls From(JObject userLayer, JObject sharedLayer)
    {
        JObject pause = userLayer?[PauseKey] as JObject;
        JObject retention = userLayer?[RetentionKey] as JObject;
        JObject admin = sharedLayer?[AdminKey] as JObject;
        return new MemoryControls(
            IsTrue(pause?["saving"]),
            IsTrue(pause?["recall"]),
            ClampDays(retention?["unusedDays"]),
            IsTrue(admin?["extractionDisabled"]));
    }

    /// <summary>The controls of a user: their own layer, and the shared layer for the admin switch.</summary>
    public static MemoryControls ForUser(SwarmUI.Accounts.User user)
        => user is null ? Default : From(SettingsService.GetUserSettings(user), SettingsService.GetSettings());

    /// <summary>Whether the automatic extractor may run for a user whose own master switch is <paramref name="userEnabled"/>: on, not paused, and not switched off by an admin.</summary>
    public bool ExtractionAllowed(bool userEnabled) => userEnabled && !SavingPaused && !AdminExtractionDisabled;

    /// <summary>The patch for the user's own settings that a client's request asks for: only the fields it names (<c>savingPaused</c>, <c>recallPaused</c>, <c>unusedDays</c>), each checked, or null
    /// when it names none. <c>unusedDays</c> is kept between <see cref="MinRetentionDays"/> and <see cref="MaxRetentionDays"/>, or 0 for off.</summary>
    public static JObject UserPatch(JObject request)
    {
        JObject patch = [];
        JObject pause = [];
        if (request?["savingPaused"]?.Type == JTokenType.Boolean)
        {
            pause["saving"] = (bool)request["savingPaused"];
        }
        if (request?["recallPaused"]?.Type == JTokenType.Boolean)
        {
            pause["recall"] = (bool)request["recallPaused"];
        }
        if (pause.Count > 0)
        {
            patch[PauseKey] = pause;
        }
        if (request?["unusedDays"]?.Type is JTokenType.Integer or JTokenType.Float)
        {
            double days = (double)request["unusedDays"];
            patch[RetentionKey] = new JObject { ["unusedDays"] = double.IsFinite(days) ? ClampDays((int)Math.Clamp(Math.Round(days), 0, MaxRetentionDays)) : 0 };
        }
        return patch.Count == 0 ? null : patch;
    }

    /// <summary>The shared settings with the admin switch set (the rest untouched).</summary>
    public static JObject WithAdminSwitch(JObject shared, bool extractionDisabled)
    {
        shared ??= [];
        shared[AdminKey] = new JObject { ["extractionDisabled"] = extractionDisabled };
        return shared;
    }

    /// <summary>A days value as stored: 0 for none or anything that is not a whole number, otherwise kept between <see cref="MinRetentionDays"/> and <see cref="MaxRetentionDays"/>.</summary>
    public static int ClampDays(JToken token)
    {
        if (token is null || token.Type != JTokenType.Integer)
        {
            return 0;
        }
        long days = token.Value<long>();
        return days <= 0 ? 0 : (int)Math.Clamp(days, MinRetentionDays, MaxRetentionDays);
    }

    /// <summary>A days value a client asked for, kept in range the same way (0 turns retention off).</summary>
    public static int ClampDays(int days) => days <= 0 ? 0 : Math.Clamp(days, MinRetentionDays, MaxRetentionDays);

    /// <summary>Whether a token is the boolean true.</summary>
    private static bool IsTrue(JToken token) => token?.Type == JTokenType.Boolean && (bool)token;
}
