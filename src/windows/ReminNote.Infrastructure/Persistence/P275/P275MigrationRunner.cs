using System.Security.Cryptography;

namespace ReminNote.Infrastructure.Persistence.P275;

/// <summary>
/// Implements the P2.75 migration attempt state machine. P2.75-01 and
/// P2.75-03 provide the backup, source-inventory, verification and marker
/// adapters; this class only sequences their capabilities and owns the
/// Candidate/promote boundary.
/// </summary>
public sealed class P275MigrationRunner : IP275MigrationRunner
{
    private readonly IP275ActiveSourceReader sourceReader;
    private readonly IP275SafetyBackupProvider backupProvider;
    private readonly IP275WriterQuiescence writerQuiescence;
    private readonly IP275MigrationLockProvider lockProvider;
    private readonly IP275ForwardMigrationApplier migrationApplier;
    private readonly IP275CandidateFinalizer candidateFinalizer;
    private readonly IP275CandidateVerifier candidateVerifier;
    private readonly IP275AtomicPromoter promoter;
    private readonly IP275MigrationStatePort statePort;

    public P275MigrationRunner(
        IP275ActiveSourceReader sourceReader,
        IP275SafetyBackupProvider backupProvider,
        IP275WriterQuiescence writerQuiescence,
        IP275MigrationLockProvider lockProvider,
        IP275ForwardMigrationApplier migrationApplier,
        IP275CandidateFinalizer candidateFinalizer,
        IP275CandidateVerifier candidateVerifier,
        IP275AtomicPromoter promoter,
        IP275MigrationStatePort statePort)
    {
        this.sourceReader = sourceReader ?? throw new ArgumentNullException(nameof(sourceReader));
        this.backupProvider = backupProvider ?? throw new ArgumentNullException(nameof(backupProvider));
        this.writerQuiescence = writerQuiescence ?? throw new ArgumentNullException(nameof(writerQuiescence));
        this.lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
        this.migrationApplier = migrationApplier ?? throw new ArgumentNullException(nameof(migrationApplier));
        this.candidateFinalizer = candidateFinalizer ?? throw new ArgumentNullException(nameof(candidateFinalizer));
        this.candidateVerifier = candidateVerifier ?? throw new ArgumentNullException(nameof(candidateVerifier));
        this.promoter = promoter ?? throw new ArgumentNullException(nameof(promoter));
        this.statePort = statePort ?? throw new ArgumentNullException(nameof(statePort));
    }

