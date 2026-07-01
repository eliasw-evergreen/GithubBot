using TimeZoneConverter;

namespace GithubBot.Services;

public class WorkHoursService(PreferencesService prefs, IConfiguration config)
{
    private WorkHoursConfig GetConfig() => prefs.ResolveWorkHours(config);

    public bool IsWorkTime()
    {
        var cfg = GetConfig();
        if (!TimeSpan.TryParse(cfg.Start, out var start) || !TimeSpan.TryParse(cfg.End, out var end))
            return true; // not configured → always work time (feature off)

        var tz = GetTimezone(cfg.Timezone);
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var days = ParseDays(cfg.Days);

        if (!days.Contains((int)now.DayOfWeek)) return false;

        var timeOfDay = now.TimeOfDay;
        return timeOfDay >= start && timeOfDay < end;
    }

    public TimeSpan TimeUntilNextWorkStart()
    {
        var cfg = GetConfig();
        if (!TimeSpan.TryParse(cfg.Start, out var start) || !TimeSpan.TryParse(cfg.End, out _))
            return TimeSpan.Zero;

        var tz = GetTimezone(cfg.Timezone);
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var days = ParseDays(cfg.Days);

        // Walk forward day by day until we find a work day
        var candidate = now.Date.Add(start);
        if (candidate <= now) candidate = candidate.AddDays(1);

        for (int i = 0; i < 8; i++)
        {
            if (days.Contains((int)candidate.DayOfWeek))
            {
                var candidateUtc = TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), tz);
                var delay = candidateUtc - DateTime.UtcNow;
                return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
            }
            candidate = candidate.AddDays(1);
        }

        return TimeSpan.FromHours(24); // fallback
    }

    private static TimeZoneInfo GetTimezone(string? id)
    {
        if (string.IsNullOrEmpty(id)) return TimeZoneInfo.Utc;
        try { return TZConvert.GetTimeZoneInfo(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    private static HashSet<int> ParseDays(string? days)
    {
        if (string.IsNullOrEmpty(days))
            return [1, 2, 3, 4, 5]; // Mon–Fri default
        var result = new HashSet<int>();
        foreach (var part in days.Split(','))
            if (int.TryParse(part.Trim(), out var d))
                result.Add(d % 7); // normalise: ISO Mon=1..Sun=7 → DayOfWeek Sun=0..Sat=6
        return result;
    }
}
