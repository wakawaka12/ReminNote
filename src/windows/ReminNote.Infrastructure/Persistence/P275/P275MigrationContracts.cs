using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;
using ReminNote.Core.Protocol;

namespace ReminNote.Infrastructure.Persistence.P275;

public static class P275MigrationContract
{
    public const string Version = "RN-P275-SAFETY-1.0.0";

    public const int MaxMarkerBytes = 8 * 1024;

    public const int MaxSchemaItems = 128;

    public const int MaxMigrationIdBytes = 192;

    public const int MaxArtifactBytes = 512;

    public const int MaxNextActionBytes = 64;
}

/// <summary>
/// Machine-readable state names from the P2.75 runtime marker contract.
/// Marker persistence itself belongs to the P2.75-03 adapter.
/// </summary>
public enum P275MigrationState
{
    Ready,
    MigrationRequired,
    BackupInProgress,
    BackupFailed,
    MigrationInProgress,
    MigrationFailed,
    VerifyInProgress,
    VerifyFailed,
    PromotionInProgress,
    PromotionUnknown,
    RecoveryRequired,
    RecoveryInProgress,
    RecoveryFailed
}

public static class P275MigrationStateNames
{
    public static string ToValue(P275MigrationState state) => state switch
    {
        P275MigrationState.Ready => "READY",
        P275MigrationState.MigrationRequired => "MIGRATION_REQUIRED",
        P275MigrationState.BackupInProgress => "BACKUP_IN_PROGRESS",
        P275MigrationState.BackupFailed => "BACKUP_FAILED",
        P275MigrationState.MigrationInProgress => "MIGRATION_IN_PROGRESS",
        P275MigrationState.MigrationFailed => "MIGRATION_FAILED",
        P275MigrationState.VerifyInProgress => "VERIFY_IN_PROGRESS",
        P275MigrationState.VerifyFailed => "VERIFY_FAILED",
        P275MigrationState.PromotionInProgress => "PROMOTION_IN_PROGRESS",
        P275MigrationState.PromotionUnknown => "PROMOTION_UNKNOWN",
        P275MigrationState.RecoveryRequired => "RECOVERY_REQUIRED",
        P275MigrationState.RecoveryInProgress => "RECOVERY_IN_PROGRESS",
        P275MigrationState.RecoveryFailed => "RECOVERY_FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown P2.75 migration state.")
    };

    public static bool TryParse(string? value, out P275MigrationState state)
    {
        state = value switch
        {
            "READY" => P275MigrationState.Ready,
            "MIGRATION_REQUIRED" => P275MigrationState.MigrationRequired,
            "BACKUP_IN_PROGRESS" => P275MigrationState.BackupInProgress,
            "BACKUP_FAILED" => P275MigrationState.BackupFailed,
            "MIGRATION_IN_PROGRESS" => P275MigrationState.MigrationInProgress,
            "MIGRATION_FAILED" => P275MigrationState.MigrationFailed,
            "VERIFY_IN_PROGRESS" => P275MigrationState.VerifyInProgress,
            "VERIFY_FAILED" => P275MigrationState.VerifyFailed,
            "PROMOTION_IN_PROGRESS" => P275MigrationState.PromotionInProgress,
            "PROMOTION_UNKNOWN" => P275MigrationState.PromotionUnknown,
            "RECOVERY_REQUIRED" => P275MigrationState.RecoveryRequired,
            "RECOVERY_IN_PROGRESS" => P275MigrationState.RecoveryInProgress,
            "RECOVERY_FAILED" => P275MigrationState.RecoveryFailed,
            _ => P275MigrationState.RecoveryRequired
        };

        return value is not null &&
            value is "READY" or
                "MIGRATION_REQUIRED" or
                "BACKUP_IN_PROGRESS" or
                "BACKUP_FAILED" or
                "MIGRATION_IN_PROGRESS" or
                "MIGRATION_FAILED" or
                "VERIFY_IN_PROGRESS" or
                "VERIFY_FAILED" or
                "PROMOTION_IN_PROGRESS" or
                "PROMOTION_UNKNOWN" or
                "RECOVERY_REQUIRED" or
                "RECOVERY_IN_PROGRESS" or
                "RECOVERY_FAILED";
    }
}

