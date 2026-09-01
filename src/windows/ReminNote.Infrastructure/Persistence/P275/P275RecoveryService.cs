using System.Data;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReminNote.Core.Protocol;
using ReminNote.Infrastructure.Persistence.Backup;

namespace ReminNote.Infrastructure.Persistence.P275;

public sealed record P275RecoveryRestoreRequest(
    P275ProfilePaths Paths,
    string ProfileScope,
    string BackupArtifactId,
    P275MigrationPlan Plan,
    TimeSpan LockTimeout,
    string? AgentInstanceId = null);

public sealed record P275RecoveryResult(
    bool Succeeded,
    string? FailureCode)
{
    public static P275RecoveryResult Success() => new(true, null);

    public static P275RecoveryResult Failed(string failureCode) =>
        new(
            false,
            P275MigrationFailureCodes.IsStable(failureCode)
                ? failureCode
                : P275MigrationFailureCodes.RestoreFailed);
}

public sealed record P275RecoveryBackup(
    SafetyBackupManifest Manifest,
    string ArtifactPath,
    string ManifestPath,
    long ByteLength,
    string Sha256)
{
    public P275VerifiedSafetyBackup ToVerifiedBackup() => new(
        Manifest.Artifact,
        ArtifactPath,
        sourceIsEmpty: false,
        Manifest.SourceSchema,
        new P275SourceFingerprint(
            P275SourceFingerprintCodec.Encode(Manifest.SourceFingerprint)),
        ByteLength,
        Sha256);
}

