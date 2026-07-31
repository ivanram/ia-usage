using System.Globalization;

namespace ClaudeUsageTray;

internal static class TimeFormat
{
    private static readonly CultureInfo Es = new("es-ES");

    /// <summary>"el 5 de agosto (en 5 días)" / "el 5 de agosto (en 3 h)" / "ya disponible".</summary>
    public static string ResetLine(DateTimeOffset target)
    {
        var span = target - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return "ya disponible";

        var local = target.ToLocalTime();
        var datePart = $"el {local.Day} de {local.ToString("MMMM", Es)}";

        string relativePart;
        if (span.TotalDays >= 1)
        {
            var days = (int)span.TotalDays;
            relativePart = $"en {days} día{(days == 1 ? "" : "s")}";
        }
        else if (span.TotalHours >= 1)
        {
            relativePart = $"en {(int)span.TotalHours} h";
        }
        else
        {
            relativePart = $"en {Math.Max(1, (int)span.TotalMinutes)} min";
        }

        return $"{datePart} ({relativePart})";
    }

    /// <summary>
    /// "en 4:32 h" / "en 20 minutos" / "ya disponible" — a precise
    /// countdown for short (same-day) reset windows, like Claude's
    /// rolling 5-hour limit. The calendar-style ResetLine ("el 5 de
    /// agosto (en 4 h)") reads fine for a reset days away, but for
    /// something resetting later today it's needlessly roundabout
    /// compared to just counting down.
    /// </summary>
    public static string ResetCountdown(DateTimeOffset target)
    {
        var span = target - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return "ya disponible";

        if (span.TotalHours >= 1)
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;
            return $"en {hours}:{minutes:D2} h";
        }

        var mins = Math.Max(1, (int)span.TotalMinutes);
        return $"en {mins} minuto{(mins == 1 ? "" : "s")}";
    }

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

    /// <summary>"actualizado hace un momento" / "hace 3 min" / "hace 2 h".</summary>
    public static string Ago(DateTime past)
    {
        var span = DateTime.Now - past;
        if (span < TimeSpan.FromSeconds(30)) return "hace un momento";
        if (span.TotalMinutes < 1) return "hace unos segundos";
        if (span.TotalHours < 1) return $"hace {(int)span.TotalMinutes} min";
        if (span.TotalDays < 1) return $"hace {(int)span.TotalHours} h";
        return $"hace {(int)span.TotalDays} d";
    }
}