/// <summary>
/// Stable failure codes owned by the P2.75 safety contract. Adapter
/// implementations may keep richer diagnostics privately, but orchestration
/// never exposes those diagnostics as a state or user-facing error.
/// </summary>
public static class P275MigrationFailureCodes
{
    public const string ArgumentsInvalid = "migration.args_invalid";
    public const string PathInvalid = "migration.path_invalid";
    public const string Locked = "migration.locked";
    public const string SourceInvalid = "migration.source_invalid";
    public const string BackupFailed = "migration.backup_failed";
    public const string BackupUnreadable = "migration.backup_unreadable";
    public const string SourceChanged = "migration.source_changed";
    public const string IncompatibleSchema = "migration.incompatible_schema";
    public const string ApplyFailed = "migration.apply_failed";
    public const string VerifyFailed = "migration.verify_failed";
    public const string IntegrityFailed = "migration.integrity_failed";
    public const string ForeignKeyFailed = "migration.foreign_key_failed";
    public const string HistoryFailed = "migration.history_failed";
    public const string PromoteFailed = "migration.promote_failed";
    public const string PromoteUnknown = "migration.promote_unknown";
    public const string StateUnwritable = "migration.state_unwritable";
    public const string RecoveryRequired = "migration.recovery_required";
    public const string RecoveryBackupInvalid = "migration.recovery_backup_invalid";
    public const string RestoreFailed = "migration.restore_failed";

    public static bool IsStable(string? value) => IsKnown(value);

    public static bool IsKnown(string? code) => code is
        ArgumentsInvalid or
        PathInvalid or
        Locked or
        SourceInvalid or
        BackupFailed or
        BackupUnreadable or
        SourceChanged or
        IncompatibleSchema or
        ApplyFailed or
        VerifyFailed or
        IntegrityFailed or
        ForeignKeyFailed or
        HistoryFailed or
        PromoteFailed or
        PromoteUnknown or
        StateUnwritable or
        RecoveryRequired or
        RecoveryBackupInvalid or
        RestoreFailed;
}

public enum P275VerificationPhase
{
    Candidate,
    PostPromote,
    AlreadyReady
}

public enum P275PromotionOutcome
{
    Succeeded,
    ExplicitFailure,
    Unknown
}

/// <summary>
/// Canonical profile layout supplied to every P2.75 adapter. It does not
/// create directories in its constructor; the runner creates the bounded
/// runtime layout after profile validation and before attempting the lock.
/// </summary>
public sealed class P275ProfilePaths
{
    public P275ProfilePaths(string dataRoot, string profileName)
    {
        DataRoot = CanonicalizeDirectory(dataRoot, requireExisting: true);
        ProfileName = ValidateProfileName(profileName);
        ProfileRoot = CanonicalizeChild(
            ProfileName == "default"
                ? DataRoot
                : Path.Combine(DataRoot, "profiles", ProfileName),
            DataRoot);
        ActiveDatabasePath = CanonicalizeChild(
            Path.Combine(ProfileRoot, "reminnote.sqlite"),
            DataRoot);
        BackupsDirectory = CanonicalizeChild(Path.Combine(ProfileRoot, "backups"), DataRoot);
        RecoveryDirectory = CanonicalizeChild(Path.Combine(ProfileRoot, "recovery"), DataRoot);
        StagingDirectory = CanonicalizeChild(Path.Combine(RecoveryDirectory, "staging"), DataRoot);
        FailedDirectory = CanonicalizeChild(Path.Combine(RecoveryDirectory, "failed"), DataRoot);
        HistoryDirectory = CanonicalizeChild(Path.Combine(RecoveryDirectory, "history"), DataRoot);
        RuntimeDirectory = CanonicalizeChild(Path.Combine(ProfileRoot, "runtime"), DataRoot);
        MigrationLockPath = CanonicalizeChild(Path.Combine(RuntimeDirectory, "migration.lock"), DataRoot);
        WriterQuiescencePath = CanonicalizeChild(Path.Combine(RuntimeDirectory, "writer-quiescence.lock"), DataRoot);
    }

    public string DataRoot { get; }

    public string ProfileName { get; }

    public string ProfileRoot { get; }

    public string ActiveDatabasePath { get; }

    public string BackupsDirectory { get; }

    public string RecoveryDirectory { get; }

    public string StagingDirectory { get; }

    public string FailedDirectory { get; }

    public string HistoryDirectory { get; }

    public string RuntimeDirectory { get; }

    public string MigrationLockPath { get; }

    public string WriterQuiescencePath { get; }

    public P275CandidatePaths CreateCandidatePaths(string runId)
    {
        ValidateCanonicalUuid(runId, nameof(runId));
        var stagingDirectory = CanonicalizeChild(Path.Combine(StagingDirectory, runId), DataRoot);
        var failedDirectory = CanonicalizeChild(Path.Combine(FailedDirectory, runId), DataRoot);
        var historyDirectory = CanonicalizeChild(Path.Combine(HistoryDirectory, runId), DataRoot);
        return new P275CandidatePaths(
            runId,
            stagingDirectory,
            Path.Combine(stagingDirectory, "candidate.sqlite"),
            failedDirectory,
            historyDirectory);
    }

    /// <summary>
    /// Creates only directories that belong to this validated profile. Every
    /// existing directory is checked for reparse/link escape before use.
    /// </summary>
    public void EnsureRuntimeDirectories()
    {
        EnsureDirectory(DataRoot, DataRoot);
        EnsureDirectory(ProfileRoot, DataRoot);
        EnsureDirectory(BackupsDirectory, DataRoot);
        EnsureDirectory(RecoveryDirectory, DataRoot);
        EnsureDirectory(StagingDirectory, DataRoot);
        EnsureDirectory(FailedDirectory, DataRoot);
        EnsureDirectory(HistoryDirectory, DataRoot);
        EnsureDirectory(RuntimeDirectory, DataRoot);
    }