/// <summary>
/// Reads one explicitly selected backup as a capability. The artifact is not
/// usable for restore until its bounded manifest, profile/schema identity,
/// standalone shape and SHA-256 all agree.
/// </summary>
public sealed class P275RecoveryBackupReader
{
    private const string ManifestTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly JsonSerializerOptions ManifestSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16
    };

    public static async ValueTask<P275RecoveryBackup> ReadAsync(
        P275ProfileLayout layout,
        string expectedProfileScope,
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProfileScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);

        try
        {
            ProtocolProfileScope.Validate(expectedProfileScope);
            P275ArtifactNames.ValidateBackupArtifactId(artifactId);
            layout.EnsureBackupForRead(artifactId);
            var artifactPath = layout.GetBackupPath(artifactId);
            if (!P275FileSafety.IsRegularFile(artifactPath) ||
                P275CandidateSidecarFinalizer.EnumerateSidecars(artifactPath).Count != 0)
            {
                throw new FormatException("The selected backup is not a standalone file.");
            }

            var manifestArtifact = Path.ChangeExtension(artifactId, ".json");
            P275ArtifactNames.ValidateManifestArtifactId(manifestArtifact);
            layout.EnsureManifestForRead(manifestArtifact);
            var manifestPath = layout.GetManifestPath(manifestArtifact);
            var payload = await ReadBoundedAsync(
                    manifestPath,
                    SafetyBackupContract.MaxManifestBytes,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new FormatException("The backup manifest is missing or oversized.");
            var manifest = DeserializeManifest(payload);
            ValidateManifest(manifest, expectedProfileScope, artifactId);

            var actual = await HashFileAsync(artifactPath, cancellationToken)
                .ConfigureAwait(false);
            if (actual.ByteLength != manifest.ByteLength ||
                !string.Equals(actual.Sha256, manifest.Sha256, StringComparison.Ordinal))
            {
                throw new CryptographicException("The selected backup hash does not match its manifest.");
            }

            return new P275RecoveryBackup(
                manifest,
                artifactPath,
                manifestPath,
                actual.ByteLength,
                actual.Sha256);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (P275MigrationOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new P275MigrationOperationException(
                P275MigrationFailureCodes.RecoveryBackupInvalid,
                exception);
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
            throw new FormatException("The backup manifest root is not an object.");
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

        return JsonSerializer.Deserialize<SafetyBackupManifest>(
                   payload,
                   ManifestSerializerOptions)
               ?? throw new FormatException("The backup manifest is empty.");
    }

    internal static void ValidateManifest(
        SafetyBackupManifest manifest,
        string expectedProfileScope,
        string artifactId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(
                manifest.ContractVersion,
                SafetyBackupContract.ContractVersion,
                StringComparison.Ordinal) ||
            !string.Equals(manifest.Result, SafetyBackupContract.VerifiedResult, StringComparison.Ordinal) ||
            !string.Equals(manifest.ProfileScope, expectedProfileScope, StringComparison.Ordinal) ||
            !string.Equals(manifest.Artifact, artifactId, StringComparison.Ordinal))
        {
            throw new FormatException("The backup manifest identity is not valid.");
        }

        if (!Guid.TryParseExact(manifest.RunId, "D", out var runId) ||
            !string.Equals(runId.ToString("D"), manifest.RunId, StringComparison.Ordinal))
        {
            throw new FormatException("The backup manifest RunId is not canonical.");
        }

        ProtocolProfileScope.Validate(manifest.ProfileScope);
        ValidateSchema(manifest.SourceSchema, allowEmpty: true);
        ValidateSchema(manifest.TargetSchema, allowEmpty: false);
        if (!DateTimeOffset.TryParseExact(
                manifest.CreatedAtUtc,
                ManifestTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var createdAt) ||
            !string.Equals(
                createdAt.ToUniversalTime().ToString(
                    ManifestTimestampFormat,
                    CultureInfo.InvariantCulture),
                manifest.CreatedAtUtc,
                StringComparison.Ordinal))
        {
            throw new FormatException("The backup manifest timestamp is not canonical.");
        }

        if (manifest.ByteLength <= 0)
        {
            throw new FormatException("The backup manifest length is invalid.");
        }

        ValidateHash(manifest.Sha256);
        ValidateSourceFingerprint(manifest.SourceFingerprint);
    }

    private static void ValidateSchema(
        IReadOnlyList<string>? schema,
        bool allowEmpty)
    {
        if (schema is null || schema.Count > SafetyBackupContract.MaxSchemaEntries ||
            !allowEmpty && schema.Count == 0)
        {
            throw new FormatException("The backup manifest schema is invalid.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var migrationId in schema)
        {
            if (string.IsNullOrWhiteSpace(migrationId) ||
                migrationId == "empty" ||
                Encoding.UTF8.GetByteCount(migrationId) > SafetyBackupContract.MaxMigrationIdBytes ||
                migrationId.Any(character =>
                    character is not (>= 'a' and <= 'z') and
                        not (>= 'A' and <= 'Z') and
                        not (>= '0' and <= '9') and
                        not ('_' or '-' or '.')) ||
                !seen.Add(migrationId))
            {
                throw new FormatException("The backup manifest schema is invalid.");
            }
        }
    }

    private static void ValidateSourceFingerprint(SafetyBackupSourceFingerprint? fingerprint)
    {
        if (fingerprint is null || fingerprint.ByteLength <= 0)
        {
            throw new FormatException("The backup source fingerprint is invalid.");
        }

        ValidateHash(fingerprint.Sha256);
        ValidateHash(fingerprint.SchemaHistorySha256);
        ValidateSidecar(fingerprint.Wal);
        ValidateSidecar(fingerprint.Shm);
        ValidateSidecar(fingerprint.Journal);
    }

    private static void ValidateSidecar(SafetyBackupSidecarFingerprint? sidecar)
    {
        if (sidecar is null || sidecar.ByteLength < 0)
        {
            throw new FormatException("The backup source sidecar fingerprint is invalid.");
        }

        if (sidecar.Present)
        {
            if (sidecar.Sha256 is null)
            {
                throw new FormatException("A present backup source sidecar has no hash.");
            }

            ValidateHash(sidecar.Sha256);
        }
        else if (sidecar.ByteLength != 0 || sidecar.Sha256 is not null)
        {
            throw new FormatException("An absent backup source sidecar carries metadata.");
        }
    }

    private static void ValidateHash(string? value)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new FormatException("The backup hash is invalid.");
        }
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

    private static async ValueTask<FileHash> HashFileAsync(
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
        var before = stream.Length;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (before != stream.Length)
        {
            throw new IOException("The selected backup changed while it was read.");
        }

        return new(before, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private readonly record struct FileHash(long ByteLength, string Sha256);
}

/// <summary>
/// Executes the selected-backup restore path using the same Candidate,
/// forward-only migration, verification, promotion and marker adapters as
/// normal startup. It never writes a caller-supplied database path.
/// </summary>
public sealed class P275RecoveryService
{
    public static async ValueTask<P275RecoveryResult> RestoreAsync(
        P275RecoveryRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateRequest(request);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return P275RecoveryResult.Failed(P275MigrationFailureCodes.ArgumentsInvalid);
        }

        var paths = request.Paths;
        P275ProfileLayout layout;
        try
        {
            layout = new P275ProfileLayout(paths.ProfileRoot);
            layout.EnsureProfileRootForRead();
            paths.EnsureRuntimeDirectories();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException)
        {
            _ = exception;
            return P275RecoveryResult.Failed(P275MigrationFailureCodes.PathInvalid);
        }

        var statePort = new P275ProductionMigrationStatePort(layout);
        var operationId = Guid.CreateVersion7();
        var operationRunId = operationId.ToString("D");
        var markerRunId = operationRunId;
        var candidateRunId = Guid.CreateVersion7().ToString("D");
        P275RecoveryBackup? recoveryBackup = null;
        P275VerifiedSafetyBackup? backup = null;
        P275Candidate? candidate = null;
        IP275MigrationLockLease? migrationLock = null;
        IP275WriterQuiescenceLease? quiescence = null;
        var promoted = false;
        var phase = RecoveryPhase.Initial;

        try
        {
            if (!await TryRecordAsync(
                    statePort,
                    CreateSnapshot(
                        paths,
                        request.ProfileScope,
                        request.Plan,
                        markerRunId,
                        P275MigrationState.RecoveryInProgress,
                        request.Plan.ApprovedSourceMigrations,
                        backup,
                        candidate,
                        failureCode: null,
                        retryable: false,
                        nextAction: P275MigrationNextActions.ViewStatus,
                        agentInstanceId: request.AgentInstanceId),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275RecoveryResult.Failed(P275MigrationFailureCodes.StateUnwritable);
            }

            phase = RecoveryPhase.Lock;
            migrationLock = await new P275FileMigrationLockProvider()
                    .AcquireAsync(
                        new P275MigrationLockRequest(
                            paths,
                            operationRunId,
                            request.LockTimeout),
                        cancellationToken)
                    .ConfigureAwait(false);
            if (migrationLock is null)
            {
                return await FailAsync(
                        statePort,
                        paths,
                        request,
                        markerRunId,
                        recoveryBackup,
                        backup,
                        candidate,
                        promoted,
                        P275MigrationFailureCodes.Locked,
                        retryable: true,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            quiescence = await new P275FileWriterQuiescence()
                    .AcquireAsync(paths, operationRunId, cancellationToken)
                    .ConfigureAwait(false);
            if (quiescence is null)
            {
                return await FailAsync(
                        statePort,
                        paths,
                        request,
                        markerRunId,
                        recoveryBackup,
                        backup,
                        candidate,
                        promoted,
                        P275MigrationFailureCodes.RecoveryRequired,
                        retryable: false,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            phase = RecoveryPhase.Backup;
            recoveryBackup = await P275RecoveryBackupReader
                    .ReadAsync(
                        layout,
                        request.ProfileScope,
                        request.BackupArtifactId,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!recoveryBackup.Manifest.SourceSchema.SequenceEqual(
                    request.Plan.ApprovedSourceMigrations,
                    StringComparer.Ordinal) ||
                !recoveryBackup.Manifest.TargetSchema.SequenceEqual(
                    request.Plan.ApprovedTargetMigrations,
                    StringComparer.Ordinal))
            {
                return await FailAsync(
                        statePort,
                        paths,
                        request,
                        markerRunId,
                        recoveryBackup,
                        backup,
                        candidate,
                        promoted,
                        P275MigrationFailureCodes.IncompatibleSchema,
                        retryable: false,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            markerRunId = recoveryBackup.Manifest.RunId;
            backup = recoveryBackup.ToVerifiedBackup();
            if (!await TryRecordAsync(
                    statePort,
                    CreateSnapshot(
                        paths,
                        request.ProfileScope,
                        request.Plan,
                        markerRunId,
                        P275MigrationState.RecoveryInProgress,
                        recoveryBackup.Manifest.SourceSchema,
                        backup,
                        candidate,
                        failureCode: null,
                        retryable: false,
                        nextAction: P275MigrationNextActions.ViewStatus,
                        agentInstanceId: request.AgentInstanceId),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275RecoveryResult.Failed(P275MigrationFailureCodes.StateUnwritable);
            }

            phase = RecoveryPhase.Stage;
            candidate = await StageCandidateAsync(
                    paths,
                    backup,
                    candidateRunId,
                    cancellationToken)
                .ConfigureAwait(false);
            var baseline = await CaptureSourceBaselineAsync(
                    candidate.Files.CandidateDatabasePath,
                    recoveryBackup.Manifest.SourceSchema,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!await TryRecordAsync(
                    statePort,
                    CreateSnapshot(
                        paths,
                        request.ProfileScope,
                        request.Plan,
                        markerRunId,
                        P275MigrationState.RecoveryInProgress,
                        recoveryBackup.Manifest.SourceSchema,
                        backup,
                        candidate,
                        failureCode: null,
                        retryable: false,
                        nextAction: P275MigrationNextActions.ViewStatus,
                        agentInstanceId: request.AgentInstanceId),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275RecoveryResult.Failed(P275MigrationFailureCodes.StateUnwritable);
            }

            phase = RecoveryPhase.Apply;
            var apply = await new P275EfForwardMigrationApplier()
                    .ApplyAsync(candidate, request.Plan, request.ProfileScope, cancellationToken)
                    .ConfigureAwait(false);
            if (!apply.AppliedMigrations.SequenceEqual(
                    request.Plan.ApprovedTargetMigrations,
                    StringComparer.Ordinal))
            {
                return await FailAsync(
                        statePort,
                        paths,
                        request,
                        markerRunId,
                        recoveryBackup,
                        backup,
                        candidate,
                        promoted,
                        P275MigrationFailureCodes.ApplyFailed,
                        retryable: false,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            phase = RecoveryPhase.Verify;
            var finalized = await new P275CandidateSidecarFinalizer()
                    .FinalizeAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
            if (!finalized.IsValid)
            {
                return await FailAsync(
                        statePort,
                        paths,
                        request,
                        markerRunId,
                        recoveryBackup,
                        backup,
                        candidate,
                        promoted,
                        finalized.FailureCode ?? P275MigrationFailureCodes.VerifyFailed,
                        retryable: false,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var verifier = new P275ProductionCandidateVerifier();
            if (!await TryRecordAsync(
                    statePort,
                    CreateSnapshot(
                        paths,
                        request.ProfileScope,
                        request.Plan,
                        markerRunId,
                        P275MigrationState.VerifyInProgress,
                        recoveryBackup.Manifest.SourceSchema,
                        backup,
                        candidate,
                        failureCode: null,
                        retryable: false,
                        nextAction: P275MigrationNextActions.ViewStatus,
                        agentInstanceId: request.AgentInstanceId),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275RecoveryResult.Failed(P275MigrationFailureCodes.StateUnwritable);
            }

            var candidateVerification = await verifier.VerifyAsync(
                    new P275VerificationRequest(
                        candidate,
                        request.Plan,
                        P275VerificationPhase.Candidate,
                        candidate.Files.CandidateDatabasePath,
                        paths,
                        baseline,
                        candidateRunId),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!candidateVerification.IsValid)
            {
                return await FailAsync(
                        statePort,
                        paths,
                        request,
                        markerRunId,
                        recoveryBackup,
                        backup,
                        candidate,
                        promoted,
                        candidateVerification.FailureCode ?? P275MigrationFailureCodes.VerifyFailed,
                        retryable: false,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            phase = RecoveryPhase.Promote;
            if (!await TryRecordAsync(
                    statePort,
                    CreateSnapshot(
                        paths,
                        request.ProfileScope,
                        request.Plan,
                        markerRunId,
                        P275MigrationState.PromotionInProgress,
                        recoveryBackup.Manifest.SourceSchema,
                        backup,
                        candidate,
                        failureCode: null,
                        retryable: false,
                        nextAction: P275MigrationNextActions.ViewStatus,
                        agentInstanceId: request.AgentInstanceId),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275RecoveryResult.Failed(P275MigrationFailureCodes.StateUnwritable);
            }

            var source = new P275SourceInventory(
                exists: true,
                recoveryBackup.Manifest.SourceSchema,
                new P275SourceFingerprint(
                    P275SourceFingerprintCodec.Encode(recoveryBackup.Manifest.SourceFingerprint)),
                baseline);
            var promotion = await new P275AtomicFilePromoter()
                    .PromoteAsync(
                        new P275PromotionRequest(paths, candidate, source, request.ProfileScope),
                        cancellationToken)
                    .ConfigureAwait(false);
            if (promotion.Outcome == P275PromotionOutcome.Unknown)
            {
                return await FailAsync(
                        statePort,
                        paths,
                        request,
                        markerRunId,
                        recoveryBackup,
                        backup,
                        candidate,
                        promoted,
                        P275MigrationFailureCodes.PromoteUnknown,
                        retryable: false,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (promotion.Outcome != P275PromotionOutcome.Succeeded)
            {
                return await FailAsync(
                        statePort,
                        paths,
                        request,
                        markerRunId,
                        recoveryBackup,
                        backup,
                        candidate,
                        promoted,
                        promotion.FailureCode ?? P275MigrationFailureCodes.PromoteFailed,
                        retryable: false,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            promoted = true;
            phase = RecoveryPhase.PostPromoteVerify;
            if (!await TryRecordAsync(
                    statePort,
                    CreateSnapshot(
                        paths,
                        request.ProfileScope,
                        request.Plan,
                        markerRunId,
                        P275MigrationState.VerifyInProgress,
                        recoveryBackup.Manifest.SourceSchema,
                        backup,
                        candidate: null,
                        failureCode: null,
                        retryable: false,
                        nextAction: P275MigrationNextActions.ViewStatus,
                        agentInstanceId: request.AgentInstanceId),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275RecoveryResult.Failed(P275MigrationFailureCodes.StateUnwritable);
            }

            var postPromoteVerification = await verifier.VerifyAsync(
                    new P275VerificationRequest(
                        candidate,
                        request.Plan,
                        P275VerificationPhase.PostPromote,
                        paths.ActiveDatabasePath,
                        paths,
                        baseline,
                        candidateRunId),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!postPromoteVerification.IsValid)
            {
                return await FailAsync(
                        statePort,
                        paths,
                        request,
                        markerRunId,
                        recoveryBackup,
                        backup,
                        candidate,
                        promoted,
                        postPromoteVerification.FailureCode ?? P275MigrationFailureCodes.VerifyFailed,
                        retryable: false,
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (!await TryRecordAsync(
                    statePort,
                    CreateSnapshot(
                        paths,
                        request.ProfileScope,
                        request.Plan,
                        markerRunId,
                        P275MigrationState.Ready,
                        recoveryBackup.Manifest.SourceSchema,
                        backup,
                        candidate: null,
                        failureCode: null,
                        retryable: false,
                        nextAction: P275MigrationNextActions.None,
                        agentInstanceId: request.AgentInstanceId),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275RecoveryResult.Failed(P275MigrationFailureCodes.StateUnwritable);
            }

            return P275RecoveryResult.Success();
        }
        catch (OperationCanceledException)
        {
            return await FailAsync(
                    statePort,
                    paths,
                    request,
                    markerRunId,
                    recoveryBackup,
                    backup,
                    candidate,
                    promoted,
                    P275MigrationFailureCodes.RestoreFailed,
                    retryable: false,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (P275MigrationOperationException exception)
        {
            return await FailAsync(
                    statePort,
                    paths,
                    request,
                    markerRunId,
                    recoveryBackup,
                    backup,
                    candidate,
                    promoted,
                    exception.FailureCode,
                    retryable: false,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var failureCode = phase switch
            {
                RecoveryPhase.Backup => P275MigrationFailureCodes.RecoveryBackupInvalid,
                RecoveryPhase.Apply => P275MigrationFailureCodes.ApplyFailed,
                RecoveryPhase.Promote => P275MigrationFailureCodes.PromoteUnknown,
                RecoveryPhase.Verify or RecoveryPhase.PostPromoteVerify => P275MigrationFailureCodes.VerifyFailed,
                _ => P275MigrationFailureCodes.RestoreFailed
            };
            _ = exception;
            return await FailAsync(
                    statePort,
                    paths,
                    request,
                    markerRunId,
                    recoveryBackup,
                    backup,
                    candidate,
                    promoted,
                    failureCode,
                    retryable: false,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            if (quiescence is not null)
            {
                try
                {
                    await quiescence.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (migrationLock is not null)
            {
                try
                {
                    await migrationLock.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static async ValueTask<P275RecoveryResult> FailAsync(
        P275ProductionMigrationStatePort statePort,
        P275ProfilePaths paths,
        P275RecoveryRestoreRequest request,
        string markerRunId,
        P275RecoveryBackup? recoveryBackup,
        P275VerifiedSafetyBackup? backup,
        P275Candidate? candidate,
        bool promoted,
        string failureCode,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var normalizedCode = P275MigrationFailureCodes.IsStable(failureCode)
            ? failureCode
            : P275MigrationFailureCodes.RestoreFailed;
        string? candidatePath = null;
        if (!promoted && candidate is not null)
        {
            candidatePath = PreserveCandidate(candidate);
        }

        var sourceSchema = recoveryBackup?.Manifest.SourceSchema ?? request.Plan.ApprovedSourceMigrations;
        var snapshot = CreateSnapshot(
            paths,
            request.ProfileScope,
            request.Plan,
            markerRunId,
            P275MigrationState.RecoveryFailed,
            sourceSchema,
            backup,
            promoted ? null : candidate,
            normalizedCode,
            retryable,
            retryable
                ? P275MigrationNextActions.Retry
                : P275MigrationNextActions.RestoreBackup,
            request.AgentInstanceId,
            candidatePath);
        try
        {
            await statePort.RecordAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return P275RecoveryResult.Failed(P275MigrationFailureCodes.StateUnwritable);
        }

        return P275RecoveryResult.Failed(normalizedCode);
    }

    private static P275MigrationStateSnapshot CreateSnapshot(
        P275ProfilePaths paths,
        string profileScope,
        P275MigrationPlan plan,
        string runId,
        P275MigrationState state,
        IReadOnlyList<string> sourceSchema,
        P275VerifiedSafetyBackup? backup,
        P275Candidate? candidate,
        string? failureCode,
        bool retryable,
        string nextAction,
        string? agentInstanceId,
        string? candidatePathOverride = null)
    {
        var candidatePath = candidatePathOverride ?? candidate?.Files.CandidateDatabasePath;
        string? candidateArtifact = null;
        if (candidatePath is not null)
        {
            try
            {
                candidateArtifact = paths.GetProfileRelativeArtifact(candidatePath);
            }
            catch (ArgumentException)
            {
            }
        }

        return new P275MigrationStateSnapshot(
            runId,
            profileScope,
            state,
            sourceSchema,
            plan.ApprovedTargetMigrations,
            backup?.ArtifactId,
            candidateArtifact,
            failureCode,
            retryable,
            nextAction,
            backup?.Sha256,
            LastAgentInstanceId: agentInstanceId);
    }

    private static async ValueTask<bool> TryRecordAsync(
        P275ProductionMigrationStatePort statePort,
        P275MigrationStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await statePort.RecordAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async ValueTask<P275Candidate> StageCandidateAsync(
        P275ProfilePaths paths,
        P275VerifiedSafetyBackup backup,
        string candidateRunId,
        CancellationToken cancellationToken)
    {
        var files = paths.CreateCandidatePaths(candidateRunId);
        EnsureDirectory(files.StagingDirectory);
        if (Directory.EnumerateFileSystemEntries(files.StagingDirectory).Any())
        {
            throw new P275MigrationOperationException(P275MigrationFailureCodes.RestoreFailed);
        }

        var temporaryPath = files.CandidateDatabasePath + ".partial";
        if (P275FileSafety.PathExists(temporaryPath) ||
            P275FileSafety.PathExists(files.CandidateDatabasePath))
        {
            throw new P275MigrationOperationException(P275MigrationFailureCodes.RestoreFailed);
        }

        try
        {
            await using (var source = new FileStream(
                             backup.ArtifactPath!,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.Open,
                                 Access = FileAccess.Read,
                                 Share = FileShare.Read,
                                 BufferSize = 64 * 1024,
                                 Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                             }))
            await using (var destination = new FileStream(
                             temporaryPath,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.CreateNew,
                                 Access = FileAccess.Write,
                                 Share = FileShare.None,
                                 BufferSize = 64 * 1024,
                                 Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                                 PreallocationSize = backup.ByteLength
                             }))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, files.CandidateDatabasePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new P275MigrationOperationException(
                P275MigrationFailureCodes.RestoreFailed,
                exception);
        }

        if (!P275FileSafety.IsRegularFile(files.CandidateDatabasePath) ||
            P275CandidateSidecarFinalizer.EnumerateSidecars(files.CandidateDatabasePath).Count != 0)
        {
            throw new P275MigrationOperationException(P275MigrationFailureCodes.RestoreFailed);
        }

        var actual = await HashFileAsync(files.CandidateDatabasePath, cancellationToken)
            .ConfigureAwait(false);
        if (actual.ByteLength != backup.ByteLength ||
            !string.Equals(actual.Sha256, backup.Sha256, StringComparison.Ordinal))
        {
            throw new P275MigrationOperationException(P275MigrationFailureCodes.RestoreFailed);
        }

        return new P275Candidate(paths, files, backup);
    }

    private static async ValueTask<P275VerificationBaseline> CaptureSourceBaselineAsync(
        string databasePath,
        IReadOnlyList<string> expectedMigrations,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(
                connection,
                "PRAGMA foreign_keys = ON; PRAGMA query_only = ON;",
                cancellationToken)
            .ConfigureAwait(false);
        if (await ExecuteScalarLongAsync(connection, "PRAGMA foreign_keys;", cancellationToken)
                .ConfigureAwait(false) != 1 ||
            await ExecuteScalarLongAsync(connection, "PRAGMA query_only;", cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            throw new P275MigrationOperationException(P275MigrationFailureCodes.RestoreFailed);
        }

        var history = await ReadHistoryAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!history.SequenceEqual(expectedMigrations, StringComparer.Ordinal))
        {
            throw new P275MigrationOperationException(P275MigrationFailureCodes.RecoveryBackupInvalid);
        }

        await VerifyIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
        var keyTables = new List<P275KeyTableExpectation>();
        foreach (var (table, columns) in PreservationTables)
        {
            if (await TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false))
            {
                keyTables.Add(
                    await P275CandidateVerifier.CaptureKeyTableExpectationAsync(
                            connection,
                            table,
                            columns,
                            cancellationToken)
                        .ConfigureAwait(false));
            }
        }

        return new P275VerificationBaseline(keyTables);
    }

    private static readonly (string Table, string[] Columns)[] PreservationTables =
    [
        ("tasks", ["id"]),
        ("task_history", ["id"]),
        ("app_settings", ["id"])
    ];

    private static async ValueTask<IReadOnlyList<string>> ReadHistoryAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM \"__EFMigrationsHistory\" ORDER BY rowid;";
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var result = new List<string>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0) || result.Count >= SafetyBackupContract.MaxSchemaEntries)
                {
                    throw new FormatException("The source history is invalid.");
                }

                result.Add(reader.GetString(0));
            }

            return result;
        }
        catch (SqliteException exception) when (
            exception.SqliteErrorCode == 1 &&
            exception.Message.Contains("__EFMigrationsHistory", StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }
    }

    private static async ValueTask VerifyIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        var result = await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                Convert.ToString(result, CultureInfo.InvariantCulture),
                "ok",
                StringComparison.Ordinal))
        {
            throw new P275MigrationOperationException(P275MigrationFailureCodes.IntegrityFailed);
        }

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new P275MigrationOperationException(P275MigrationFailureCodes.ForeignKeyFailed);
        }
    }

    private static async ValueTask<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async ValueTask<int> ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<FileHash> HashFileAsync(
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
        var before = stream.Length;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (before != stream.Length)
        {
            throw new IOException("The Candidate changed while it was read.");
        }

        return new(before, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static void EnsureDirectory(string path)
    {
        if (P275FileSafety.PathExists(path) && !Directory.Exists(path))
        {
            throw new IOException("The recovery directory is not a directory.");
        }

        Directory.CreateDirectory(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The recovery directory is a reparse point.");
        }
    }

    private static string? PreserveCandidate(P275Candidate candidate)
    {
        if (!Directory.Exists(candidate.Files.StagingDirectory))
        {
            return Path.Combine(candidate.Files.StagingDirectory, "candidate.sqlite");
        }

        if (Directory.Exists(candidate.Files.FailedDirectory))
        {
            return Path.Combine(candidate.Files.StagingDirectory, "candidate.sqlite");
        }

        try
        {
            EnsureDirectory(Path.GetDirectoryName(candidate.Files.FailedDirectory)!);
            Directory.Move(candidate.Files.StagingDirectory, candidate.Files.FailedDirectory);
            return Path.Combine(candidate.Files.FailedDirectory, "candidate.sqlite");
        }
        catch (IOException)
        {
            return Path.Combine(candidate.Files.StagingDirectory, "candidate.sqlite");
        }
        catch (UnauthorizedAccessException)
        {
            return Path.Combine(candidate.Files.StagingDirectory, "candidate.sqlite");
        }
    }

    private static void ValidateRequest(P275RecoveryRestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Paths);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ProtocolProfileScope.Validate(request.ProfileScope);
        P275ArtifactNames.ValidateBackupArtifactId(request.BackupArtifactId);
        if (!Guid.TryParseExact(request.AgentInstanceId, "D", out var agentInstanceId) &&
            request.AgentInstanceId is not null)
        {
            throw new ArgumentException("The Agent instance ID is invalid.", nameof(request));
        }

        if (request.AgentInstanceId is not null &&
            (agentInstanceId == Guid.Empty ||
             !string.Equals(agentInstanceId.ToString("D"), request.AgentInstanceId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The Agent instance ID is invalid.", nameof(request));
        }

        if (!request.Paths.ProfileRoot.Equals(
                Path.GetFullPath(request.Paths.ProfileRoot),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal) ||
            request.LockTimeout <= TimeSpan.Zero ||
            request.LockTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentException("The recovery request is invalid.", nameof(request));
        }
    }

    private static bool TryGetExistingCandidatePath(P275Candidate? candidate, out string? path)
    {
        path = candidate?.Files.CandidateDatabasePath;
        return path is not null;
    }

    private readonly record struct FileHash(long ByteLength, string Sha256);

    private enum RecoveryPhase
    {
        Initial,
        Lock,
        Backup,
        Stage,
        Apply,
        Verify,
        Promote,
        PostPromoteVerify
    }
}
