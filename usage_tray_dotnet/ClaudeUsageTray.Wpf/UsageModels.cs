namespace ClaudeUsageTray;

public sealed class UsageBar
{
    public required string Label { get; init; }
    public required int Percent { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    /// <summary>
    /// Marks the one bar per provider tracked for history/stats — each
    /// service's main recurring quota (a calendar week for Claude/ChatGPT)
    /// rather than a secondary/rolling window. A stable flag set by the
    /// provider itself, not derived from Label, since Label is a localized
    /// display string that changes with the app's language setting.
    /// </summary>
    public bool IsPrimary { get; init; }
    /// <summary>
    /// Marks Claude's model-specific Fable bar (see ClaudeProvider.FindModelLimit)
    /// so it can be tracked as its own history/stats series (service key
    /// <see cref="ClaudeProvider.FableServiceName"/>, "Claude - Fable")
    /// separate from Claude's own aggregate weekly quota — a stable flag for
    /// the same reason <see cref="IsPrimary"/> is: matching on the localized
    /// <see cref="Label"/> would break whenever the display language changes.
    /// </summary>
    public bool IsFable { get; init; }
    /// <summary>Short, notification-sized cadence tag (e.g. "semanal", "5 horas") for the "(...)" suffix on reset/exhausted alerts — <see cref="Label"/> itself reads more like "Límite semanal", too long to parenthesize.</summary>
    public string Qualifier { get; init; } = "";
    /// <summary>1-3 character tag (e.g. "S:", "5H:") shown to the left of the bar in the popup's compact layout, when a service has more than one bar — left empty for a service's only bar, since there's nothing to disambiguate.</summary>
    public string ShortPrefix { get; init; } = "";
}

public sealed class UsageSnapshot
{
    public required string ServiceName { get; init; }
    public bool Ok { get; init; }
    public string? ErrorMessage { get; init; }
    public List<UsageBar> Bars { get; init; } = new();
    public string? ExtraLine { get; init; }
}