    public string GetProfileRelativeArtifact(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var canonicalPath = Path.GetFullPath(fullPath);
        if (!IsWithin(canonicalPath, ProfileRoot))
        {
            throw new ArgumentException(
                "The artifact path must remain within the profile root.",
                nameof(fullPath));
        }

        var relative = Path.GetRelativePath(ProfileRoot, canonicalPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (relative is "." or "" || relative.StartsWith("../", StringComparison.Ordinal))
        {
            throw new ArgumentException("The artifact path is not a profile-relative file.", nameof(fullPath));
        }

        return relative;
    }

    private static string CanonicalizeDirectory(string value, bool requireExisting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(value) || IsUnc(value))
        {
            throw new ArgumentException("The profile data root must be an absolute local directory.", nameof(value));
        }

        var path = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Length == 2 && path[1] == Path.VolumeSeparatorChar)
        {
            path += Path.DirectorySeparatorChar;
        }

        if (requireExisting && !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("The profile data root does not exist.");
        }

        return path;
    }

    private static string CanonicalizeChild(string path, string root)
    {
        var canonical = Path.GetFullPath(path);
        if (!IsWithin(canonical, root))
        {
            throw new ArgumentException("The profile path leaves the data root.", nameof(path));
        }

        return canonical;
    }

    private static void EnsureDirectory(string path, string root)
    {
        var canonical = CanonicalizeChild(path, root);
        var relative = Path.GetRelativePath(root, canonical);
        var current = root;
        if (relative is not "." and not "")
        {
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current))
                {
                    Directory.CreateDirectory(current);
                }

                EnsureDirectoryDoesNotEscape(current, root);
            }
        }
        else
        {
            EnsureDirectoryDoesNotEscape(current, root);
        }
    }

    private static void EnsureDirectoryDoesNotEscape(string path, string root)
    {
        var directory = new DirectoryInfo(path);
        var target = directory.ResolveLinkTarget(returnFinalTarget: true);
        if (target is not null && !IsWithin(Path.GetFullPath(target.FullName), root))
        {
            throw new IOException("The profile directory reparse target leaves the data root.");
        }
    }

    private static string ValidateProfileName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length is < 1 or > 64 || !IsLowerAsciiOrDigit(value[0]))
        {
            throw new ArgumentException("The profile key is not valid.", nameof(value));
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsLowerAsciiOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                throw new ArgumentException("The profile key is not valid.", nameof(value));
            }
        }

        return value;
    }

    private static void ValidateCanonicalUuid(string value, string parameterName)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            throw new ArgumentException("The value must be a lower-case UUID in D format.", parameterName);
        }
    }

    private static bool IsLowerAsciiOrDigit(char value) =>
        value is >= 'a' and <= 'z' || value is >= '0' and <= '9';

    private static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return path.Equals(root, comparison) ||
            path.StartsWith(
                root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
                    ? root
                    : root + Path.DirectorySeparatorChar,
                comparison);
    }

    private static bool IsUnc(string value) =>
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        value.StartsWith("//", StringComparison.Ordinal);
}

public sealed record P275CandidatePaths(
    string RunId,
    string StagingDirectory,
    string CandidateDatabasePath,
    string FailedDirectory,
    string HistoryDirectory);

/// <summary>
/// An allow-listed source/target migration pair. Target must be a contiguous
/// prefix of the migrations compiled into the approved binary, and source
/// must be its prefix. The runner never uses EF's unbounded Migrate overload.
/// </summary>
public sealed class P275MigrationPlan
{
    public P275MigrationPlan(
        IEnumerable<string> approvedSourceMigrations,
        IEnumerable<string> approvedTargetMigrations)
    {
        ArgumentNullException.ThrowIfNull(approvedSourceMigrations);
        ArgumentNullException.ThrowIfNull(approvedTargetMigrations);
        ApprovedSourceMigrations = FreezeMigrationIds(approvedSourceMigrations, nameof(approvedSourceMigrations));
        ApprovedTargetMigrations = FreezeMigrationIds(approvedTargetMigrations, nameof(approvedTargetMigrations));
        if (ApprovedTargetMigrations.Count == 0 ||
            ApprovedSourceMigrations.Count > ApprovedTargetMigrations.Count ||
            !ApprovedTargetMigrations.Take(ApprovedSourceMigrations.Count)
                .SequenceEqual(ApprovedSourceMigrations, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The approved source schema must be a prefix of a non-empty approved target schema.",
                nameof(approvedTargetMigrations));
        }
    }

    public IReadOnlyList<string> ApprovedSourceMigrations { get; }