    public async ValueTask<P275MigrationResult> RunAsync(
        P275MigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var paths = request.Paths;
        var plan = request.Plan;
        var runId = request.RunId;
        P275SourceInventory? source = null;
        P275VerifiedSafetyBackup? backup = null;
        P275Candidate? candidate = null;
        IP275MigrationLockLease? migrationLock = null;
        IP275WriterQuiescenceLease? quiescence = null;
        var promoted = false;
        string? historyDirectoryPath = null;
        var currentPhase = P275MigrationState.MigrationRequired;

        try
        {
            paths.EnsureRuntimeDirectories();
            migrationLock = await lockProvider.AcquireAsync(
                    new P275MigrationLockRequest(paths, runId, request.LockTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (migrationLock is null)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.MigrationRequired,
                        P275MigrationFailureCodes.Locked,
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }

            var previousAttempt = await GuardPreviousAttemptAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            if (previousAttempt is not null)
            {
                return previousAttempt;
            }

            if (!await TryRecordAsync(
                    CreateSnapshot(request, currentPhase, failureCode: null, backup, candidate),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275MigrationResult.Failure(
                    runId,
                    P275MigrationState.RecoveryRequired,
                    P275MigrationFailureCodes.StateUnwritable);
            }

            quiescence = await writerQuiescence
                .AcquireAsync(paths, runId, cancellationToken)
                .ConfigureAwait(false);
            if (quiescence is null)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.RecoveryRequired,
                        P275MigrationFailureCodes.RecoveryRequired,
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }

            try
            {
                source = await ReadSourceAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (P275SourceInventoryException)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.RecoveryRequired,
                        P275MigrationFailureCodes.SourceInvalid,
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }
            var sourceIsApproved = source.AppliedMigrations.SequenceEqual(
                    plan.ApprovedSourceMigrations,
                    StringComparer.Ordinal) ||
                plan.IsAlreadyAtTarget(source.AppliedMigrations);
            if (!sourceIsApproved)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.RecoveryRequired,
                        P275MigrationFailureCodes.IncompatibleSchema,
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }

            if (plan.IsAlreadyAtTarget(source.AppliedMigrations))
            {
                currentPhase = P275MigrationState.VerifyInProgress;
                if (!await TryRecordAsync(
                        CreateSnapshot(request, currentPhase, failureCode: null, backup, candidate, source),
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return P275MigrationResult.Failure(
                        runId,
                        P275MigrationState.RecoveryRequired,
                        P275MigrationFailureCodes.StateUnwritable);
                }

                var activeVerification = await VerifyAsync(
                        request,
                        candidate,
                        P275VerificationPhase.AlreadyReady,
                        paths.ActiveDatabasePath,
                        plan,
                        source.VerificationBaseline,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!activeVerification.IsValid)
                {
                    return await FailAsync(
                            request,
                            P275MigrationState.VerifyFailed,
                            NormalizeVerificationCode(activeVerification.FailureCode),
                            source,
                            backup,
                            candidate,
                            promoted,
                            historyDirectoryPath)
                        .ConfigureAwait(false);
                }

                return await ReadyAsync(request, source, backup, candidate, historyDirectoryPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            currentPhase = P275MigrationState.BackupInProgress;
            if (!await TryRecordAsync(
                    CreateSnapshot(request, currentPhase, failureCode: null, backup, candidate, source),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275MigrationResult.Failure(
                    runId,
                    P275MigrationState.RecoveryRequired,
                    P275MigrationFailureCodes.StateUnwritable);
            }

            try
            {
                backup = await backupProvider.CreateVerifiedBackupAsync(
                        new P275SafetyBackupRequest(paths, request.ProfileScope, runId, source, plan),
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateVerifiedBackup(paths, source, backup);
            }
            catch (P275MigrationOperationException exception)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.BackupFailed,
                        exception.FailureCode,
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }
            catch (P275BackupValidationException)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.BackupFailed,
                        P275MigrationFailureCodes.BackupUnreadable,
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.BackupFailed,
                        P275MigrationFailureCodes.BackupFailed,
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }

            try
            {
                candidate = await StageCandidateAsync(paths, backup, runId, cancellationToken)
                    .ConfigureAwait(false);
                ValidateStagedCandidate(candidate);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.BackupFailed,
                        P275MigrationFailureCodes.BackupFailed,
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }

            currentPhase = P275MigrationState.MigrationInProgress;
            if (!await TryRecordAsync(
                    CreateSnapshot(request, currentPhase, failureCode: null, backup, candidate, source),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275MigrationResult.Failure(
                    runId,
                    P275MigrationState.RecoveryRequired,
                    P275MigrationFailureCodes.StateUnwritable,
                    backup.ArtifactId,
                    candidate.Files.CandidateDatabasePath);
            }

            try
            {
                var applyResult = await migrationApplier.ApplyAsync(
                        candidate,
                        plan,
                        request.ProfileScope,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!applyResult.AppliedMigrations.SequenceEqual(
                        plan.ApprovedTargetMigrations,
                        StringComparer.Ordinal))
                {
                    return await FailAsync(
                            request,
                            P275MigrationState.MigrationFailed,
                            P275MigrationFailureCodes.HistoryFailed,
                            source,
                            backup,
                            candidate,
                            promoted,
                            historyDirectoryPath)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.MigrationFailed,
                        P275MigrationFailureCodes.ApplyFailed,
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }

            P275CandidateFinalizationResult finalization;
            try
            {
                finalization = await candidateFinalizer
                    .FinalizeAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                finalization = P275CandidateFinalizationResult.Invalid();
            }

            if (!finalization.IsValid)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.VerifyFailed,
                        NormalizeVerificationCode(finalization.FailureCode),
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }

            currentPhase = P275MigrationState.VerifyInProgress;
            if (!await TryRecordAsync(
                    CreateSnapshot(request, currentPhase, failureCode: null, backup, candidate, source),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275MigrationResult.Failure(
                    runId,
                    P275MigrationState.RecoveryRequired,
                    P275MigrationFailureCodes.StateUnwritable,
                    backup.ArtifactId,
                    candidate.Files.CandidateDatabasePath);
            }

            var candidateVerification = await VerifyAsync(
                    request,
                    candidate,
                    P275VerificationPhase.Candidate,
                    candidate.Files.CandidateDatabasePath,
                    plan,
                    source.VerificationBaseline,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!candidateVerification.IsValid)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.VerifyFailed,
                        NormalizeVerificationCode(candidateVerification.FailureCode),
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }

            P275SourceInventory sourceBeforePromote;
            try
            {
                sourceBeforePromote = await ReadSourceAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.RecoveryRequired,
                        P275MigrationFailureCodes.SourceInvalid,
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }

            if (!HasSameSource(source, sourceBeforePromote))
            {
                return await FailAsync(
                        request,
                        P275MigrationState.RecoveryRequired,
                        P275MigrationFailureCodes.SourceChanged,
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }

            currentPhase = P275MigrationState.PromotionInProgress;
            if (!await TryRecordAsync(
                    CreateSnapshot(request, currentPhase, failureCode: null, backup, candidate, source),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275MigrationResult.Failure(
                    runId,
                    P275MigrationState.RecoveryRequired,
                    P275MigrationFailureCodes.StateUnwritable,
                    backup.ArtifactId,
                    candidate.Files.CandidateDatabasePath);
            }

            P275PromotionResult promotion;
            try
            {
                promotion = await promoter.PromoteAsync(
                        new P275PromotionRequest(paths, candidate, source, request.ProfileScope),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                promotion = P275PromotionResult.Unknown();
            }

            switch (promotion.Outcome)
            {
                case P275PromotionOutcome.ExplicitFailure:
                    return await FailAsync(
                            request,
                            P275MigrationState.RecoveryRequired,
                            NormalizePromotionCode(promotion.FailureCode, P275MigrationFailureCodes.PromoteFailed),
                            source,
                            backup,
                            candidate,
                            promoted,
                            promotion.HistoryDirectoryPath)
                        .ConfigureAwait(false);
                case P275PromotionOutcome.Unknown:
                    return await FailAsync(
                            request,
                            P275MigrationState.PromotionUnknown,
                            P275MigrationFailureCodes.PromoteUnknown,
                            source,
                            backup,
                            candidate,
                            promoted,
                            promotion.HistoryDirectoryPath)
                        .ConfigureAwait(false);
                case P275PromotionOutcome.Succeeded:
                    promoted = true;
                    historyDirectoryPath = promotion.HistoryDirectoryPath;
                    break;
                default:
                    return await FailAsync(
                            request,
                            P275MigrationState.PromotionUnknown,
                            P275MigrationFailureCodes.PromoteUnknown,
                            source,
                            backup,
                            candidate,
                            promoted,
                            historyDirectoryPath)
                        .ConfigureAwait(false);
            }

            currentPhase = P275MigrationState.VerifyInProgress;
            if (!await TryRecordAsync(
                    CreateSnapshot(request, currentPhase, failureCode: null, backup, candidate, source),
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return P275MigrationResult.Failure(
                    runId,
                    P275MigrationState.RecoveryRequired,
                    P275MigrationFailureCodes.StateUnwritable,
                    backup.ArtifactId,
                    candidate.Files.CandidateDatabasePath,
                    historyDirectoryPath,
                    promoted: true);
            }

            var postPromoteVerification = await VerifyAsync(
                    request,
                    candidate,
                    P275VerificationPhase.PostPromote,
                    paths.ActiveDatabasePath,
                    plan,
                    source.VerificationBaseline,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!postPromoteVerification.IsValid)
            {
                return await FailAsync(
                        request,
                        P275MigrationState.VerifyFailed,
                        NormalizeVerificationCode(postPromoteVerification.FailureCode),
                        source,
                        backup,
                        candidate,
                        promoted,
                        historyDirectoryPath)
                    .ConfigureAwait(false);
            }

            return await ReadyAsync(request, source, backup, candidate, historyDirectoryPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await FailAsync(
                    request,
                    StateForException(currentPhase),
                    FailureCodeForException(currentPhase),
                    source,
                    backup,
                    candidate,
                    promoted,
                    historyDirectoryPath)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return await FailAsync(
                    request,
                    StateForException(currentPhase),
                    FailureCodeForException(currentPhase),
                    source,
                    backup,
                    candidate,
                    promoted,
                    historyDirectoryPath)
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
                    // The migration result has already been made fail-closed;
                    // a quiescence adapter must report cleanup failures to its
                    // own diagnostics without changing the result to READY.
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
                    // The lease is best-effort cleanup after the state result;
                    // never mask the already fail-closed startup decision.
                }
            }
        }
    }

    private async ValueTask<P275MigrationResult?> GuardPreviousAttemptAsync(
        P275MigrationRequest request,
        CancellationToken cancellationToken)
    {
        P275MigrationStateReadResult previousRead;
        try
        {
            previousRead = await statePort
                .ReadAsync(request.ProfileScope, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return P275MigrationResult.Failure(
                request.RunId,
                P275MigrationState.RecoveryRequired,
                P275MigrationFailureCodes.StateUnwritable);
        }

        if (previousRead.Status == P275MarkerReadStatus.Missing)
        {
            return null;
        }

        if (!previousRead.IsUsable || previousRead.Marker is null)
        {
            return P275MigrationResult.Failure(
                request.RunId,
                P275MigrationState.RecoveryRequired,
                P275MigrationFailureCodes.RecoveryRequired);
        }

        var previous = previousRead.Marker;
        if (previous.State == P275MigrationState.Ready)
        {
            return null;
        }

        if (request.AllowRetry && IsExplicitRetryAllowed(previous))
        {
            return null;
        }

        if (previous.State == P275MigrationState.PromotionInProgress)
        {
            var recorded = await TryRecordAsync(
                    SnapshotFromPrevious(
                        previous,
                        P275MigrationState.PromotionUnknown,
                        P275MigrationFailureCodes.PromoteUnknown,
                        retryable: false,
                        P275MigrationNextActions.RestoreBackup),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!recorded)
            {
                return P275MigrationResult.Failure(
                    request.RunId,
                    P275MigrationState.RecoveryRequired,
                    P275MigrationFailureCodes.StateUnwritable);
            }

            return P275MigrationResult.Failure(
                previous.RunId.ToString("D"),
                P275MigrationState.PromotionUnknown,
                P275MigrationFailureCodes.PromoteUnknown,
                previous.BackupArtifact);
        }

        if (previous.State is
                P275MigrationState.BackupInProgress or
                P275MigrationState.MigrationInProgress or
                P275MigrationState.VerifyInProgress or
                P275MigrationState.RecoveryInProgress ||
            previous.State == P275MigrationState.MigrationRequired &&
                previous.FailureCode is null)
        {
            var recorded = await TryRecordAsync(
                    SnapshotFromPrevious(
                        previous,
                        P275MigrationState.RecoveryRequired,
                        P275MigrationFailureCodes.RecoveryRequired,
                        retryable: false,
                        P275MigrationNextActions.ViewStatus),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!recorded)
            {
                return P275MigrationResult.Failure(
                    request.RunId,
                    P275MigrationState.RecoveryRequired,
                    P275MigrationFailureCodes.StateUnwritable);
            }

            return P275MigrationResult.Failure(
                previous.RunId.ToString("D"),
                P275MigrationState.RecoveryRequired,
                P275MigrationFailureCodes.RecoveryRequired,
                previous.BackupArtifact);
        }

        return P275MigrationResult.Failure(
            previous.RunId.ToString("D"),
            previous.State,
            previous.FailureCode ?? P275MigrationFailureCodes.RecoveryRequired,
            previous.BackupArtifact);
    }

    private static bool IsExplicitRetryAllowed(P275MigrationStateMarker marker) =>
        marker.State is
            (P275MigrationState.MigrationRequired or
            P275MigrationState.BackupFailed or
            P275MigrationState.MigrationFailed or
            P275MigrationState.RecoveryRequired) &&
        marker.Retryable &&
        marker.NextAction == P275MigrationNextActions.Retry;

    private static P275MigrationStateSnapshot SnapshotFromPrevious(
        P275MigrationStateMarker previous,
        P275MigrationState state,
        string failureCode,
        bool retryable,
        string nextAction) =>
        new(
            previous.RunId.ToString("D"),
            previous.ProfileScope,
            state,
            previous.SourceSchema,
            previous.TargetSchema,
            previous.BackupArtifact,
            previous.CandidateArtifact,
            failureCode,
            retryable,
            nextAction,
            previous.BackupSha256,
            previous.StartedAtUtc,
            DateTimeOffset.UtcNow,
            previous.LastAgentInstanceId);

    private async ValueTask<P275SourceInventory> ReadSourceAsync(
        P275MigrationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var source = await sourceReader
                .ReadAsync(request.Paths, request.ProfileScope, cancellationToken)
                .ConfigureAwait(false);
            return source ?? throw new P275SourceInventoryException();
        }
        catch (P275SourceInventoryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new P275SourceInventoryException(exception);
        }
    }

    private async ValueTask<P275VerificationResult> VerifyAsync(
        P275MigrationRequest request,
        P275Candidate? candidate,
        P275VerificationPhase phase,
        string databasePath,
        P275MigrationPlan plan,
        P275VerificationBaseline? baseline,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await candidateVerifier
                .VerifyAsync(
                    new P275VerificationRequest(
                        candidate,
                        plan,
                        phase,
                        databasePath,
                        request.Paths,
                        baseline,
                        request.RunId),
                    cancellationToken)
                .ConfigureAwait(false);
            return result ?? P275VerificationResult.Invalid();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = request;
            return P275VerificationResult.Invalid();
        }
    }

    private static async ValueTask<P275Candidate> StageCandidateAsync(
        P275ProfilePaths paths,
        P275VerifiedSafetyBackup backup,
        string runId,
        CancellationToken cancellationToken)
    {
        var files = paths.CreateCandidatePaths(runId);
        if (Directory.Exists(files.StagingDirectory) &&
            Directory.EnumerateFileSystemEntries(files.StagingDirectory).Any())
        {
            throw new IOException("The migration staging directory is already occupied.");
        }

        Directory.CreateDirectory(files.StagingDirectory);
        var temporaryPath = files.CandidateDatabasePath + ".partial";
        if (File.Exists(temporaryPath) || File.Exists(files.CandidateDatabasePath))
        {
            throw new IOException("The migration Candidate path is already occupied.");
        }

        try
        {
            if (backup.SourceIsEmpty)
            {
                await using var empty = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.Asynchronous | FileOptions.WriteThrough);
                await empty.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var source = new FileStream(
                    backup.ArtifactPath!,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.WriteThrough);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, files.CandidateDatabasePath);
        }
        catch
        {
            // Keep any partial artifact in the per-Run staging directory. The
            // caller moves that directory to failed/<RunId> as evidence.
            throw;
        }

        return new P275Candidate(paths, files, backup);
    }

    private static void ValidateStagedCandidate(P275Candidate candidate)
    {
        if (!P275FileSafety.IsRegularFile(candidate.Files.CandidateDatabasePath) ||
            P275CandidateSidecarFinalizer.EnumerateSidecars(candidate.Files.CandidateDatabasePath).Count != 0)
        {
            throw new IOException("The staged Candidate is not a standalone regular file.");
        }

        var fileInfo = new FileInfo(candidate.Files.CandidateDatabasePath);
        if (candidate.Backup.SourceIsEmpty)
        {
            if (fileInfo.Length != 0)
            {
                throw new IOException("The staged empty Candidate is not empty.");
            }

            return;
        }

        if (fileInfo.Length != candidate.Backup.ByteLength)
        {
            throw new IOException("The staged Candidate length differs from the verified backup.");
        }

        using var stream = new FileStream(
            candidate.Files.CandidateDatabasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualHash, candidate.Backup.Sha256, StringComparison.Ordinal))
        {
            throw new IOException("The staged Candidate hash differs from the verified backup.");
        }
    }

    private static void ValidateVerifiedBackup(
        P275ProfilePaths paths,
        P275SourceInventory source,
        P275VerifiedSafetyBackup backup)
    {
        if (backup.SourceIsEmpty != !source.Exists ||
            !backup.SourceMigrations.SequenceEqual(source.AppliedMigrations, StringComparer.Ordinal) ||
            !string.Equals(backup.SourceFingerprint.Value, source.Fingerprint.Value, StringComparison.Ordinal))
        {
            throw new P275BackupValidationException();
        }

        if (backup.SourceIsEmpty)
        {
            return;
        }

        var backupPath = backup.ArtifactPath!;
        var expectedBackupPath = Path.GetFullPath(
            Path.Combine(paths.BackupsDirectory, backup.ArtifactId));
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                Path.GetFullPath(backupPath),
                expectedBackupPath,
                pathComparison) ||
            !IsWithin(backupPath, paths.BackupsDirectory) ||
            !P275FileSafety.IsRegularFile(backupPath) ||
            P275CandidateSidecarFinalizer.EnumerateSidecars(backupPath).Count != 0)
        {
            throw new P275BackupValidationException();
        }

        var fileInfo = new FileInfo(backupPath);
        if (fileInfo.Length != backup.ByteLength || fileInfo.Length <= 0)
        {
            throw new P275BackupValidationException();
        }

        using var stream = new FileStream(
            backupPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualHash, backup.Sha256, StringComparison.Ordinal))
        {
            throw new P275BackupValidationException();
        }
    }

    private async ValueTask<P275MigrationResult> ReadyAsync(
        P275MigrationRequest request,
        P275SourceInventory? source,
        P275VerifiedSafetyBackup? backup,
        P275Candidate? candidate,
        string? historyDirectoryPath,
        CancellationToken cancellationToken)
    {
        if (!await TryRecordAsync(
                CreateSnapshot(request, P275MigrationState.Ready, failureCode: null, backup, candidate, source),
                cancellationToken)
            .ConfigureAwait(false))
        {
            return P275MigrationResult.Failure(
                request.RunId,
                P275MigrationState.RecoveryRequired,
                P275MigrationFailureCodes.StateUnwritable,
                backup?.ArtifactId,
                candidate?.Files.CandidateDatabasePath,
                historyDirectoryPath,
                promoted: historyDirectoryPath is not null);
        }

        return P275MigrationResult.Success(
            request.RunId,
            backup?.ArtifactId,
            candidate?.Files.CandidateDatabasePath,
            historyDirectoryPath);
    }

    private async ValueTask<P275MigrationResult> FailAsync(
        P275MigrationRequest request,
        P275MigrationState state,
        string failureCode,
        P275SourceInventory? source,
        P275VerifiedSafetyBackup? backup,
        P275Candidate? candidate,
        bool promoted,
        string? historyDirectoryPath)
    {
        var candidatePath = candidate?.Files.CandidateDatabasePath;
        if (!promoted && candidate is not null)
        {
            candidatePath = PreserveCandidate(candidate);
        }

        var normalizedCode = P275MigrationFailureCodes.IsStable(failureCode)
            ? failureCode
            : P275MigrationFailureCodes.RecoveryRequired;
        var snapshot = CreateSnapshot(
            request,
            state,
            normalizedCode,
            backup,
            candidate,
            source,
            candidatePath);
        if (!await TryRecordAsync(snapshot, CancellationToken.None).ConfigureAwait(false))
        {
            return P275MigrationResult.Failure(
                request.RunId,
                P275MigrationState.RecoveryRequired,
                P275MigrationFailureCodes.StateUnwritable,
                backup?.ArtifactId,
                candidatePath,
                historyDirectoryPath,
                promoted);
        }

        return P275MigrationResult.Failure(
            request.RunId,
            state,
            normalizedCode,
            backup?.ArtifactId,
            candidatePath,
            historyDirectoryPath,
            promoted);
    }

    private async ValueTask<bool> TryRecordAsync(
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

    private static string? PreserveCandidate(P275Candidate candidate)
    {
        if (!Directory.Exists(candidate.Files.StagingDirectory))
        {
            return Directory.Exists(candidate.Files.FailedDirectory)
                ? Path.Combine(candidate.Files.FailedDirectory, "candidate.sqlite")
                : Path.Combine(candidate.Files.StagingDirectory, "candidate.sqlite");
        }

        // Never merge into or overwrite an existing failed RunId directory.
        // Leave the live Candidate in staging and report that exact artifact
        // when a collision prevents the normal directory move.
        if (Directory.Exists(candidate.Files.FailedDirectory))
        {
            return Path.Combine(candidate.Files.StagingDirectory, "candidate.sqlite");
        }

        try
        {
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

    private static P275MigrationStateSnapshot CreateSnapshot(
        P275MigrationRequest request,
        P275MigrationState state,
        string? failureCode,
        P275VerifiedSafetyBackup? backup,
        P275Candidate? candidate,
        P275SourceInventory? source = null,
        string? candidatePathOverride = null)
    {
        var candidatePath = candidatePathOverride ?? candidate?.Files.CandidateDatabasePath;
        string? candidateArtifact = null;
        if (candidatePath is not null)
        {
            try
            {
                candidateArtifact = request.Paths.GetProfileRelativeArtifact(candidatePath);
            }
            catch (ArgumentException)
            {
                candidateArtifact = null;
            }
        }

        var sourceSchema = source?.AppliedMigrations ?? request.Plan.ApprovedSourceMigrations;
        var backupArtifact = backup?.ArtifactId;
        return new P275MigrationStateSnapshot(
            request.RunId,
            request.ProfileScope,
            state,
            sourceSchema,
            request.Plan.ApprovedTargetMigrations,
            backupArtifact,
            candidateArtifact,
            failureCode,
            Retryable(failureCode),
            NextAction(failureCode),
            backup?.Sha256,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private static bool HasSameSource(P275SourceInventory expected, P275SourceInventory actual) =>
        expected.Exists == actual.Exists &&
        expected.AppliedMigrations.SequenceEqual(actual.AppliedMigrations, StringComparer.Ordinal) &&
        string.Equals(expected.Fingerprint.Value, actual.Fingerprint.Value, StringComparison.Ordinal);

    private static string NormalizeVerificationCode(string? code) =>
        code is not null && P275MigrationFailureCodes.IsStable(code)
            ? code
            : P275MigrationFailureCodes.VerifyFailed;

    private static string NormalizePromotionCode(string? code, string fallback) =>
        code is not null && P275MigrationFailureCodes.IsStable(code)
            ? code
            : fallback;

    private static bool Retryable(string? failureCode) => failureCode is
        P275MigrationFailureCodes.Locked or
        P275MigrationFailureCodes.BackupFailed or
        P275MigrationFailureCodes.BackupUnreadable or
        P275MigrationFailureCodes.SourceChanged or
        P275MigrationFailureCodes.ApplyFailed or
        P275MigrationFailureCodes.StateUnwritable;

    private static string NextAction(string? failureCode) => failureCode switch
    {
        P275MigrationFailureCodes.Locked or
        P275MigrationFailureCodes.BackupFailed or
        P275MigrationFailureCodes.BackupUnreadable or
        P275MigrationFailureCodes.SourceChanged or
        P275MigrationFailureCodes.ApplyFailed => "retry",
        P275MigrationFailureCodes.PromoteUnknown => P275MigrationNextActions.RestoreBackup,
        _ when failureCode is null => "none",
        _ => P275MigrationNextActions.RestoreBackup
    };

    private static P275MigrationState StateForException(P275MigrationState phase) => phase switch
    {
        P275MigrationState.BackupInProgress => P275MigrationState.BackupFailed,
        P275MigrationState.MigrationInProgress => P275MigrationState.MigrationFailed,
        P275MigrationState.PromotionInProgress => P275MigrationState.PromotionUnknown,
        P275MigrationState.VerifyInProgress => P275MigrationState.VerifyFailed,
        _ => P275MigrationState.RecoveryRequired
    };

    private static string FailureCodeForException(P275MigrationState phase) => phase switch
    {
        P275MigrationState.BackupInProgress => P275MigrationFailureCodes.BackupFailed,
        P275MigrationState.MigrationInProgress => P275MigrationFailureCodes.ApplyFailed,
        P275MigrationState.PromotionInProgress => P275MigrationFailureCodes.PromoteUnknown,
        P275MigrationState.VerifyInProgress => P275MigrationFailureCodes.VerifyFailed,
        _ => P275MigrationFailureCodes.RecoveryRequired
    };

    private static void ValidateRequest(P275MigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Paths);
        ArgumentNullException.ThrowIfNull(request.Plan);
        if (string.IsNullOrWhiteSpace(request.ProfileScope) ||
            request.ProfileScope.Length > 128 ||
            request.ProfileScope.Any(char.IsControl))
        {
            throw new ArgumentException("The profile scope is invalid.", nameof(request));
        }

        if (!Guid.TryParseExact(request.RunId, "D", out var runId) ||
            !string.Equals(runId.ToString("D"), request.RunId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The migration RunId must be a lower-case UUID.", nameof(request));
        }

        if (request.LockTimeout <= TimeSpan.Zero || request.LockTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The migration lock wait must be bounded to 30 seconds.");
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return fullPath.Equals(fullRoot, comparison) ||
            fullPath.StartsWith(
                fullRoot.EndsWith(Path.DirectorySeparatorChar)
                    ? fullRoot
                    : fullRoot + Path.DirectorySeparatorChar,
                comparison);
    }

    private sealed class P275SourceInventoryException : Exception
    {
        public P275SourceInventoryException(Exception? innerException = null)
            : base("The Active DB source inventory could not be read.", innerException)
        {
        }
    }

    private sealed class P275BackupValidationException : Exception
    {
    }
}

/// <summary>
/// Startup gate kept separate from the migration runner so the Agent host can
/// make the no-ready/no-writable decision before opening P25StorageStore.
/// </summary>
public sealed class P275StartupGate
{
    private readonly IP275MigrationRunner migrationRunner;

    public P275StartupGate(IP275MigrationRunner migrationRunner)
    {
        this.migrationRunner = migrationRunner ?? throw new ArgumentNullException(nameof(migrationRunner));
    }

    public async ValueTask<P275StartupGateResult> OpenAsync(
        P275MigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var migration = await migrationRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (!migration.Ready || !migration.Writable || migration.State != P275MigrationState.Ready)
        {
            return new P275StartupGateResult(
                migration,
                Ready: false,
                Writable: false);
        }

        return new P275StartupGateResult(migration, Ready: true, Writable: true);
    }
}

public sealed record P275StartupGateResult(
    P275MigrationResult Migration,
    bool Ready,
    bool Writable);
