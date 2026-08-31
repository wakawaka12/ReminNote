using ReminNote.Agent.Transport;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Agent.Runtime;

/// <summary>
/// Adapter seam between the Agent host and the P2.75-01/03 providers. The
/// providers are intentionally not duplicated in this slice. Until the total
/// assembly supplies them, the default composition returns a stable
/// fail-closed recovery-required result.
/// </summary>
internal sealed class AgentMigrationStartupComposition
{
    private static readonly P275MigrationPlan DefaultPlan = new(
        [
            "20260828025922_InitialTaskSchema",
            "20260828120000_P2TaskLoop",
            "20260828130000_P2ContinuationDeleteBoundary",
            "20260831090000_P25StorageConsistency"
        ],
        [
            "20260828025922_InitialTaskSchema",
            "20260828120000_P2TaskLoop",
            "20260828130000_P2ContinuationDeleteBoundary",
            "20260831090000_P25StorageConsistency"
        ]);

    private readonly P275StartupGate startupGate;
    private readonly P275MigrationPlan migrationPlan;
    private readonly TimeSpan lockTimeout;

    private AgentMigrationStartupComposition(
        P275StartupGate startupGate,
        P275MigrationPlan migrationPlan,
        TimeSpan lockTimeout)
    {
        this.startupGate = startupGate ?? throw new ArgumentNullException(nameof(startupGate));
        this.migrationPlan = migrationPlan ?? throw new ArgumentNullException(nameof(migrationPlan));
        if (lockTimeout <= TimeSpan.Zero || lockTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        }

        this.lockTimeout = lockTimeout;
    }

    internal static AgentMigrationStartupComposition CreateDefault() =>
        new(
            new P275StartupGate(new P275MigrationAssemblyUnavailable()),
            DefaultPlan,
            TimeSpan.FromSeconds(10));

    /// <summary>
    /// Explicit integration point for the total assembly. P2.75-01 supplies
    /// source/backup, P2.75-03 supplies verification/state, and this slice's
    /// runner is composed once here; no provider implementation is copied
    /// into Agent.
    /// </summary>
    internal static AgentMigrationStartupComposition CreateForIntegration(
        IP275MigrationRunner migrationRunner,
        P275MigrationPlan migrationPlan,
        TimeSpan lockTimeout) =>
        new(new P275StartupGate(migrationRunner), migrationPlan, lockTimeout);

    internal async ValueTask<P275StartupGateResult> OpenAsync(
        ResolvedTransportProfile profile,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var paths = new P275ProfilePaths(profile.DataRoot, profile.ProfileName);
        var request = new P275MigrationRequest(
            paths,
            profile.ProfileScope,
            runId,
            migrationPlan,
            lockTimeout);
        return await startupGate.OpenAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private sealed class P275MigrationAssemblyUnavailable : IP275MigrationRunner
    {
        public ValueTask<P275MigrationResult> RunAsync(
            P275MigrationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                P275MigrationResult.Failure(
                    request.RunId,
                    P275MigrationState.RecoveryRequired,
                    P275MigrationFailureCodes.RecoveryRequired));
        }
    }
}