    public IReadOnlyList<string> ApprovedTargetMigrations { get; }

    public string TargetMigrationId => ApprovedTargetMigrations[^1];

    public bool IsAlreadyAtTarget(IReadOnlyList<string> sourceMigrations) =>
        sourceMigrations.SequenceEqual(ApprovedTargetMigrations, StringComparer.Ordinal);

    public void ValidateKnownMigrations(IReadOnlyList<string> knownMigrations)
    {
        ArgumentNullException.ThrowIfNull(knownMigrations);
        if (!knownMigrations.Take(ApprovedTargetMigrations.Count)
                .SequenceEqual(ApprovedTargetMigrations, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The target migration list is not an approved prefix of the binary migrations.");
        }
    }

    public void ValidateSource(IReadOnlyList<string> sourceMigrations)
    {
        ArgumentNullException.ThrowIfNull(sourceMigrations);
        if (!sourceMigrations.SequenceEqual(ApprovedSourceMigrations, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The source migration history is not the approved source schema.");
        }
    }

    private static ReadOnlyCollection<string> FreezeMigrationIds(IEnumerable<string> values, string parameterName)
    {
        var result = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
                value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')) ||
                result.Contains(value, StringComparer.Ordinal))
            {
                throw new ArgumentException("Migration IDs must be unique bounded safe tokens.", parameterName);
            }

            result.Add(value);
        }

        return new ReadOnlyCollection<string>(result);
    }
}

