using ReminNote.Core.Protocol;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Agent.Runtime;

/// <summary>
/// The only Agent-side recovery writer. Profile resolution remains owned by
/// AgentRecoveryCommandAdapter; this actor derives the canonical profile
/// paths and delegates all database work to the P2.75 Infrastructure service.
/// </summary>
internal sealed class P275RecoveryActor : IAgentRecoveryActor
{
    private readonly P275MigrationPlan migrationPlan;
    private readonly TimeSpan lockTimeout;

    public P275RecoveryActor(
        P275MigrationPlan migrationPlan,
        TimeSpan? lockTimeout = null)
    {
        this.migrationPlan = migrationPlan ?? throw new ArgumentNullException(nameof(migrationPlan));
        this.lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(10);
        if (this.lockTimeout <= TimeSpan.Zero || this.lockTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        }
    }

    public async ValueTask<AgentRecoveryOperationResult> RestoreAsync(
        AgentRecoveryRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var paths = new P275ProfilePaths(
                request.Profile.DataRoot,
                request.Profile.ProfileName);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(paths.ProfileRoot, request.Profile.ProfileRoot, comparison) ||
                !string.Equals(paths.ActiveDatabasePath, request.Profile.DatabasePath, comparison))
            {
                return AgentRecoveryOperationResult.Failed(P275MigrationFailureCodes.PathInvalid);
            }

            var result = await P275RecoveryService
                    .RestoreAsync(
                        new P275RecoveryRestoreRequest(
                            paths,
                            request.Profile.ProfileScope,
                            request.BackupArtifactId,
                            migrationPlan,
                            lockTimeout,
                            ProtocolIds.NewEventId().ToString("D")),
                        cancellationToken)
                    .ConfigureAwait(false);
            return result.Succeeded
                ? AgentRecoveryOperationResult.SucceededResult()
                : AgentRecoveryOperationResult.Failed(
                    result.FailureCode ?? P275MigrationFailureCodes.RestoreFailed);
        }
        catch (OperationCanceledException)
        {
            return AgentRecoveryOperationResult.Failed(P275MigrationFailureCodes.RestoreFailed);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return AgentRecoveryOperationResult.Failed(P275MigrationFailureCodes.RestoreFailed);
        }
    }
}
