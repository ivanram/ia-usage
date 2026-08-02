namespace ClaudeUsageTray;

/// <summary>
/// One individual chat/session from a local coding agent (Claude Code,
/// Codex, ...) — shared shape so the /proyectos Telegram command can merge
/// every agent's own helper into a single list, then group by
/// <see cref="ProjectPath"/> for display. <see cref="Name"/> is null when
/// the agent doesn't have a friendly name available for this particular
/// session (e.g. Claude Code only derives one for the currently-running
/// instance, not historical ones).
/// </summary>
public readonly record struct AgentTask(string Agent, string ProjectPath, DateTime LastActivity, bool IsActiveNow, string? Name);
