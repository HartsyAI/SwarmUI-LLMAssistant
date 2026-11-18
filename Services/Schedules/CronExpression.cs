namespace Hartsy.Extensions.LLMAssistant.Services.Schedules;

/// <summary>A five-field cron expression (<c>minute hour day-of-month month day-of-week</c>) and the times it names. Each field takes <c>*</c>, a number, a range
/// (<c>1-5</c>), a list (<c>1,3,5</c>) and a step (<c>*/15</c>, <c>10-50/10</c>); months and weekdays may be written as three-letter names. Sunday is 0 or 7. As in
/// standard cron, when both day-of-month and day-of-week are restricted a day matches if <i>either</i> does. No clock and no time zone are involved: this works on local
/// wall-clock times, and <see cref="ScheduleTrigger"/> turns them into instants.</summary>
public sealed class CronExpression
{
    /// <summary>How far ahead (in days) <see cref="NextLocal"/> looks before it gives up, so an expression that never fires (<c>0 0 31 2 *</c>) ends the search.</summary>
    public const int SearchDays = 366 * 5;

    /// <summary>The month names, by number.</summary>
    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>The weekday names, Sunday first (0).</summary>
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>The minutes (0 to 59) that match.</summary>
    private readonly bool[] _minutes = new bool[60];

    /// <summary>The hours (0 to 23) that match.</summary>
    private readonly bool[] _hours = new bool[24];

    /// <summary>The days of the month (1 to 31) that match.</summary>
    private readonly bool[] _daysOfMonth = new bool[32];

    /// <summary>The months (1 to 12) that match.</summary>
    private readonly bool[] _months = new bool[13];

    /// <summary>The weekdays (0 Sunday to 6) that match.</summary>
    private readonly bool[] _daysOfWeek = new bool[7];

    /// <summary>Whether the day-of-month field was <c>*</c>.</summary>
    private bool _domAny;

    /// <summary>Whether the day-of-week field was <c>*</c>.</summary>
    private bool _dowAny;

    /// <summary>The text it was made from, trimmed and with single spaces.</summary>
    public string Text { get; private set; } = "";

    /// <summary>Reads an expression. False, with the reason, when it is not a valid five-field one.</summary>
    public static bool TryParse(string text, out CronExpression cron, out string error)
    {
        cron = null;
        string[] parts = (text ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            error = "A cron expression has five fields: minute hour day-of-month month day-of-week.";
            return false;
        }
        CronExpression result = new() { Text = string.Join(' ', parts) };
        if (!Fill(parts[0], result._minutes, 0, 59, null, out error, "minute")
            || !Fill(parts[1], result._hours, 0, 23, null, out error, "hour")
            || !Fill(parts[2], result._daysOfMonth, 1, 31, null, out error, "day of month")
            || !Fill(parts[3], result._months, 1, 12, MonthNames, out error, "month")
            || !Fill(parts[4], result._daysOfWeek, 0, 7, DayNames, out error, "day of week"))
        {
            return false;
        }
        result._domAny = parts[2].StartsWith('*');
        result._dowAny = parts[4].StartsWith('*');
        cron = result;
        return true;
    }

    /// <summary>Fills <paramref name="set"/> from one field. A weekday of 7 is Sunday, so it is folded onto 0 afterwards.</summary>
    private static bool Fill(string field, bool[] set, int min, int max, string[] names, out string error, string what)
    {
        error = null;
        bool[] target = what == "day of week" ? new bool[8] : set;
        foreach (string item in field.Split(','))
        {
            string range = item;
            int step = 1;
            int slash = item.IndexOf('/');
            if (slash >= 0)
            {
                range = item[..slash];
                if (!int.TryParse(item[(slash + 1)..], out step) || step < 1 || step > max)
                {
                    error = $"The step in the {what} field ('{item}') must be a whole number from 1 to {max}.";
                    return false;
                }
            }
            int from, to;
            if (range == "")
            {
                error = $"The {what} field has an empty part.";
                return false;
            }
            if (range == "*")
            {
                (from, to) = (min, max);
            }
            else
            {
                string[] ends = range.Split('-');
                if (ends.Length > 2 || !Number(ends[0], names, min, max, out from) || !Number(ends[^1], names, min, max, out to))
                {
                    error = $"The {what} field ('{item}') must be numbers from {min} to {max}" + (names is null ? "." : " or names.");
                    return false;
                }
                // A step on a single number (5/10) runs from it to the end, as in other cron implementations.
                if (ends.Length == 1 && slash >= 0)
                {
                    to = max;
                }
                if (from > to)
                {
                    error = $"The range '{item}' in the {what} field runs backwards.";
                    return false;
                }
            }
            for (int value = from; value <= to; value += step)
            {
                target[value] = true;
            }
        }
        if (what == "day of week")
        {
            for (int day = 0; day < 7; day++)
            {
                set[day] = target[day] || (day == 0 && target[7]);
            }
        }
        return true;
    }

    /// <summary>One number or name, inside the field's range.</summary>
    private static bool Number(string text, string[] names, int min, int max, out int value)
    {
        value = 0;
        if (names is not null)
        {
            int index = Array.IndexOf(names, text.ToLowerInvariant());
            if (index >= 0)
            {
                // Months count from 1, weekdays from 0.
                value = names == MonthNames ? index + 1 : index;
                return true;
            }
        }
        return int.TryParse(text, out value) && value >= min && value <= max;
    }

    /// <summary>Whether the day (not the time of day) matches, by the day-of-month and day-of-week rule.</summary>
    public bool MatchesDay(DateTime local)
    {
        if (!_months[local.Month])
        {
            return false;
        }
        bool dom = _daysOfMonth[local.Day];
        bool dow = _daysOfWeek[(int)local.DayOfWeek];
        // Standard cron: both restricted means either one may match; otherwise only the restricted one counts.
        return _domAny || _dowAny ? dom && dow : dom || dow;
    }

    /// <summary>Whether the minute of <paramref name="local"/> is one the expression names.</summary>
    public bool Matches(DateTime local) => MatchesDay(local) && _hours[local.Hour] && _minutes[local.Minute];

    /// <summary>The first matching minute strictly after <paramref name="afterLocal"/> (seconds ignored), or null when there is none within <see cref="SearchDays"/>. The search
    /// skips whole days, hours and minutes that cannot match, so a rare expression costs a few thousand steps, not millions.</summary>
    public DateTime? NextLocal(DateTime afterLocal)
    {
        DateTime t = new(afterLocal.Year, afterLocal.Month, afterLocal.Day, afterLocal.Hour, afterLocal.Minute, 0, DateTimeKind.Unspecified);
        t = t.AddMinutes(1);
        DateTime limit = t.AddDays(SearchDays);
        while (t < limit)
        {
            if (!MatchesDay(t))
            {
                t = t.Date.AddDays(1);
            }
            else if (!_hours[t.Hour])
            {
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Unspecified).AddHours(1);
            }
            else if (!_minutes[t.Minute])
            {
                t = t.AddMinutes(1);
            }
            else
            {
                return t;
            }
        }
        return null;
    }
}
