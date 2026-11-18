using Newtonsoft.Json.Linq;
using NodaTime;

namespace Hartsy.Extensions.LLMAssistant.Services.Schedules;

/// <summary>The fixed limits on schedules. Constants, not settings: the shortest gap between runs in particular is what keeps a schedule from becoming a loop that burns a
/// user's models, so a setting a user could write would defeat it.</summary>
public static class ScheduleLimits
{
    /// <summary>The shortest gap between two runs of one schedule, in minutes.</summary>
    public const int MinIntervalMinutes = 5;
    /// <summary>The longest "every N minutes" interval (30 days).</summary>
    public const int MaxEveryMinutes = 60 * 24 * 30;
    /// <summary>How many schedules one user may have.</summary>
    public const int MaxPerUser = 20;
    /// <summary>The most characters of a schedule's prompt.</summary>
    public const int MaxPromptChars = 20_000;
    /// <summary>The most characters of a schedule's title.</summary>
    public const int MaxTitleChars = 80;
    /// <summary>How many finished runs of a schedule are kept (the oldest are deleted as new ones start).</summary>
    public const int KeepRuns = 6;
    /// <summary>How late a run may start and still be run (after the server was down or busy). A later one is skipped, not made up; there is never a backlog.</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(6);
    /// <summary>How often the scheduler looks for runs that are due, in seconds.</summary>
    public const int TickSeconds = 10;
    /// <summary>How many firings are looked at to check a cron expression's shortest gap.</summary>
    public const int GapChecks = 12;
}

/// <summary>When a schedule runs: every N minutes, by a cron expression, or once at a time. Pure: it holds no clock, and every method takes the time it is asked about, so the
/// maths (cron, time zones, daylight saving) is testable with fixed dates.
/// <list type="bullet">
/// <item><b>every</b>: a steady cadence from an anchor (when the schedule was made), so a restart does not shift it.</item>
/// <item><b>cron</b>: five fields read in the schedule's time zone. A time that does not exist (the hour skipped when clocks go forward) is skipped, and one that happens
/// twice (clocks going back) runs at its first occurrence only.</item>
/// <item><b>at</b>: one instant.</item>
/// </list></summary>
public sealed class ScheduleTrigger
{
    /// <summary>The kinds of trigger.</summary>
    public const string Every = "every", Cron = "cron", At = "at";

    /// <summary><see cref="Every"/>, <see cref="Cron"/> or <see cref="At"/>.</summary>
    public string Kind { get; private set; }

    /// <summary>The interval, for <see cref="Every"/>.</summary>
    public int EveryMinutes { get; private set; }

    /// <summary>The expression, for <see cref="Cron"/>.</summary>
    public CronExpression CronExpression { get; private set; }

    /// <summary>The instant, for <see cref="At"/>.</summary>
    public DateTime AtUtc { get; private set; }

    /// <summary>Where the cadence of <see cref="Every"/> counts from.</summary>
    public DateTime AnchorUtc { get; private set; }

    /// <summary>The time zone cron is read in.</summary>
    public DateTimeZone Zone { get; private set; } = DateTimeZone.Utc;

    /// <summary>The id of <see cref="Zone"/>, as stored (<c>UTC</c> or an IANA name).</summary>
    public string ZoneId => Zone.Id;

    /// <summary>Finds a time zone by its IANA name (such as <c>Europe/Paris</c>), or <c>UTC</c> for none. An unknown one is an error, never a silent change to UTC.
    /// The zone data is NodaTime's own, not the operating system's: SwarmUI runs with invariant globalization, under which Windows cannot look an IANA name up, and the
    /// browser (which names the user's zone) only speaks IANA.</summary>
    public static bool TryZone(string id, out DateTimeZone zone, out string error)
    {
        error = null;
        zone = DateTimeZone.Utc;
        if (string.IsNullOrWhiteSpace(id) || id.Trim().Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string wanted = id.Trim();
        // The names are case-sensitive in the database; a user typing europe/paris means the same zone.
        string found = DateTimeZoneProviders.Tzdb.Ids.FirstOrDefault(i => i.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            error = $"'{id}' is not a time zone this server knows (use a name such as Europe/Paris or America/New_York).";
            return false;
        }
        zone = DateTimeZoneProviders.Tzdb[found];
        return true;
    }

    /// <summary>A UTC instant as the wall-clock time in <see cref="Zone"/>.</summary>
    public DateTime ToLocal(DateTime utc) => Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).InZone(Zone).LocalDateTime.ToDateTimeUnspecified();

