namespace ClaudeUsageTray;

internal static class TimeFormat
{
    /// <summary>"faltan 3 días, 2 h" / "faltan 45 min" / "ya disponible".</summary>
    public static string Relative(DateTimeOffset target)
    {
        var span = target - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return "ya disponible";

        if (span.TotalDays >= 1)
        {
            var days = (int)span.TotalDays;
            var hours = span.Hours;
            return hours > 0
                ? $"faltan {days} día{(days == 1 ? "" : "s")}, {hours} h"
                : $"faltan {days} día{(days == 1 ? "" : "s")}";
        }

        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;
            return minutes > 0 ? $"faltan {hours} h, {minutes} min" : $"faltan {hours} h";
        }

        var mins = Math.Max(1, (int)span.TotalMinutes);
        return $"faltan {mins} min";
    }

    /// <summary>Short form for tooltip/tray text: "3d 2h" / "45min".</summary>
    public static string RelativeShort(DateTimeOffset target)
    {
        var span = target - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return "ya";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}min";
        return $"{Math.Max(1, (int)span.TotalMinutes)}min";
    }
}
