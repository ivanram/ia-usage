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
}

public sealed class UsageSnapshot
{
    public required string ServiceName { get; init; }
    public bool Ok { get; init; }
    public string? ErrorMessage { get; init; }
    public List<UsageBar> Bars { get; init; } = new();
    public string? ExtraLine { get; init; }
}