public sealed record P275SourceFingerprint
{
    public P275SourceFingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
        {
            throw new ArgumentException("A source fingerprint must be bounded non-sensitive text.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
}

public sealed class P275SourceInventory
{
    public P275SourceInventory(
        bool exists,
        IEnumerable<string> appliedMigrations,
        P275SourceFingerprint fingerprint,
        P275VerificationBaseline? verificationBaseline = null)
    {
        ArgumentNullException.ThrowIfNull(appliedMigrations);
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        AppliedMigrations = new ReadOnlyCollection<string>(appliedMigrations.ToArray());
        if (!exists && AppliedMigrations.Count != 0)
        {
            throw new ArgumentException("An empty source cannot have migration history.", nameof(appliedMigrations));
        }

        Exists = exists;
        VerificationBaseline = verificationBaseline ?? P275VerificationBaseline.Empty;
    }

    public bool Exists { get; }

    public IReadOnlyList<string> AppliedMigrations { get; }

    public P275SourceFingerprint Fingerprint { get; }

    public P275VerificationBaseline VerificationBaseline { get; }
}

/// <summary>
/// Data facts captured from the source before any Candidate work starts. They
/// are carried into post-migration verification so a production verifier does
/// not derive its preservation expectations from the already-mutated Candidate.
/// </summary>
public sealed record P275VerificationBaseline(
    IReadOnlyList<P275KeyTableExpectation> KeyTables)
{
    public static P275VerificationBaseline Empty { get; } = new([]);
}

/// <summary>
/// This value is a capability returned only by the P2.75-01 adapter after it
/// has created and independently verified a Safety Backup. The runner still
/// checks its path and source identity before staging it.
/// </summary>
public sealed class P275VerifiedSafetyBackup
{
    public P275VerifiedSafetyBackup(
        string artifactId,
        string? artifactPath,
        bool sourceIsEmpty,
        IEnumerable<string> sourceMigrations,
        P275SourceFingerprint sourceFingerprint,
        long byteLength,
        string sha256)
    {
        ArtifactId = ValidateArtifactId(artifactId);
        if (sourceIsEmpty)
        {
            if (artifactPath is not null || byteLength != 0 || sha256.Length != 0)
            {
                throw new ArgumentException("An empty source backup cannot carry a file artifact.", nameof(artifactPath));
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
            if (byteLength <= 0 || !IsLowerHexSha256(sha256))
            {
                throw new ArgumentException("A verified backup must have a positive length and SHA-256.", nameof(sha256));
            }
        }

        ArgumentNullException.ThrowIfNull(sourceMigrations);
        SourceMigrations = new ReadOnlyCollection<string>(sourceMigrations.ToArray());
        SourceFingerprint = sourceFingerprint ?? throw new ArgumentNullException(nameof(sourceFingerprint));
        ArtifactPath = artifactPath is null ? null : Path.GetFullPath(artifactPath);
        SourceIsEmpty = sourceIsEmpty;
        ByteLength = byteLength;
        Sha256 = sha256;
    }

    public string ArtifactId { get; }

    public string? ArtifactPath { get; }

    public bool SourceIsEmpty { get; }

    public IReadOnlyList<string> SourceMigrations { get; }

    public P275SourceFingerprint SourceFingerprint { get; }

    public long ByteLength { get; }

    public string Sha256 { get; }

    private static string ValidateArtifactId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 240 || value is "." or ".." ||
            value.Contains('/') || value.Contains('\\') || value.Contains('\0') ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("The backup artifact ID must be one bounded file name.", nameof(value));
        }

        return value;
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record P275SafetyBackupRequest(
    P275ProfilePaths Paths,
    string ProfileScope,
    string RunId,
    P275SourceInventory Source,
    P275MigrationPlan Plan);

public sealed record P275Candidate(
    P275ProfilePaths Paths,
    P275CandidatePaths Files,
    P275VerifiedSafetyBackup Backup);

public sealed record P275VerificationRequest(
    P275Candidate? Candidate,
    P275MigrationPlan Plan,
    P275VerificationPhase Phase,
    string DatabasePath,
    P275ProfilePaths? Paths = null,
    P275VerificationBaseline? Baseline = null,
    string? RunId = null);

public sealed record P275PromotionRequest(
    P275ProfilePaths Paths,
    P275Candidate Candidate,
    P275SourceInventory SourceBeforeBackup,
    string ProfileScope);

public sealed record P275MigrationLockRequest(
    P275ProfilePaths Paths,
    string RunId,
    TimeSpan Timeout);

public sealed record P275VerificationResult(bool IsValid, string? FailureCode)
{
    public static P275VerificationResult Valid() => new(true, null);

    public static P275VerificationResult Invalid(string failureCode = P275MigrationFailureCodes.VerifyFailed)
    {
        if (!P275MigrationFailureCodes.IsStable(failureCode))
        {
            throw new ArgumentException("The verification failure code is not stable.", nameof(failureCode));
        }

        return new(false, failureCode);
    }
}

public sealed record P275CandidateFinalizationResult(bool IsValid, string? FailureCode)
{
    public static P275CandidateFinalizationResult Valid() => new(true, null);

    public static P275CandidateFinalizationResult Invalid(string failureCode = P275MigrationFailureCodes.VerifyFailed) =>
        new(false, P275MigrationFailureCodes.IsStable(failureCode)
            ? failureCode
            : throw new ArgumentException("The finalization failure code is not stable.", nameof(failureCode)));
}

public sealed record P275PromotionResult(
    P275PromotionOutcome Outcome,
    string? FailureCode,
    string? HistoryDirectoryPath)
{
    public static P275PromotionResult Succeeded(string historyDirectoryPath) =>
        new(P275PromotionOutcome.Succeeded, null, historyDirectoryPath);

    public static P275PromotionResult ExplicitFailure(string failureCode = P275MigrationFailureCodes.PromoteFailed) =>
        new(
            P275PromotionOutcome.ExplicitFailure,
            P275MigrationFailureCodes.IsStable(failureCode)
                ? failureCode
                : throw new ArgumentException("The promotion failure code is not stable.", nameof(failureCode)),
            null);

    public static P275PromotionResult Unknown() =>
        new(P275PromotionOutcome.Unknown, P275MigrationFailureCodes.PromoteUnknown, null);
}

public sealed record P275CandidateApplyResult(IReadOnlyList<string> AppliedMigrations);

public sealed record P275MigrationStateSnapshot(
    string RunId,
    string ProfileScope,
    P275MigrationState State,
    IReadOnlyList<string> SourceSchema,
    IReadOnlyList<string> TargetSchema,
    string? BackupArtifact,
    string? CandidateArtifact,
    string? FailureCode,
    bool Retryable,
    string NextAction,
    string? BackupSha256 = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? UpdatedAtUtc = null,
    string? LastAgentInstanceId = null);

public sealed record P275MigrationResult(
    string RunId,
    P275MigrationState State,
    bool Ready,
    bool Writable,
    bool Promoted,
    string? FailureCode,
    string? BackupArtifact,
    string? CandidateDatabasePath,
    string? HistoryDirectoryPath)
{
    public static P275MigrationResult Success(
        string runId,
        string? backupArtifact,
        string? candidateDatabasePath,
        string? historyDirectoryPath) =>
        new(runId, P275MigrationState.Ready, true, true, historyDirectoryPath is not null, null, backupArtifact, candidateDatabasePath, historyDirectoryPath);

    public static P275MigrationResult Failure(
        string runId,
        P275MigrationState state,
        string failureCode,
        string? backupArtifact = null,
        string? candidateDatabasePath = null,
        string? historyDirectoryPath = null,
        bool promoted = false) =>
        new(
            runId,
            state,
            Ready: false,
            Writable: false,
            promoted,
            P275MigrationFailureCodes.IsStable(failureCode)
                ? failureCode
                : throw new ArgumentException("The migration failure code is not stable.", nameof(failureCode)),
            backupArtifact,
            candidateDatabasePath,
            historyDirectoryPath);
}

public interface IP275ActiveSourceReader
{
    ValueTask<P275SourceInventory> ReadAsync(
        P275ProfilePaths paths,
        string profileScope,
        CancellationToken cancellationToken = default);
}

public interface IP275SafetyBackupProvider
{
    ValueTask<P275VerifiedSafetyBackup> CreateVerifiedBackupAsync(
        P275SafetyBackupRequest request,
        CancellationToken cancellationToken = default);
}

public interface IP275WriterQuiescence
{
    ValueTask<IP275WriterQuiescenceLease> AcquireAsync(
        P275ProfilePaths paths,
        string runId,
        CancellationToken cancellationToken = default);
}

public interface IP275WriterQuiescenceLease : IAsyncDisposable
{
}

public interface IP275ForwardMigrationApplier
{
    ValueTask<P275CandidateApplyResult> ApplyAsync(
        P275Candidate candidate,
        P275MigrationPlan plan,
        string profileScope,
        CancellationToken cancellationToken = default);
}

public interface IP275CandidateFinalizer
{
    ValueTask<P275CandidateFinalizationResult> FinalizeAsync(
        P275Candidate candidate,
        CancellationToken cancellationToken = default);
}

public interface IP275CandidateVerifier
{
    ValueTask<P275VerificationResult> VerifyAsync(
        P275VerificationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IP275AtomicPromoter
{
    ValueTask<P275PromotionResult> PromoteAsync(
        P275PromotionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IP275MigrationStatePort
{
    ValueTask RecordAsync(
        P275MigrationStateSnapshot snapshot,
        CancellationToken cancellationToken = default);

    ValueTask<P275MigrationStateReadResult> ReadAsync(
        string expectedProfileScope,
        CancellationToken cancellationToken = default);
}

public interface IP275MigrationLockProvider
{
    ValueTask<IP275MigrationLockLease?> AcquireAsync(
        P275MigrationLockRequest request,
        CancellationToken cancellationToken = default);
}

public interface IP275MigrationLockLease : IAsyncDisposable
{
}

public interface IP275MigrationRunner
{
    ValueTask<P275MigrationResult> RunAsync(
        P275MigrationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record P275MigrationRequest(
    P275ProfilePaths Paths,
    string ProfileScope,
    string RunId,
    P275MigrationPlan Plan,
    TimeSpan LockTimeout,
    bool AllowRetry = false);

public static class P275MigrationNextActions
{
    public const string None = "none";
    public const string ViewStatus = "view-status";
    public const string Retry = "retry";
    public const string RestoreBackup = "restore-backup";
    public const string Exit = "exit";

    public static bool IsKnown(string? value) => value is
        None or ViewStatus or Retry or RestoreBackup or Exit;
}

public enum P275RecoveryStatusMode
{
    Ready,
    ReadOnly,
    RecoveryRequired,
    Unavailable
}

public readonly record struct P275StateReadiness(
    bool Ready,
    bool Writable,
    bool RecoveryRequired,
    P275RecoveryStatusMode Mode);

public static class P275MigrationStatePolicy
{
    public static P275StateReadiness Evaluate(P275MigrationState state)
    {
        if (!Enum.IsDefined(state))
        {
            return new(false, false, true, P275RecoveryStatusMode.RecoveryRequired);
        }

        return state switch
        {
            P275MigrationState.Ready => new(true, true, false, P275RecoveryStatusMode.Ready),
            P275MigrationState.BackupFailed or
                P275MigrationState.MigrationFailed or
                P275MigrationState.VerifyFailed or
                P275MigrationState.PromotionUnknown or
                P275MigrationState.RecoveryRequired or
                P275MigrationState.RecoveryInProgress or
                P275MigrationState.RecoveryFailed =>
                new(false, false, true, P275RecoveryStatusMode.RecoveryRequired),
            _ => new(false, false, false, P275RecoveryStatusMode.ReadOnly)
        };
    }
}

public static class P275ArtifactNames
{
    public static string ValidateBackupArtifactId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > P275MigrationContract.MaxArtifactBytes ||
            value is "." or ".." ||
            value.Contains('\0') ||
            value.Contains('/') ||
            value.Contains('\\') ||
            !value.EndsWith(".sqlite", StringComparison.Ordinal))
        {
            throw new ArgumentException("The backup artifact id is invalid.", nameof(value));
        }

        foreach (var character in value)
        {
            if (!IsSafeArtifactCharacter(character))
            {
                throw new ArgumentException("The backup artifact id is invalid.", nameof(value));
            }
        }

        return value;
    }

    public static string ValidateRelativeArtifact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > P275MigrationContract.MaxArtifactBytes ||
            value.Contains('\0') ||
            Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("The artifact path is invalid.", nameof(value));
        }

        var normalized = value.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("The artifact path is invalid.", nameof(value));
        }

        foreach (var segment in segments)
        {
            foreach (var character in segment)
            {
                if (!IsSafeArtifactCharacter(character))
                {
                    throw new ArgumentException("The artifact path is invalid.", nameof(value));
                }
            }
        }

        return normalized;
    }

    public static string ValidateManifestArtifactId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > P275MigrationContract.MaxArtifactBytes ||
            value is "." or ".." ||
            value.Contains('\0') ||
            value.Contains('/') ||
            value.Contains('\\') ||
            !value.EndsWith(".json", StringComparison.Ordinal))
        {
            throw new ArgumentException("The manifest artifact id is invalid.", nameof(value));
        }

        foreach (var character in value)
        {
            if (!IsSafeArtifactCharacter(character))
            {
                throw new ArgumentException("The manifest artifact id is invalid.", nameof(value));
            }
        }

        return value;
    }

    private static bool IsSafeArtifactCharacter(char value) =>
        value is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9' or
            '.' or '-' or '_';
}

public sealed class P275MigrationStateMarker
{
    public P275MigrationStateMarker(
        P275MigrationState state,
        Guid runId,
        string profileScope,
        IEnumerable<string> sourceSchema,
        IEnumerable<string> targetSchema,
        string? backupArtifact,
        string? backupSha256,
        string? candidateArtifact,
        DateTimeOffset startedAtUtc,
        DateTimeOffset updatedAtUtc,
        string? failureCode,
        bool retryable,
        string nextAction,
        IEnumerable<string>? lastKnownGoodSchema = null,
        string? lastAgentInstanceId = null)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (runId == Guid.Empty)
        {
            throw new ArgumentException("RunId must be a non-empty UUID.", nameof(runId));
        }

        ProtocolProfileScope.Validate(profileScope);

        State = state;
        RunId = runId;
        ProfileScope = profileScope;
        SourceSchema = CopySchema(sourceSchema, nameof(sourceSchema));
        TargetSchema = CopySchema(targetSchema, nameof(targetSchema));
        LastKnownGoodSchema = CopySchema(
            lastKnownGoodSchema ?? Array.Empty<string>(),
            nameof(lastKnownGoodSchema));
        BackupArtifact = backupArtifact is null
            ? null
            : P275ArtifactNames.ValidateBackupArtifactId(backupArtifact);
        BackupSha256 = ValidateHash(backupSha256, nameof(backupSha256));
        CandidateArtifact = candidateArtifact is null
            ? null
            : P275ArtifactNames.ValidateRelativeArtifact(candidateArtifact);
        StartedAtUtc = ValidateUtc(startedAtUtc, nameof(startedAtUtc));
        UpdatedAtUtc = ValidateUtc(updatedAtUtc, nameof(updatedAtUtc));
        if (UpdatedAtUtc < StartedAtUtc)
        {
            throw new ArgumentException(
                "The marker update timestamp cannot precede its start timestamp.",
                nameof(updatedAtUtc));
        }
        FailureCode = ValidateFailureCode(failureCode);
        NextAction = ValidateNextAction(nextAction);
        LastAgentInstanceId = ValidateUuid(lastAgentInstanceId, nameof(lastAgentInstanceId));
        Retryable = retryable;

        if ((BackupArtifact is null) != (BackupSha256 is null))
        {
            throw new ArgumentException(
                "Backup artifact and hash must be supplied together.",
                nameof(backupArtifact));
        }

        if (State == P275MigrationState.Ready)
        {
            if (FailureCode is not null || Retryable || NextAction != P275MigrationNextActions.None)
            {
                throw new ArgumentException("READY cannot carry failure state.", nameof(state));
            }
        }
        else if (IsFailureState(State) && FailureCode is null)
        {
            throw new ArgumentException("A failed migration state requires a stable failure code.", nameof(failureCode));
        }
    }

    public P275MigrationState State { get; }

    public Guid RunId { get; }

    public string ProfileScope { get; }

    public IReadOnlyList<string> SourceSchema { get; }

    public IReadOnlyList<string> TargetSchema { get; }

    public string? BackupArtifact { get; }

    public string? BackupSha256 { get; }

    public string? CandidateArtifact { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public string? FailureCode { get; }

    public bool Retryable { get; }

    public string NextAction { get; }

    public IReadOnlyList<string> LastKnownGoodSchema { get; }

    public string? LastAgentInstanceId { get; }

    public bool Ready => P275MigrationStatePolicy.Evaluate(State).Ready;

    public bool Writable => P275MigrationStatePolicy.Evaluate(State).Writable;

    internal P275MarkerDocument ToDocument() => new(
        P275MigrationContract.Version,
        P275MigrationStateNames.ToValue(State),
        RunId.ToString("D"),
        ProfileScope,
        SourceSchema,
        TargetSchema,
        BackupArtifact,
        BackupSha256,
        CandidateArtifact,
        FormatUtc(StartedAtUtc),
        FormatUtc(UpdatedAtUtc),
        FailureCode,
        Retryable,
        NextAction,
        LastKnownGoodSchema,
        LastAgentInstanceId);

    internal static P275MigrationStateMarker FromDocument(P275MarkerDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(document.ContractVersion, P275MigrationContract.Version, StringComparison.Ordinal) ||
            !P275MigrationStateNames.TryParse(document.State, out var state) ||
            !Guid.TryParseExact(document.RunId, "D", out var runId) ||
            !string.Equals(document.RunId, runId.ToString("D"), StringComparison.Ordinal) ||
            !TryParseUtc(document.StartedAtUtc, out var startedAtUtc) ||
            !TryParseUtc(document.UpdatedAtUtc, out var updatedAtUtc))
        {
            throw new FormatException("The migration marker is not valid.");
        }

        return new P275MigrationStateMarker(
            state,
            runId,
            document.ProfileScope,
            document.SourceSchema,
            document.TargetSchema,
            document.BackupArtifact,
            document.BackupSha256,
            document.CandidateArtifact,
            startedAtUtc,
            updatedAtUtc,
            document.FailureCode,
            document.Retryable,
            document.NextAction,
            document.LastKnownGoodSchema,
            document.LastAgentInstanceId);
    }

    private static ReadOnlyCollection<string> CopySchema(IEnumerable<string> values, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new List<string>();
        foreach (var value in values)
        {
            if (copy.Count >= P275MigrationContract.MaxSchemaItems)
            {
                throw new ArgumentException("The schema list is too large.", fieldName);
            }

            if (string.IsNullOrWhiteSpace(value) ||
                value != "empty" &&
                System.Text.Encoding.UTF8.GetByteCount(value) > P275MigrationContract.MaxMigrationIdBytes ||
                value != "empty" &&
                value.Any(character => !IsSchemaCharacter(character)))
            {
                throw new ArgumentException("The schema migration id is invalid.", fieldName);
            }

            copy.Add(value);
        }

        if (copy.Count != copy.Distinct(StringComparer.Ordinal).Count())
        {
            throw new ArgumentException("The schema list cannot contain duplicates.", fieldName);
        }

        if (copy.Contains("empty", StringComparer.Ordinal) && copy.Count != 1)
        {
            throw new ArgumentException("The empty schema sentinel cannot be combined with migration ids.", fieldName);
        }

        return new ReadOnlyCollection<string>(copy);
    }

    private static string? ValidateHash(string? value, string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length != 64 || value.Any(character => !IsLowerHex(character)))
        {
            throw new ArgumentException("The hash must be 64 lowercase hexadecimal characters.", fieldName);
        }

        return value;
    }

