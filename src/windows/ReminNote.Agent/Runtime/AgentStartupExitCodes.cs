namespace ReminNote.Agent.Runtime;

/// <summary>
/// Process exit codes consumed by Bootstrap. A migration-gated Agent must not
/// be mistaken for a crashed healthy Agent and must not enter the watchdog
/// retry loop.
/// </summary>
public static class AgentStartupExitCodes
{
    public const int MigrationBlocked = 20;
}
