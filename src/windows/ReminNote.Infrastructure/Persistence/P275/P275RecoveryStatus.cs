using System.Security;
using System.Security.Cryptography;

namespace ReminNote.Infrastructure.Persistence.P275;

public enum P275BackupHashStatus
{
    NotSpecified,
    Matches,
    Missing,
    Mismatched,
    Unreadable
}

/// <summary>
/// Bounded, UI-safe recovery status. It intentionally contains no database
/// rows, absolute paths or exception text.
/// </summary>
public sealed record P275RecoveryStatus(
    P275RecoveryStatusMode Mode,
    P275MigrationState State,
    bool Ready,
    bool Writable,
    bool RecoveryRequired,
    string? FailureCode,
    string? RunId,
    string? ProfileScope,
    IReadOnlyList<string> SourceSchema,
    IReadOnlyList<string> TargetSchema,
    string? BackupArtifact,
    string? BackupSha256,
    string? CandidateArtifact,
    P275BackupHashStatus BackupHashStatus,
    string NextAction,
    IReadOnlyList<string> LastKnownGoodSchema,
    string? LastAgentInstanceId,
    bool MarkerAvailable)
{
    public static P275RecoveryStatus Unavailable(string failureCode = P275MigrationFailureCodes.RecoveryRequired) => new(
        P275RecoveryStatusMode.Unavailable,
        P275MigrationState.RecoveryRequired,
        false,
        false,
        true,
        failureCode,
        null,
        null,
        Array.Empty<string>(),
        Array.Empty<string>(),
        null,
        null,
        null,
        P275BackupHashStatus.Unreadable,
        P275MigrationNextActions.ViewStatus,
        Array.Empty<string>(),
        null,
        false);
}

/// <summary>
/// Reads the marker and, when one is named, only the backup file's bounded
/// metadata/hash. It never opens the business SQLite database.
/// </summary>
public sealed class P275RecoveryStatusReader
{
    private readonly P275MigrationStateStore stateStore;
    private readonly P275ProfileLayout layout;

    public P275RecoveryStatusReader(P275ProfileLayout layout)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        stateStore = new P275MigrationStateStore(this.layout);
    }

    public async ValueTask<P275RecoveryStatus> ReadAsync(
        string expectedProfileScope,
        CancellationToken cancellationToken = default)
    {
        var read = await stateStore.ReadAsync(expectedProfileScope, cancellationToken).ConfigureAwait(false);
        if (!read.IsUsable)
        {
            return P275RecoveryStatus.Unavailable(read.FailureCode ?? P275MigrationFailureCodes.RecoveryRequired);
        }

        var marker = read.Marker!;
        var readiness = P275MigrationStatePolicy.Evaluate(marker.State);
        var backupHashStatus = await ReadBackupHashStatusAsync(marker, cancellationToken).ConfigureAwait(false);
        var failureCode = marker.FailureCode;
        var ready = readiness.Ready;
        var writable = readiness.Writable;
        var mode = readiness.Mode;
        if (backupHashStatus is
                P275BackupHashStatus.Missing or
                P275BackupHashStatus.Mismatched or
                P275BackupHashStatus.Unreadable)
        {
            mode = P275RecoveryStatusMode.RecoveryRequired;
            ready = false;
            writable = false;
            failureCode ??= P275MigrationFailureCodes.RecoveryBackupInvalid;
        }

        var nextAction = marker.NextAction;
        if (mode == P275RecoveryStatusMode.RecoveryRequired &&
            nextAction == P275MigrationNextActions.None)
        {
            nextAction = P275MigrationNextActions.ViewStatus;
        }

        return new P275RecoveryStatus(
            mode,
            marker.State,
            ready,
            writable,
            mode is P275RecoveryStatusMode.RecoveryRequired or P275RecoveryStatusMode.Unavailable,
            failureCode,
            marker.RunId.ToString("D"),
            marker.ProfileScope,
            marker.SourceSchema,
            marker.TargetSchema,
            marker.BackupArtifact,
            marker.BackupSha256,
            marker.CandidateArtifact,
            backupHashStatus,
            nextAction,
            marker.LastKnownGoodSchema,
            marker.LastAgentInstanceId,
            MarkerAvailable: true);
    }

    private async ValueTask<P275BackupHashStatus> ReadBackupHashStatusAsync(
        P275MigrationStateMarker marker,
        CancellationToken cancellationToken)
    {
        if (marker.BackupArtifact is null || marker.BackupSha256 is null)
        {
            return P275BackupHashStatus.NotSpecified;
        }

        string path;
        try
        {
            layout.EnsureBackupForRead(marker.BackupArtifact);
            path = layout.GetBackupPath(marker.BackupArtifact);
        }
        catch (P275PathValidationException)
        {
            return P275BackupHashStatus.Missing;
        }

        try
        {
            var hash = await ComputeFileHashAsync(path, cancellationToken).ConfigureAwait(false);
            return string.Equals(hash, marker.BackupSha256, StringComparison.Ordinal)
                ? P275BackupHashStatus.Matches
                : P275BackupHashStatus.Mismatched;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            CryptographicException)
        {
            return P275BackupHashStatus.Unreadable;
        }
    }

    private static async ValueTask<string> ComputeFileHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.SequentialScan
            });
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
