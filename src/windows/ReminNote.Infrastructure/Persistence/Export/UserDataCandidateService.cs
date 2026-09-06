using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Reminders.Export;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Infrastructure.Persistence.Export;

public enum UserDataCandidateStatus
{
    Verified,
    RequiresConfirmation,
    Blocked,
    Promoted,
    PromotionUnknown
}

public sealed record UserDataCandidateValidationResult(
    bool IsValid,
    string? FailureCode,
    string CandidateRoot,
    string CandidateDatabasePath,
    string SourceChecksum,
    IReadOnlyList<string> Issues)
{
    public static UserDataCandidateValidationResult Invalid(
        string candidateRoot,
        string candidateDatabasePath,
        string failureCode,
        string? issue = null) => new(
        false,
        failureCode,
        candidateRoot,
        candidateDatabasePath,
        string.Empty,
        issue is null ? [failureCode] : [issue]);
}

public sealed record UserDataCandidatePromotionRequest(
    string CandidateRoot,
    string DataRoot,
    string ProfileName,
    string ProfileScope,
    string ActiveDatabasePath,
    bool Confirmed = false);

public sealed record UserDataCandidatePromotionResult(
    UserDataCandidateStatus Status,
    string? FailureCode,
    string CandidateDatabasePath,
    string? BackupArtifact,
    string? HistoryDirectoryPath,
    string SourceChecksum,
    IReadOnlyList<string> Issues);

/// <summary>
/// Validates and, only after an explicit confirmation, promotes a structured
/// user-data Candidate. The service is deliberately separate from the import
/// planner: verification is read-only, promotion uses the existing P2.75
/// migration lock, writer-quiescence lease, safety backup and atomic promoter.
/// </summary>
public static class UserDataCandidateService
{
    private const int MaxMarkerBytes = 8 * 1024;
    private static readonly HashSet<string> MarkerProperties =
    [
        "schema",
        "schemaVersion",
        "sourceChecksum",
        "candidateDatabase",
        "notificationPolicy",
        "pendingSchedulesDeferred",
        "deferredScheduleIds",
        "activeWrite"
    ];

