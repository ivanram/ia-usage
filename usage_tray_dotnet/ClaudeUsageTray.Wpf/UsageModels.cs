namespace ClaudeUsageTray;

public sealed class UsageBar
{
    public required string Label { get; init; }
    public required int Percent { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
}

public sealed class UsageSnapshot
{
    public required string ServiceName { get; init; }
    public bool Ok { get; init; }
    public string? ErrorMessage { get; init; }
    public List<UsageBar> Bars { get; init; } = new();
    public string? ExtraLine { get; init; }
}