    private static string? ValidateFailureCode(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!P275MigrationFailureCodes.IsKnown(value))
        {
            throw new ArgumentException("The migration failure code is not in the stable contract.", nameof(value));
        }

        return value;
    }

    private static string ValidateNextAction(string value)
    {
        if (!P275MigrationNextActions.IsKnown(value) ||
            System.Text.Encoding.UTF8.GetByteCount(value) > P275MigrationContract.MaxNextActionBytes)
        {
            throw new ArgumentException("The migration next action is invalid.", nameof(value));
        }

        return value;
    }

    private static string? ValidateUuid(string? value, string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new ArgumentException("The value must be a lowercase UUID.", fieldName);
        }

        return value;
    }

    private static DateTimeOffset ValidateUtc(DateTimeOffset value, string fieldName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The marker timestamp must be UTC.", fieldName);
        }

        return value;
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static bool TryParseUtc(string? value, out DateTimeOffset result)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out result))
        {
            return false;
        }

        return string.Equals(FormatUtc(result), value, StringComparison.Ordinal);
    }

    private static bool IsFailureState(P275MigrationState state) => state is
        P275MigrationState.BackupFailed or
        P275MigrationState.MigrationFailed or
        P275MigrationState.VerifyFailed or
        P275MigrationState.PromotionUnknown or
        P275MigrationState.RecoveryRequired or
        P275MigrationState.RecoveryFailed;

    private static bool IsSchemaCharacter(char value) =>
        value is >= 'a' and <= 'z' or
            >= 'A' and <= 'Z' or
            >= '0' and <= '9' or
            '.' or '-' or '_';

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}

internal sealed record P275MarkerDocument(
    [property: JsonPropertyName("contractVersion")] string ContractVersion,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("profileScope")] string ProfileScope,
    [property: JsonPropertyName("sourceSchema")] IReadOnlyList<string> SourceSchema,
    [property: JsonPropertyName("targetSchema")] IReadOnlyList<string> TargetSchema,
    [property: JsonPropertyName("backupArtifact")] string? BackupArtifact,
    [property: JsonPropertyName("backupSha256")] string? BackupSha256,
    [property: JsonPropertyName("candidateArtifact")] string? CandidateArtifact,
    [property: JsonPropertyName("startedAtUtc")] string StartedAtUtc,
    [property: JsonPropertyName("updatedAtUtc")] string UpdatedAtUtc,
    [property: JsonPropertyName("failureCode")] string? FailureCode,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("nextAction")] string NextAction,
    [property: JsonPropertyName("lastKnownGoodSchema")] IReadOnlyList<string> LastKnownGoodSchema,
    [property: JsonPropertyName("lastAgentInstanceId")] string? LastAgentInstanceId);