    /// <summary>Reads a trigger from its stored form: <c>{kind, everyMinutes?, cron?, at?, anchor?}</c>. Checks it as well: the interval range, the cron expression, and that no
    /// two runs of a cron schedule come closer than <see cref="ScheduleLimits.MinIntervalMinutes"/>. <paramref name="nowUtc"/> starts the cadence when the trigger has no anchor yet.</summary>
    public static bool TryFromJson(JObject json, string zoneId, DateTime nowUtc, out ScheduleTrigger trigger, out string error)
    {
        trigger = null;
        if (json is null)
        {
            error = "A schedule needs a trigger.";
            return false;
        }
        if (!TryZone(zoneId, out DateTimeZone zone, out error))
        {
            return false;
        }
        ScheduleTrigger result = new() { Kind = json["kind"]?.ToString()?.Trim().ToLowerInvariant(), Zone = zone, AnchorUtc = nowUtc };
        if (ReadDate(json["anchor"]) is DateTime anchor)
        {
            result.AnchorUtc = anchor;
        }
        switch (result.Kind)
        {
            case Every:
                if (json["everyMinutes"]?.Type != JTokenType.Integer)
                {
                    error = "'Every' needs a number of minutes.";
                    return false;
                }
                long minutes = json["everyMinutes"].Value<long>();
                if (minutes < ScheduleLimits.MinIntervalMinutes || minutes > ScheduleLimits.MaxEveryMinutes)
                {
                    error = $"'Every' must be from {ScheduleLimits.MinIntervalMinutes} minutes to {ScheduleLimits.MaxEveryMinutes} minutes (30 days).";
                    return false;
                }
                result.EveryMinutes = (int)minutes;
                break;
            case Cron:
                if (!CronExpression.TryParse(json["cron"]?.ToString(), out CronExpression cron, out error))
                {
                    return false;
                }
                result.CronExpression = cron;
                if (result.ShortestGapMinutes(nowUtc) is double gap and < ScheduleLimits.MinIntervalMinutes)
                {
                    error = $"That would run every {Math.Max(1, (int)Math.Round(gap))} minute(s); schedules must be at least {ScheduleLimits.MinIntervalMinutes} minutes apart.";
                    return false;
                }
                break;
            case At:
                if (ReadDate(json["at"]) is not DateTime at)
                {
                    error = "'At' needs a date and time, for example 2026-12-01T09:00:00Z.";
                    return false;
                }
                result.AtUtc = at;
                break;
            default:
                error = "The trigger kind must be every, cron or at.";
                return false;
        }
        error = null;
        trigger = result;
        return true;
    }

    /// <summary>A date from JSON, as a UTC instant: a date token, or text such as <c>2026-12-01T09:00:00Z</c> (text with no offset is read as UTC). Null when it is not one.</summary>
    public static DateTime? ReadDate(JToken token)
    {
        if (token is null)
        {
            return null;
        }
        if (token.Type == JTokenType.Date)
        {
            return token.Value<DateTime>().ToUniversalTime();
        }
        return token.Type == JTokenType.String && DateTime.TryParse(token.ToString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed) ? parsed : null;
    }

    /// <summary>The stored form of the trigger.</summary>
    public JObject ToJson()
    {
        JObject json = new() { ["kind"] = Kind };
        switch (Kind)
        {
            case Every:
                json["everyMinutes"] = EveryMinutes;
                json["anchor"] = AnchorUtc.ToString("o");
                break;
            case Cron:
                json["cron"] = CronExpression.Text;
                break;
            case At:
                json["at"] = AtUtc.ToString("o");
                break;
        }
        return json;
    }

