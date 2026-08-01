using System.Globalization;

namespace ClaudeUsageTray;

internal static class TimeFormat
{
    private static readonly CultureInfo Es = new("es-ES");
    private static readonly CultureInfo En = new("en-US");
    private static CultureInfo Culture => Strings.Current == AppLanguage.Spanish ? Es : En;

    /// <summary>"el 5 de agosto (en 5 días)" / "August 5 (in 5 days)" / "ya disponible".</summary>
    public static string ResetLine(DateTimeOffset target)
    {
        var span = target - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return Strings.T("time.available");

        var local = target.ToLocalTime();
        var month = local.ToString("MMMM", Culture);
        var datePart = Strings.Current == AppLanguage.Spanish ? $"el {local.Day} de {month}" : $"{month} {local.Day}";

        string relativePart;
        if (span.TotalDays >= 1)
        {
            // Calendar-day difference, not raw span.TotalDays truncated —
            // the date shown right next to this is a calendar date, so if
            // today is the 1st and the target is the 3rd that reads as
            // "2 days" regardless of what time of day it currently is, not
            // "1 day" just because fewer than 48 raw hours remain.
            var days = (local.Date - DateTimeOffset.Now.ToLocalTime().Date).Days;
            relativePart = Strings.Current == AppLanguage.Spanish
                ? $"en {days} día{(days == 1 ? "" : "s")}"
                : $"in {days} day{(days == 1 ? "" : "s")}";
        }
        else if (span.TotalHours >= 1)
        {
            relativePart = Strings.Current == AppLanguage.Spanish ? $"en {(int)span.TotalHours} h" : $"in {(int)span.TotalHours} h";
        }
        else
        {
            var mins = Math.Max(1, (int)span.TotalMinutes);
            relativePart = Strings.Current == AppLanguage.Spanish ? $"en {mins} min" : $"in {mins} min";
        }

        return $"{datePart} ({relativePart})";
    }

    /// <summary>
    /// "a las 3:00 am (en 1:55 horas)" / "at 3:00 am (in 1h 55m)" /
    /// "ya disponible" — precise clock time + countdown for short
    /// (same-day) reset windows, like Claude's rolling 5-hour limit. The
    /// calendar-style ResetLine ("el 5 de agosto (en 4 h)") reads fine
    /// for a reset days away, but for something resetting later today
    /// the actual clock time is more useful than a date.
    /// </summary>
    public static string ResetCountdown(DateTimeOffset target)
    {
        var span = target - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return Strings.T("time.available");

        var local = target.ToLocalTime();
        var hour12 = local.Hour % 12;
        if (hour12 == 0) hour12 = 12;
        var ampm = local.Hour < 12 ? "am" : "pm";
        var timePart = Strings.Current == AppLanguage.Spanish
            ? $"a las {hour12}:{local.Minute:D2} {ampm}"
            : $"at {hour12}:{local.Minute:D2} {ampm}";

        string relativePart;
        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;
            relativePart = Strings.Current == AppLanguage.Spanish
                ? $"en {hours}:{minutes:D2} horas"
                : $"in {hours}h {minutes}m";
        }
        else
        {
            var mins = Math.Max(1, (int)span.TotalMinutes);
            relativePart = Strings.Current == AppLanguage.Spanish
                ? $"en {mins} minuto{(mins == 1 ? "" : "s")}"
                : $"in {mins} minute{(mins == 1 ? "" : "s")}";
        }

        return $"{timePart} ({relativePart})";
    }

    /// <summary>Short form for tooltip/tray text: "3d 2h" / "45min".</summary>
    public static string RelativeShort(DateTimeOffset target)
    {
        var span = target - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return Strings.Current == AppLanguage.Spanish ? "ya" : "now";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}min";
        return $"{Math.Max(1, (int)span.TotalMinutes)}min";
    }

    /// <summary>"hace un momento" / "just now" / "3 min ago".</summary>
    public static string Ago(DateTime past)
    {
        var span = DateTime.Now - past;
        if (span < TimeSpan.FromSeconds(30)) return Strings.T("time.ago.moment");
        if (span.TotalMinutes < 1) return Strings.T("time.ago.seconds");
        if (span.TotalHours < 1) return Strings.F("time.ago.minutes", (int)span.TotalMinutes);
        if (span.TotalDays < 1) return Strings.F("time.ago.hours", (int)span.TotalHours);
        return Strings.F("time.ago.days", (int)span.TotalDays);
    }
}
