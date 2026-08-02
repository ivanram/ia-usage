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

/// <summary>
/// The internal agent key ("Codex") stays exactly as-is everywhere it's
/// used for matching/storage — ChartBuilder's usage-service mapping,
/// PromptCountStore's rows, TrayOrchestrator's sampling calls — so
/// existing history.db data and the Claude/ChatGPT chart-overlay lookup
/// keep working. This is purely a presentation-layer relabel for wherever
/// the agent name is shown to the user, since "Codex" alone reads as
/// unrelated to ChatGPT unless you already know it's OpenAI's coding CLI.
/// </summary>
internal static class AgentDisplayNames
{
    public static string For(string agent) => agent switch
    {
        "Codex" => "ChatGPT Codex",
        _ => agent,
    };
}