    public static async ValueTask<UserDataCandidateValidationResult> ValidateAsync(
        string candidateRoot,
        string activeDatabasePath,
        string profileScope,
        CancellationToken cancellationToken = default)
    {
        string root;
        string candidatePath;
        try
        {
            (root, candidatePath) = ResolveCandidatePaths(
                candidateRoot,
                activeDatabasePath,
                profileScope);
        }
        catch (UserDataExportContractException exception)
        {
            return UserDataCandidateValidationResult.Invalid(
                candidateRoot,
                Path.Combine(candidateRoot ?? string.Empty, "candidate.sqlite"),
                exception.Code,
                exception.Message);
        }

        CandidateMarker marker;
        UserDataExportDocument document;
        try
        {
            marker = await ReadMarkerAsync(root, cancellationToken).ConfigureAwait(false);
            document = UserDataExportJson.Parse(
                await File.ReadAllBytesAsync(
                        Path.Combine(root, "structured-export.json"),
                        cancellationToken)
                    .ConfigureAwait(false));
            if (!string.Equals(marker.SourceChecksum, document.Checksum, StringComparison.Ordinal))
            {
                return UserDataCandidateValidationResult.Invalid(
                    root,
                    candidatePath,
                    "candidate.checksum_mismatch",
                    "Candidate marker 与结构化导出的 checksum 不一致。");
            }

            await ValidatePolicySidecarAsync(root, marker, document, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UserDataExportContractException exception)
        {
            return UserDataCandidateValidationResult.Invalid(
                root,
                candidatePath,
                exception.Code,
                exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            DecoderFallbackException or
            FormatException or
            SecurityException)
        {
            return UserDataCandidateValidationResult.Invalid(
                root,
                candidatePath,
                "candidate.read_failed",
                "Candidate 清单或结构化 artifact 无法读取。");
        }

        try
        {
            await VerifyCandidateDatabaseAsync(candidatePath, root, profileScope, document, marker, cancellationToken)
                .ConfigureAwait(false);
            return new(
                true,
                null,
                root,
                candidatePath,
                marker.SourceChecksum,
                ["candidate.marker=valid", "candidate.checksum=valid", "candidate.database=valid"]);
        }
        catch (UserDataExportContractException exception)
        {
            return UserDataCandidateValidationResult.Invalid(
                root,
                candidatePath,
                exception.Code,
                exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SqliteException or
            InvalidOperationException or
            ArgumentException or
            SecurityException)
        {
            return UserDataCandidateValidationResult.Invalid(
                root,
                candidatePath,
                "candidate.database_invalid",
                "Candidate 数据库未通过完整性、外键、schema 或 profile 校验。");
        }
    }

    public static async ValueTask<UserDataCandidatePromotionResult> PromoteAsync(
        UserDataCandidatePromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Confirmed)
        {
            return new(
                UserDataCandidateStatus.RequiresConfirmation,
                "candidate.confirmation_required",
                Path.Combine(request.CandidateRoot, "candidate.sqlite"),
                null,
                null,
                string.Empty,
                ["必须显式提供 --confirm 才能切换 Active。"]);
        }

        var initial = await ValidateAsync(
                request.CandidateRoot,
                request.ActiveDatabasePath,
                request.ProfileScope,
                cancellationToken)
            .ConfigureAwait(false);
        if (!initial.IsValid)
        {
            return Blocked(initial);
        }

        P275ProfilePaths paths;
        try
        {
            paths = new P275ProfilePaths(request.DataRoot, request.ProfileName);
            if (!PathEquals(paths.ActiveDatabasePath, request.ActiveDatabasePath))
            {
                return new(
                    UserDataCandidateStatus.Blocked,
                    "candidate.active_path_mismatch",
                    initial.CandidateDatabasePath,
                    null,
                    null,
                    initial.SourceChecksum,
                    ["Active 路径与解析后的 profile 不一致，拒绝切换。"]);
            }

            paths.EnsureRuntimeDirectories();
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            SecurityException)
        {
            return new(
                UserDataCandidateStatus.Blocked,
                "candidate.path_invalid",
                initial.CandidateDatabasePath,
                null,
                null,
                initial.SourceChecksum,
                ["Candidate 或 Active 数据根不是安全的本地 profile 路径。"]);
        }

        var runId = Guid.CreateVersion7().ToString("D");
        await using var migrationLock = await new P275FileMigrationLockProvider()
            .AcquireAsync(
                new P275MigrationLockRequest(paths, runId, TimeSpan.FromSeconds(10)),
                cancellationToken)
            .ConfigureAwait(false);
        if (migrationLock is null)
        {
            return new(
                UserDataCandidateStatus.Blocked,
                P275MigrationFailureCodes.Locked,
                initial.CandidateDatabasePath,
                null,
                null,
                initial.SourceChecksum,
                ["profile 正被另一个迁移、恢复或 Agent writer 占用。"]);
        }

        await using var quiescence = await new P275FileWriterQuiescence()
            .AcquireAsync(paths, runId, cancellationToken)
            .ConfigureAwait(false);
        if (quiescence is null)
        {
            return new(
                UserDataCandidateStatus.Blocked,
                P275MigrationFailureCodes.Locked,
                initial.CandidateDatabasePath,
                null,
                null,
                initial.SourceChecksum,
                ["Agent writer 未停止，拒绝在活动写入期间切换 Active。"]);
        }

        // Re-read all external Candidate material after both leases are held;
        // a user cannot replace the validated file behind the promotion gate.
        var latest = await ValidateAsync(
                request.CandidateRoot,
                request.ActiveDatabasePath,
                request.ProfileScope,
                cancellationToken)
            .ConfigureAwait(false);
        if (!latest.IsValid || !string.Equals(initial.SourceChecksum, latest.SourceChecksum, StringComparison.Ordinal))
        {
            return new(
                UserDataCandidateStatus.Blocked,
                latest.IsValid ? "candidate.changed" : latest.FailureCode,
                latest.CandidateDatabasePath,
                null,
                null,
                latest.SourceChecksum,
                ["Candidate 在锁定后发生变化或重新验证失败，未写入 Active。"]);
        }

        P275SourceInventory source;
        try
        {
            source = await ReadSourceAsync(paths, request.ProfileScope, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SqliteException or
            InvalidOperationException or
            ArgumentException)
        {
            return new(
                UserDataCandidateStatus.Blocked,
                P275MigrationFailureCodes.SourceInvalid,
                latest.CandidateDatabasePath,
                null,
                null,
                latest.SourceChecksum,
                ["无法安全读取当前 Active source fingerprint。"]);
        }

        P275VerifiedSafetyBackup backup;
        try
        {
            var plan = P275MigrationPlanCatalog.Current;
            if (!source.AppliedMigrations.SequenceEqual(
                    plan.ApprovedTargetMigrations.Take(source.AppliedMigrations.Count),
                    StringComparer.Ordinal) &&
                !source.AppliedMigrations.SequenceEqual(plan.ApprovedSourceMigrations, StringComparer.Ordinal))
            {
                return new(
                    UserDataCandidateStatus.Blocked,
                    P275MigrationFailureCodes.IncompatibleSchema,
                    latest.CandidateDatabasePath,
                    null,
                    null,
                    latest.SourceChecksum,
                    ["当前 Active schema 不在已批准的 P2.75/P3 迁移前缀内。"]);
            }

            backup = source.Exists
                ? await new P275SafetyBackupProvider().CreateVerifiedBackupAsync(
                        new P275SafetyBackupRequest(
                            paths,
                            request.ProfileScope,
                            runId,
                            source,
                            plan),
                        cancellationToken)
                    .ConfigureAwait(false)
                : new P275VerifiedSafetyBackup(
                    "structured-empty-" + runId,
                    null,
                    sourceIsEmpty: true,
                    [],
                    new P275SourceFingerprint("empty"),
                    0,
                    string.Empty);
        }
        catch (P275MigrationOperationException exception)
        {
            return new(
                UserDataCandidateStatus.Blocked,
                exception.FailureCode,
                latest.CandidateDatabasePath,
                null,
                null,
                latest.SourceChecksum,
                ["Active 安全备份未完成，拒绝切换。"]);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SqliteException or
            InvalidOperationException or
            ArgumentException)
        {
            return new(
                UserDataCandidateStatus.Blocked,
                P275MigrationFailureCodes.BackupFailed,
                latest.CandidateDatabasePath,
                null,
                null,
                latest.SourceChecksum,
                ["Active 安全备份失败，拒绝切换。"]);
        }

        var candidateFiles = paths.CreateCandidatePaths(runId);
        var promotionBoundaryStarted = false;
        try
        {
            Directory.CreateDirectory(candidateFiles.StagingDirectory);
            CopyGeneration(latest.CandidateDatabasePath, candidateFiles.CandidateDatabasePath);
            foreach (var sidecar in P275CandidateSidecarFinalizer.EnumerateSidecars(latest.CandidateDatabasePath))
            {
                CopyGeneration(sidecar, candidateFiles.CandidateDatabasePath +
                    sidecar[latest.CandidateDatabasePath.Length..]);
            }

            var candidate = new P275Candidate(paths, candidateFiles, backup);
            var statePort = new P275ProductionMigrationStatePort(new P275ProfileLayout(paths.ProfileRoot));
            await statePort.RecordAsync(
                    new P275MigrationStateSnapshot(
                        runId,
                        request.ProfileScope,
                        P275MigrationState.PromotionInProgress,
                        source.AppliedMigrations,
                        P275MigrationPlanCatalog.Current.ApprovedTargetMigrations,
                        backup.SourceIsEmpty ? null : backup.ArtifactId,
                        P275ProfileLayout.GetCandidateArtifact(Guid.Parse(runId)),
                        null,
                        false,
                        P275MigrationNextActions.None,
                        backup.SourceIsEmpty ? null : backup.Sha256,
                        LastAgentInstanceId: runId),
                    cancellationToken)
                .ConfigureAwait(false);

            var finalized = await new P275CandidateSidecarFinalizer()
                .FinalizeAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            if (!finalized.IsValid)
            {
                return new(
                    UserDataCandidateStatus.Blocked,
                    finalized.FailureCode ?? P275MigrationFailureCodes.VerifyFailed,
                    latest.CandidateDatabasePath,
                    backup.SourceIsEmpty ? null : backup.ArtifactId,
                    null,
                    latest.SourceChecksum,
                    ["Candidate sidecar 未能收敛为完整单文件 generation。"]);
            }

            var sourceAfterBackup = await ReadSourceAsync(paths, request.ProfileScope, cancellationToken)
                .ConfigureAwait(false);
            if (!SameSource(source, sourceAfterBackup))
            {
                return new(
                    UserDataCandidateStatus.Blocked,
                    P275MigrationFailureCodes.SourceChanged,
                    latest.CandidateDatabasePath,
                    backup.SourceIsEmpty ? null : backup.ArtifactId,
                    null,
                    latest.SourceChecksum,
                    ["Active 在备份后发生变化，Candidate 保留且未切换。"]);
            }

            promotionBoundaryStarted = true;
            var promotion = await new P275AtomicFilePromoter()
                .PromoteAsync(
                    new P275PromotionRequest(paths, candidate, source, request.ProfileScope),
                    cancellationToken)
                .ConfigureAwait(false);
            if (promotion.Outcome == P275PromotionOutcome.Unknown)
            {
                await TryRecordStateAsync(
                        statePort,
                        runId,
                        request.ProfileScope,
                        source,
                        backup,
                        P275MigrationState.PromotionUnknown,
                        P275MigrationFailureCodes.PromoteUnknown,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new(
                    UserDataCandidateStatus.PromotionUnknown,
                    P275MigrationFailureCodes.PromoteUnknown,
                    latest.CandidateDatabasePath,
                    backup.SourceIsEmpty ? null : backup.ArtifactId,
                    null,
                    latest.SourceChecksum,
                    ["原子切换结果不确定，Active 不再由本命令继续猜测或修复。"]);
            }

            if (promotion.Outcome != P275PromotionOutcome.Succeeded)
            {
                await TryRecordStateAsync(
                        statePort,
                        runId,
                        request.ProfileScope,
                        source,
                        backup,
                        P275MigrationState.VerifyFailed,
                        promotion.FailureCode ?? P275MigrationFailureCodes.PromoteFailed,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new(
                    UserDataCandidateStatus.Blocked,
                    promotion.FailureCode ?? P275MigrationFailureCodes.PromoteFailed,
                    latest.CandidateDatabasePath,
                    backup.SourceIsEmpty ? null : backup.ArtifactId,
                    null,
                    latest.SourceChecksum,
                    ["Candidate 未能原子切换，Active 保持原 generation。"]);
            }

            var postVerify = await VerifyActiveDatabaseAsync(paths, request.ProfileScope, cancellationToken)
                .ConfigureAwait(false);
            if (!postVerify)
            {
                await TryRecordStateAsync(
                        statePort,
                        runId,
                        request.ProfileScope,
                        source,
                        backup,
                        P275MigrationState.PromotionUnknown,
                        P275MigrationFailureCodes.PromoteUnknown,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new(
                    UserDataCandidateStatus.PromotionUnknown,
                    P275MigrationFailureCodes.PromoteUnknown,
                    latest.CandidateDatabasePath,
                    backup.SourceIsEmpty ? null : backup.ArtifactId,
                    promotion.HistoryDirectoryPath,
                    latest.SourceChecksum,
                    ["切换后 Active 未通过独立 post-promote verify。"]);
            }

            await AdoptPolicySidecarAsync(latest.CandidateRoot, paths.DataRoot, cancellationToken)
                .ConfigureAwait(false);
            await statePort.RecordAsync(
                    new P275MigrationStateSnapshot(
                        runId,
                        request.ProfileScope,
                        P275MigrationState.Ready,
                        source.AppliedMigrations,
                        P275MigrationPlanCatalog.Current.ApprovedTargetMigrations,
                        backup.SourceIsEmpty ? null : backup.ArtifactId,
                        null,
                        null,
                        false,
                        P275MigrationNextActions.None,
                        backup.SourceIsEmpty ? null : backup.Sha256,
                        LastAgentInstanceId: runId),
                    cancellationToken)
                .ConfigureAwait(false);

            return new(
                UserDataCandidateStatus.Promoted,
                null,
                latest.CandidateDatabasePath,
                backup.SourceIsEmpty ? null : backup.ArtifactId,
                promotion.HistoryDirectoryPath,
                latest.SourceChecksum,
                ["candidate.verify=pass", "active.promote=pass", "active.post_verify=pass"]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SqliteException or
            InvalidOperationException or
            ArgumentException or
            SecurityException)
        {
            return new(
                promotionBoundaryStarted
                    ? UserDataCandidateStatus.PromotionUnknown
                    : UserDataCandidateStatus.Blocked,
                promotionBoundaryStarted
                    ? P275MigrationFailureCodes.PromoteUnknown
                    : P275MigrationFailureCodes.PromoteFailed,
                latest.CandidateDatabasePath,
                backup.SourceIsEmpty ? null : backup.ArtifactId,
                null,
                latest.SourceChecksum,
                [promotionBoundaryStarted
                    ? "原子切换边界之后发生异常，结果按 UNKNOWN 处理，必须进入恢复路径。"
                    : "受控 Candidate promotion 未完成，Active 未由异常路径继续写入。"]);
        }
    }

    private static UserDataCandidatePromotionResult Blocked(
        UserDataCandidateValidationResult result) => new(
        UserDataCandidateStatus.Blocked,
        result.FailureCode ?? "candidate.invalid",
        result.CandidateDatabasePath,
        null,
        null,
        result.SourceChecksum,
        result.Issues);

    private static (string Root, string CandidatePath) ResolveCandidatePaths(
        string candidateRoot,
        string activeDatabasePath,
        string profileScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeDatabasePath);
        ProtocolProfileScope.Validate(profileScope);
        var root = CanonicalizeLocalDirectory(candidateRoot, "candidateRoot");
        var active = CanonicalizeLocalPath(activeDatabasePath, "activeDatabasePath");
        var activeRoot = Path.GetDirectoryName(active)
            ?? throw new UserDataExportContractException("candidate.path_invalid", "Active profile 目录无效。", "activeDatabasePath");
        if (IsSameOrWithin(root, activeRoot) || IsSameOrWithin(activeRoot, root))
        {
            throw new UserDataExportContractException(
                "candidate.active_write_forbidden",
                "Candidate root 不能与 Active profile 目录重合或互相包含。",
                "candidateRoot");
        }

        var candidatePath = Path.Combine(root, "candidate.sqlite");
        var markerPath = Path.Combine(root, "candidate-restore.json");
        var artifactPath = Path.Combine(root, "structured-export.json");
        foreach (var path in new[] { candidatePath, markerPath, artifactPath })
        {
            EnsureWithin(root, path);
        }

        if (!Directory.Exists(root) || !IsRegularNonReparseDirectory(root))
        {
            throw new UserDataExportContractException("candidate.path_invalid", "Candidate root 不存在或是 reparse/link 路径。", "candidateRoot");
        }

        if (!File.Exists(candidatePath) || !File.Exists(markerPath) || !File.Exists(artifactPath))
        {
            throw new UserDataExportContractException("candidate.artifact_missing", "Candidate 缺少 candidate.sqlite、candidate-restore.json 或 structured-export.json。", "candidateRoot");
        }

        foreach (var path in new[] { candidatePath, markerPath, artifactPath })
        {
            if (!IsRegularNonReparseFile(path))
            {
                throw new UserDataExportContractException("candidate.path_invalid", "Candidate 清单或数据库不是普通文件。", "candidateRoot");
            }
        }

        return (root, candidatePath);
    }

    private static async ValueTask<CandidateMarker> ReadMarkerAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "candidate-restore.json");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0 || bytes.Length > MaxMarkerBytes)
        {
            throw new UserDataExportContractException("candidate.marker_invalid", "Candidate marker 大小无效。", "candidate-restore.json");
        }

        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new UserDataExportContractException("candidate.marker_invalid", "Candidate marker 必须是 object。", "candidate-restore.json");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name) || !MarkerProperties.Contains(property.Name))
                {
                    throw new UserDataExportContractException("candidate.marker_invalid", "Candidate marker 含重复或未知字段。", "candidate-restore.json");
                }
            }

            var schema = ReadString(document.RootElement, "schema");
            var schemaVersion = ReadInt(document.RootElement, "schemaVersion");
            var sourceChecksum = ReadString(document.RootElement, "sourceChecksum");
            var candidateDatabase = ReadString(document.RootElement, "candidateDatabase");
            var notificationPolicy = ReadNullableString(document.RootElement, "notificationPolicy");
            var pendingDeferred = ReadInt(document.RootElement, "pendingSchedulesDeferred");
            var deferredScheduleIds = ReadStringArray(document.RootElement, "deferredScheduleIds");
            var activeWrite = ReadBoolean(document.RootElement, "activeWrite");
            if (!string.Equals(schema, UserDataExportContract.Schema, StringComparison.Ordinal) ||
                schemaVersion != UserDataExportContract.SchemaVersion ||
                !IsChecksum(sourceChecksum) ||
                !string.Equals(candidateDatabase, "candidate.sqlite", StringComparison.Ordinal) ||
                notificationPolicy is not (null or "notification-policy.json") ||
                pendingDeferred < 0 ||
                pendingDeferred != deferredScheduleIds.Length ||
                activeWrite)
            {
                throw new UserDataExportContractException("candidate.marker_invalid", "Candidate marker 与受控恢复契约不匹配。", "candidate-restore.json");
            }

            return new(sourceChecksum, notificationPolicy, pendingDeferred, deferredScheduleIds);
        }
        catch (UserDataExportContractException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new UserDataExportContractException("candidate.marker_invalid", "Candidate marker 不是严格 JSON。", "candidate-restore.json", exception);
        }
    }

    private static async ValueTask ValidatePolicySidecarAsync(
        string root,
        CandidateMarker marker,
        UserDataExportDocument document,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "notification-policy.json");
        if (marker.NotificationPolicy is null)
        {
            if (File.Exists(path))
            {
                throw new UserDataExportContractException("candidate.policy_unexpected", "Candidate 存在未在 marker 声明的通知策略侧车。", "notification-policy.json");
            }

            return;
        }

        if (!File.Exists(path) || !IsRegularNonReparseFile(path))
        {
            throw new UserDataExportContractException("candidate.policy_missing", "Candidate marker 声明了通知策略但侧车缺失。", "notification-policy.json");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 256 * 1024)
        {
            throw new UserDataExportContractException("candidate.policy_invalid", "通知策略侧车超出大小上限。", "notification-policy.json");
        }

        string canonical;
        try
        {
            using var policy = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            if (policy.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException();
            }

            canonical = Encoding.UTF8.GetString(ProtocolCanonicalization.Canonicalize(policy.RootElement));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or DecoderFallbackException)
        {
            throw new UserDataExportContractException("candidate.policy_invalid", "通知策略侧车不是合法 JSON object。", "notification-policy.json", exception);
        }

        if (!string.Equals(canonical, document.NotificationPolicyJson, StringComparison.Ordinal))
        {
            throw new UserDataExportContractException("candidate.policy_mismatch", "通知策略侧车与导出 envelope 不一致。", "notification-policy.json");
        }
    }

    private static async ValueTask VerifyCandidateDatabaseAsync(
        string candidatePath,
        string candidateRoot,
        string profileScope,
        UserDataExportDocument document,
        CandidateMarker marker,
        CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "reminnote-candidate-verify-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var layout = new P275ProfileLayout(tempRoot);
            Directory.CreateDirectory(layout.ProfileRoot);
            Directory.CreateDirectory(layout.StagingDirectory);
            var runId = Guid.CreateVersion7();
            var verificationPath = layout.GetCandidatePath(runId);
            Directory.CreateDirectory(Path.GetDirectoryName(verificationPath)!);
            CopyGeneration(candidatePath, verificationPath);
            foreach (var sidecar in P275CandidateSidecarFinalizer.EnumerateSidecars(candidatePath))
            {
                CopyGeneration(sidecar, verificationPath + sidecar[candidatePath.Length..]);
            }

            var expectations = await P275MigrationVerificationExpectationsFactory
                .CreateAsync(P275MigrationPlanCatalog.Current, P275VerificationBaseline.Empty, cancellationToken)
                .ConfigureAwait(false);
            var verification = await P275CandidateVerifier.VerifyAsync(
                    new P275CandidateVerificationRequest(
                        layout,
                        runId,
                        expectations),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!verification.Passed)
            {
                throw new UserDataExportContractException(
                    "candidate.database_invalid",
                    $"Candidate 数据库验证失败：{verification.FailureCode ?? P275MigrationFailureCodes.VerifyFailed}。");
            }

            await using var connection = await P25ReadOnlyConnectionFactory
                .OpenAsync(candidatePath, cancellationToken)
                .ConfigureAwait(false);
            await using var context = ReminNoteDatabase.CreateContext(connection);
            var profiles = await context.RevisionStates
                .AsNoTracking()
                .Select(value => value.ProfileScope)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (profiles.Length != 1 || !string.Equals(profiles[0], profileScope, StringComparison.Ordinal))
            {
                throw new UserDataExportContractException("candidate.profile_mismatch", "Candidate profile scope 与当前目标不一致。", "profileScope");
            }

            var expectedDeferredScheduleIds = document.Reminders.Schedules
                .Where(value => string.Equals(value.State, ScheduleState.PENDING.ToString(), StringComparison.Ordinal))
                .Select(value => value.Id)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (!marker.DeferredScheduleIds.SequenceEqual(expectedDeferredScheduleIds, StringComparer.Ordinal))
            {
                throw new UserDataExportContractException("candidate.marker_invalid", "Candidate marker 的延期 Schedule ID 集合与导出内容不一致。", "deferredScheduleIds");
            }

            var expectedPendingSchedules = expectedDeferredScheduleIds.Length;
            if (marker.PendingSchedulesDeferred != expectedPendingSchedules)
            {
                throw new UserDataExportContractException("candidate.marker_invalid", "Candidate marker 的 pending Schedule 延期计数与导出内容不一致。", "pendingSchedulesDeferred");
            }

            var taskCount = await context.Tasks.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
            var historyCount = await context.TaskHistory.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
            var ruleCount = await context.ReminderRules.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
            var scheduleCount = await context.ReminderSchedules.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
            var instanceCount = await context.ReminderInstances.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
            var expectedSchedules = document.Reminders.Schedules.Count;
            var expectedInstances = document.Reminders.Instances.Count;
            if (taskCount != document.Tasks.Count ||
                historyCount != document.TaskHistory.Count ||
                ruleCount != document.Reminders.Rules.Count ||
                scheduleCount != expectedSchedules ||
                instanceCount != expectedInstances)
            {
                throw new UserDataExportContractException("candidate.data_incomplete", "Candidate 中的 Task/History/Rule/Schedule/Instance 数量与导出内容不一致。", "candidate.sqlite");
            }

            if (await context.ReminderSchedules
                    .AsNoTracking()
                    .AnyAsync(value => value.State == ScheduleState.PENDING, cancellationToken)
                .ConfigureAwait(false))
            {
                throw new UserDataExportContractException("candidate.pending_schedule", "Candidate 不得直接激活旧 pending Schedule。", "reminders.schedules");
            }

            await VerifyCandidateContentAsync(
                    candidatePath,
                    candidateRoot,
                    profileScope,
                    document,
                    marker,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(tempRoot);
        }
    }

    /// <summary>
    /// Counts are only a liveness check. Promotion additionally requires the
    /// complete structured export to round-trip through the Candidate DB.
    /// The only intentional transformation is source PENDING schedules being
    /// represented as CANCELLED/RECOVERY_OBSOLETE so the scheduler cannot
    /// activate an unconfirmed reminder.
    /// </summary>
    private static async ValueTask VerifyCandidateContentAsync(
        string candidatePath,
        string candidateRoot,
        string profileScope,
        UserDataExportDocument expected,
        CandidateMarker marker,
        CancellationToken cancellationToken)
    {
        UserDataExportArtifact actualArtifact;
        try
        {
            actualArtifact = await UserDataExportService
                .CreateAsync(candidatePath, candidateRoot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UserDataExportContractException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or SqliteException or InvalidOperationException or FormatException)
        {
            throw new UserDataExportContractException(
                "candidate.content_read_failed",
                "Candidate 结构化内容无法重新导出。",
                "candidate.sqlite",
                exception);
        }

        var actual = actualArtifact.Document;
        if (!string.Equals(actual.Schema, expected.Schema, StringComparison.Ordinal) ||
            actual.SchemaVersion != expected.SchemaVersion ||
            actual.GlobalRevision != expected.GlobalRevision ||
            !string.Equals(actual.NotificationPolicyJson, expected.NotificationPolicyJson, StringComparison.Ordinal) ||
            actual.AppSettings != expected.AppSettings)
        {
            throw ContentMismatch("envelope");
        }

        RequireEqual("tasks", expected.Tasks, actual.Tasks, static value => value.Id);
        RequireEqual("taskHistory", expected.TaskHistory, actual.TaskHistory, static value => value.Id);
        RequireEqual("deliveryAttempts", expected.DeliveryAttempts, actual.DeliveryAttempts, static value => value.AttemptId);
        RequireEqual(
            "deliveryAttemptEvents",
            expected.DeliveryAttemptEvents ?? Array.Empty<UserDeliveryAttemptEventExport>(),
            actual.DeliveryAttemptEvents ?? Array.Empty<UserDeliveryAttemptEventExport>(),
            static value => $"{value.AttemptId}:{value.EventOrdinal:D20}");
        RequireEqual("reminders.rules", expected.Reminders.Rules, actual.Reminders.Rules, static value => value.Id);
        RequireEqual("reminders.instances", expected.Reminders.Instances, actual.Reminders.Instances, static value => value.Id);

        var expectedSchedules = expected.Reminders.Schedules
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var expectedSchedulesById = expectedSchedules.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var deferredIds = marker.DeferredScheduleIds.ToHashSet(StringComparer.Ordinal);
        var actualSchedules = actual.Reminders.Schedules
            .Select(value =>
            {
                if (!deferredIds.Contains(value.Id))
                {
                    return value;
                }

                if (!expectedSchedulesById.TryGetValue(value.Id, out var sourceSchedule) ||
                    !string.Equals(value.State, ScheduleState.CANCELLED.ToString(), StringComparison.Ordinal) ||
                    !string.Equals(value.TerminalReason, ScheduleStateReason.RECOVERY_OBSOLETE.ToString(), StringComparison.Ordinal) ||
                    value.ReplacementScheduleId is not null ||
                    !string.Equals(value.TerminalAtUtc, value.CreatedAtUtc, StringComparison.Ordinal))
                {
                    throw ContentMismatch($"reminders.schedules[{value.Id}].deferred_state");
                }

                // Only the four fields that represent the explicit deferred
                // decision are normalized. All other schedule fields remain
                // Candidate-derived so a count-preserving mutation cannot be
                // hidden by the normalization step.
                return value with
                {
                    State = sourceSchedule.State,
                    TerminalReason = sourceSchedule.TerminalReason,
                    ReplacementScheduleId = sourceSchedule.ReplacementScheduleId,
                    TerminalAtUtc = sourceSchedule.TerminalAtUtc
                };
            })
            .ToArray();
        RequireEqual("reminders.schedules", expectedSchedules, actualSchedules, static value => value.Id);

        // Exporting the Candidate also verifies that its event stream was
        // rebound to the requested profile scope. Keep the explicit argument
        // in the method contract so future readers cannot accidentally compare
        // a different profile's rows.
        _ = profileScope;
    }

    private static void RequireEqual<T, TKey>(
        string field,
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        Func<T, TKey> keySelector)
        where TKey : notnull
    {
        var expectedArray = expected.OrderBy(keySelector).ToArray();
        var actualArray = actual.OrderBy(keySelector).ToArray();
        if (expectedArray.Length != actualArray.Length ||
            !expectedArray.SequenceEqual(actualArray))
        {
            throw ContentMismatch(field);
        }
    }

    private static UserDataExportContractException ContentMismatch(string field) =>
        new("candidate.content_mismatch", $"Candidate 内容与结构化导出在 {field} 字段不一致，拒绝 promotion。", field);

    private static async ValueTask<P275SourceInventory> ReadSourceAsync(
        P275ProfilePaths paths,
        string profileScope,
        CancellationToken cancellationToken)
    {
        return await new P275ActiveSourceReader()
            .ReadAsync(paths, profileScope, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<bool> VerifyActiveDatabaseAsync(
        P275ProfilePaths paths,
        string profileScope,
        CancellationToken cancellationToken)
    {
        var expectations = await P275MigrationVerificationExpectationsFactory
            .CreateAsync(P275MigrationPlanCatalog.Current, P275VerificationBaseline.Empty, cancellationToken)
            .ConfigureAwait(false);
        var result = await P275CandidateVerifier.VerifyAsync(
                new P275CandidateVerificationRequest(
                    new P275ProfileLayout(paths.ProfileRoot),
                    Guid.CreateVersion7(),
                    expectations,
                    paths.ActiveDatabasePath),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Passed)
        {
            return false;
        }

        await using var connection = await P25ReadOnlyConnectionFactory
            .OpenAsync(paths.ActiveDatabasePath, cancellationToken)
            .ConfigureAwait(false);
        await using var context = ReminNoteDatabase.CreateContext(connection);
        var profiles = await context.RevisionStates
            .AsNoTracking()
            .Select(value => value.ProfileScope)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return profiles.Length == 1 && string.Equals(profiles[0], profileScope, StringComparison.Ordinal);
    }

    private static async ValueTask AdoptPolicySidecarAsync(
        string candidateRoot,
        string dataRoot,
        CancellationToken cancellationToken)
    {
        var source = Path.Combine(candidateRoot, "notification-policy.json");
        if (!File.Exists(source))
        {
            return;
        }

        var target = Path.Combine(Path.GetFullPath(dataRoot), "notification-policy.json");
        EnsureWithin(Path.GetFullPath(dataRoot), target);
        var temporary = target + ".candidate-" + Guid.CreateVersion7().ToString("N") + ".tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static async ValueTask TryRecordStateAsync(
        P275ProductionMigrationStatePort statePort,
        string runId,
        string profileScope,
        P275SourceInventory source,
        P275VerifiedSafetyBackup backup,
        P275MigrationState state,
        string failureCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await statePort.RecordAsync(
                    new P275MigrationStateSnapshot(
                        runId,
                        profileScope,
                        state,
                        source.AppliedMigrations,
                        P275MigrationPlanCatalog.Current.ApprovedTargetMigrations,
                        backup.SourceIsEmpty ? null : backup.ArtifactId,
                        P275ProfileLayout.GetCandidateArtifact(Guid.Parse(runId)),
                        failureCode,
                        true,
                        P275MigrationNextActions.ViewStatus,
                        backup.SourceIsEmpty ? null : backup.Sha256,
                        LastAgentInstanceId: runId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The caller already returns a fail-closed result. State is best
            // effort here because a state-write failure cannot make Active
            // safe to guess after a promotion boundary.
        }
    }

    private static bool SameSource(P275SourceInventory left, P275SourceInventory right) =>
        left.Exists == right.Exists &&
        left.AppliedMigrations.SequenceEqual(right.AppliedMigrations, StringComparer.Ordinal) &&
        string.Equals(left.Fingerprint.Value, right.Fingerprint.Value, StringComparison.Ordinal);

    private static void CopyGeneration(string source, string destination)
    {
        if (!IsRegularNonReparseFile(source) || File.Exists(destination))
        {
            throw new IOException("Candidate generation file is unavailable.");
        }

        var parent = Path.GetDirectoryName(destination)
            ?? throw new IOException("Candidate generation has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporary = destination + ".partial";
        if (File.Exists(temporary))
        {
            throw new IOException("Candidate generation temporary path is occupied.");
        }

        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string CanonicalizeLocalDirectory(string value, string fieldName)
    {
        var path = CanonicalizeLocalPath(value, fieldName);
        if (!Directory.Exists(path))
        {
            throw new UserDataExportContractException("candidate.path_invalid", "Candidate root 目录不存在。", fieldName);
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string CanonicalizeLocalPath(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new UserDataExportContractException("candidate.path_invalid", "路径必须是绝对本地路径。", fieldName);
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            throw new UserDataExportContractException("candidate.path_invalid", "路径无法规范化。", fieldName, exception);
        }
    }

    private static bool IsSameOrWithin(string candidate, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedCandidate.Equals(normalizedParent, comparison) ||
            normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, comparison) ||
            normalizedCandidate.StartsWith(normalizedParent + Path.AltDirectorySeparatorChar, comparison);
    }

    private static void EnsureWithin(string root, string child)
    {
        if (!IsSameOrWithin(child, root))
        {
            throw new UserDataExportContractException("candidate.path_invalid", "Candidate 路径越出其 root。", "candidateRoot");
        }
    }

    private static bool IsRegularNonReparseDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        return (info.Attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static bool IsRegularNonReparseFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsChecksum(string value) =>
        value.StartsWith(UserDataExportContract.ChecksumPrefix, StringComparison.Ordinal) &&
        value.Length == UserDataExportContract.ChecksumPrefix.Length + 64 &&
        value[UserDataExportContract.ChecksumPrefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string[] ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new UserDataExportContractException("candidate.marker_invalid", $"Candidate marker 字段 {name} 类型无效。", name);
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new UserDataExportContractException("candidate.marker_invalid", $"Candidate marker 字段 {name} 必须是非空字符串数组。", name);
            }

            var itemValue = item.GetString()!;
            if (!Guid.TryParseExact(itemValue, "D", out var parsed) ||
                parsed.Version != 7 ||
                !string.Equals(parsed.ToString("D"), itemValue, StringComparison.Ordinal) ||
                !seen.Add(itemValue))
            {
                throw new UserDataExportContractException("candidate.marker_invalid", $"Candidate marker 字段 {name} 包含重复或无效 Schedule ID。", name);
            }

            result.Add(itemValue);
        }

        return result.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new UserDataExportContractException("candidate.marker_invalid", $"Candidate marker 缺少合法字段 {name}。", name);

    private static string? ReadNullableString(JsonElement root, string name) =>
        !root.TryGetProperty(name, out var value)
            ? throw new UserDataExportContractException("candidate.marker_invalid", $"Candidate marker 缺少字段 {name}。", name)
            : value.ValueKind == JsonValueKind.Null
                ? null
                : value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                    ? value.GetString()
                    : throw new UserDataExportContractException("candidate.marker_invalid", $"Candidate marker 字段 {name} 类型无效。", name);

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw new UserDataExportContractException("candidate.marker_invalid", $"Candidate marker 字段 {name} 类型无效。", name);

    private static bool ReadBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : throw new UserDataExportContractException("candidate.marker_invalid", $"Candidate marker 字段 {name} 类型无效。", name);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed record CandidateMarker(
        string SourceChecksum,
        string? NotificationPolicy,
        int PendingSchedulesDeferred,
        IReadOnlyList<string> DeferredScheduleIds);
}
