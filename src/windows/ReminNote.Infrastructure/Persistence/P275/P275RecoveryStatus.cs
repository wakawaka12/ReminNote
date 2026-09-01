using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using ReminNote.Infrastructure.Persistence.Backup;

namespace ReminNote.Infrastructure.Persistence.P275;

public enum P275BackupHashStatus
{
    NotSpecified,
    Matches,
    Missing,
    Mismatched,
    Unreadable
}

public enum P275BackupManifestStatus
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
    P275BackupManifestStatus ManifestStatus,
    string? ManifestArtifact,
    long? BackupByteLength,
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
        P275BackupManifestStatus.Unreadable,
        null,
        null,
        P275MigrationNextActions.ViewStatus,
        Array.Empty<string>(),
        null,
        false);
}

/// <summary>
/// Reads the marker and, when one is named, only bounded backup file and
/// manifest metadata/hash. It never opens the business SQLite database.
/// </summary>
public sealed class P275RecoveryStatusReader
{
    private static readonly JsonSerializerOptions ManifestSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16
    };

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
        var manifest = await ReadBackupManifestAsync(marker, cancellationToken).ConfigureAwait(false);
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

        if (manifest.Status is
                P275BackupManifestStatus.Missing or
                P275BackupManifestStatus.Mismatched or
                P275BackupManifestStatus.Unreadable)
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
            manifest.Status,
            manifest.Artifact,
            manifest.ByteLength,
            nextAction,
            marker.LastKnownGoodSchema,
            marker.LastAgentInstanceId,
            MarkerAvailable: true);
    }

    private async ValueTask<ManifestInspection> ReadBackupManifestAsync(
        P275MigrationStateMarker marker,
        CancellationToken cancellationToken)
    {
        if (marker.BackupArtifact is null || marker.BackupSha256 is null)
        {
            return ManifestInspection.NotSpecified;
        }

        string manifestArtifact;
        try
        {
            P275ArtifactNames.ValidateBackupArtifactId(marker.BackupArtifact);
            manifestArtifact = Path.ChangeExtension(marker.BackupArtifact, ".json");
            P275ArtifactNames.ValidateManifestArtifactId(manifestArtifact);
            layout.EnsureManifestForRead(manifestArtifact);
        }
        catch (P275PathValidationException)
        {
            return new ManifestInspection(
                P275BackupManifestStatus.Missing,
                Artifact: null,
                ByteLength: null);
        }
        catch (ArgumentException)
        {
            return new ManifestInspection(
                P275BackupManifestStatus.Unreadable,
                Artifact: null,
                ByteLength: null);
        }

        try
        {
            var payload = await ReadBoundedAsync(
                    layout.GetManifestPath(manifestArtifact),
                    SafetyBackupContract.MaxManifestBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (payload is null)
            {
                return new ManifestInspection(
                    P275BackupManifestStatus.Unreadable,
                    manifestArtifact,
                    ByteLength: null);
            }

            var manifest = DeserializeManifest(payload);
            var artifactPath = layout.GetBackupPath(marker.BackupArtifact);
            var artifactLength = new FileInfo(artifactPath).Length;
            var matches =
                string.Equals(manifest.ContractVersion, SafetyBackupContract.ContractVersion, StringComparison.Ordinal) &&
                string.Equals(manifest.RunId, marker.RunId.ToString("D"), StringComparison.Ordinal) &&
                string.Equals(manifest.ProfileScope, marker.ProfileScope, StringComparison.Ordinal) &&
                manifest.SourceSchema.SequenceEqual(marker.SourceSchema, StringComparer.Ordinal) &&
                manifest.TargetSchema.SequenceEqual(marker.TargetSchema, StringComparer.Ordinal) &&
                string.Equals(manifest.Artifact, marker.BackupArtifact, StringComparison.Ordinal) &&
                manifest.ByteLength == artifactLength &&
                manifest.ByteLength > 0 &&
                string.Equals(manifest.Sha256, marker.BackupSha256, StringComparison.Ordinal) &&
                string.Equals(manifest.Result, SafetyBackupContract.VerifiedResult, StringComparison.Ordinal);
            return new ManifestInspection(
                matches ? P275BackupManifestStatus.Matches : P275BackupManifestStatus.Mismatched,
                manifestArtifact,
                matches ? artifactLength : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            JsonException or
            FormatException or
            InvalidOperationException or
            ArgumentException or
            OverflowException)
        {
            return new ManifestInspection(
                P275BackupManifestStatus.Unreadable,
                manifestArtifact,
                ByteLength: null);
        }
    }

    private static SafetyBackupManifest DeserializeManifest(byte[] payload)
    {
        using var document = JsonDocument.Parse(
            payload,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The backup manifest root must be an object.");
        }

        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "contractVersion",
            "runId",
            "profileScope",
            "sourceSchema",
            "targetSchema",
            "createdAtUtc",
            "artifact",
            "byteLength",
            "sha256",
            "sourceFingerprint",
            "result"
        };
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!expected.Remove(property.Name))
            {
                throw new FormatException("The backup manifest has an unknown or duplicate field.");
            }
        }

        if (expected.Count != 0)
        {
            throw new FormatException("The backup manifest is missing a required field.");
        }

        return JsonSerializer.Deserialize<SafetyBackupManifest>(payload, ManifestSerializerOptions)
            ?? throw new FormatException("The backup manifest is empty.");
    }

    private static async ValueTask<byte[]?> ReadBoundedAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!P275FileSafety.IsRegularFile(path))
        {
            return null;
        }

        var buffer = new byte[maximumBytes + 1];
        var total = 0;
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                BufferSize = 4096,
                Options = FileOptions.SequentialScan
            });
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(
                    buffer.AsMemory(total, buffer.Length - total),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total < 1 || total > maximumBytes
            ? null
            : buffer.AsSpan(0, total).ToArray();
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

    private sealed record ManifestInspection(
        P275BackupManifestStatus Status,
        string? Artifact,
        long? ByteLength)
    {
        public static ManifestInspection NotSpecified { get; } = new(
            P275BackupManifestStatus.NotSpecified,
            null,
            null);
    }
}