    /// <summary>The first run strictly after <paramref name="afterUtc"/>, or null when there is none (a one-time trigger that has passed, or a cron that never fires).</summary>
    public DateTime? NextAfter(DateTime afterUtc)
    {
        afterUtc = afterUtc.ToUniversalTime();
        switch (Kind)
        {
            case Every:
                {
                    long gap = EveryMinutes * TimeSpan.TicksPerMinute;
                    long behind = (afterUtc - AnchorUtc).Ticks;
                    // The first anchor + n * gap that is later than "after" (n can be 0 when the anchor itself is still ahead).
                    long steps = behind < 0 ? 0 : behind / gap + 1;
                    return AnchorUtc.AddTicks(steps * gap);
                }
            case At:
                return AtUtc > afterUtc ? AtUtc : null;
            case Cron:
                return NextCron(afterUtc);
            default:
                return null;
        }
    }

    /// <summary>The next several runs after <paramref name="afterUtc"/>, for showing what a trigger means.</summary>
    public List<DateTime> Preview(DateTime afterUtc, int count)
    {
        List<DateTime> times = [];
        DateTime? next = NextAfter(afterUtc);
        while (next is not null && times.Count < count)
        {
            times.Add(next.Value);
            next = NextAfter(next.Value);
        }
        return times;
    }

    /// <summary>The cron schedule's next run, with the time zone's gaps and repeats handled.</summary>
    private DateTime? NextCron(DateTime afterUtc)
    {
        DateTime local = ToLocal(afterUtc);
        // Each pass finds the next matching wall-clock minute; a minute that does not exist, or whose first occurrence is already past, sends the search on from it.
        for (int pass = 0; pass < 50; pass++)
        {
            DateTime? candidate = CronExpression.NextLocal(local);
            if (candidate is null)
            {
                return null;
            }
            local = candidate.Value;
            NodaTime.TimeZones.ZoneLocalMapping mapping = Zone.MapLocal(LocalDateTime.FromDateTime(local));
            // Clocks going forward skip an hour: a minute in it does not exist, and is skipped that day.
            if (mapping.Count == 0)
            {
                continue;
            }
            // Clocks going back repeat an hour: a minute in it runs at its first occurrence only.
            DateTime utc = (mapping.Count == 1 ? mapping.Single() : mapping.First()).ToInstant().ToDateTimeUtc();
            if (utc > afterUtc)
            {
                return utc;
            }
        }
        return null;
    }

    /// <summary>The smallest gap, in minutes, between consecutive runs among the next several (cron only), or null when there are fewer than two. A year's worth is looked at
    /// by sampling forward from <paramref name="fromUtc"/>, so an expression that bunches runs up on some days only is caught too.</summary>
    private double? ShortestGapMinutes(DateTime fromUtc)
    {
        double? shortest = null;
        DateTime? previous = null;
        DateTime cursor = fromUtc;
        for (int i = 0; i < ScheduleLimits.GapChecks * 8; i++)
        {
            DateTime? next = NextAfter(cursor);
            if (next is null)
            {
                break;
            }
            if (previous is not null)
            {
                double gap = (next.Value - previous.Value).TotalMinutes;
                shortest = shortest is null ? gap : Math.Min(shortest.Value, gap);
            }
            previous = next;
            cursor = next.Value;
        }
        return shortest;
    }

    /// <summary>A short description for lists, for example "every 30 minutes".</summary>
    public string Describe() => Kind switch
    {
        Every => EveryMinutes % 1440 == 0 ? $"every {EveryMinutes / 1440} day(s)" : EveryMinutes % 60 == 0 ? $"every {EveryMinutes / 60} hour(s)" : $"every {EveryMinutes} minutes",
        Cron => $"cron {CronExpression.Text} ({ZoneId})",
        At => $"once at {AtUtc:u}",
        _ => "",
    };
}
