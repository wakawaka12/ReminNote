using System.Text.Json.Serialization;

namespace ReminNote.Infrastructure.Persistence.Backup;

/// <summary>
/// Bounded values owned by the P2.75-01 backup seam. These values describe
/// the artifact format only; they do not implement migration or recovery.
/// </summary>
public static class SafetyBackupContract
{
    public const string ContractVersion = "RN-P275-SAFETY-1.0.0";

    public const string VerifiedResult = "verified";

    public const int MaxManifestBytes = 8 * 1024;

    public const int MaxSafeFileNameChars = 240;

    public const int MaxSafeArtifactPathChars = 240;

    public const int MaxSchemaEntries = 128;

    public const int MaxMigrationIdBytes = 128;

    public const int MaxProfileScopeBytes = 128;

    public const long FreeSpaceReserveBytes = 64 * 1024;
}

/// <summary>
/// Stable failure codes exposed by the backup seam. The service deliberately
/// does not return raw exception text, source paths, or user data.
/// </summary>
public static class SafetyBackupFailureCodes
{
    public const string ArgumentsInvalid = "migration.args_invalid";

    public const string PathInvalid = "migration.path_invalid";

    public const string SourceInvalid = "migration.source_invalid";

    public const string BackupFailed = "migration.backup_failed";

    public const string BackupUnreadable = "migration.backup_unreadable";

    public const string IntegrityFailed = "migration.integrity_failed";

    public const string ForeignKeyFailed = "migration.foreign_key_failed";

    public const string SourceChanged = "migration.source_changed";
}

/// <summary>
/// Explicit profile-bound inputs for one backup attempt. The caller owns the
/// migration lifecycle and supplies the already-approved target schema. The
/// service reads source schema history from the Active DB instead of trusting
/// a caller-provided source value.
/// </summary>
public sealed record SafetyBackupRequest(
    string DataRoot,
    string ProfileRoot,
    string ActiveDatabasePath,
    string ProfileScope,
    IReadOnlyList<string> TargetSchema,
    Guid RunId,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Test-only fault hooks for deterministic isolated tests. Production
/// composition must leave this value null. Hooks are never serialized into a
/// manifest and their exceptions are converted to stable failure codes.
/// </summary>
public sealed record SafetyBackupTestHooks(
    Func<long, long>? OverrideAvailableFreeBytes = null,
    Action? BeforeDatabaseWriteThrough = null,
    Action? BeforeManifestWriteThrough = null);

public sealed record SafetyBackupSidecarFingerprint(
    [property: JsonPropertyName("present")] bool Present,
    [property: JsonPropertyName("byteLength")] long ByteLength,
    [property: JsonPropertyName("sha256")] string? Sha256);

public sealed record SafetyBackupSourceFingerprint(
    [property: JsonPropertyName("byteLength")] long ByteLength,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("schemaHistorySha256")] string SchemaHistorySha256,
    [property: JsonPropertyName("wal")] SafetyBackupSidecarFingerprint Wal,
    [property: JsonPropertyName("shm")] SafetyBackupSidecarFingerprint Shm,
    [property: JsonPropertyName("journal")] SafetyBackupSidecarFingerprint Journal);

/// <summary>
/// Bounded, non-sensitive metadata written next to a verified backup. It has
/// no absolute path, task/history content, credential, token, or exception
/// field by construction.
/// </summary>
public sealed record SafetyBackupManifest(
    [property: JsonPropertyName("contractVersion")] string ContractVersion,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("profileScope")] string ProfileScope,
    [property: JsonPropertyName("sourceSchema")] IReadOnlyList<string> SourceSchema,
    [property: JsonPropertyName("targetSchema")] IReadOnlyList<string> TargetSchema,
    [property: JsonPropertyName("createdAtUtc")] string CreatedAtUtc,
    [property: JsonPropertyName("artifact")] string Artifact,
    [property: JsonPropertyName("byteLength")] long ByteLength,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sourceFingerprint")] SafetyBackupSourceFingerprint SourceFingerprint,
    [property: JsonPropertyName("result")] string Result);

/// <summary>
/// Result of one attempt. Successful artifacts are identified only by their
/// profile-relative file names; failure results intentionally contain no
/// filesystem path or original exception text.
/// </summary>
public sealed record SafetyBackupResult(
    bool IsVerified,
    string? FailureCode,
    string? Artifact,
    string? ManifestArtifact,
    long? ByteLength,
    string? Sha256,
    SafetyBackupManifest? Manifest)
{
    public static SafetyBackupResult Failed(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        return new(
            IsVerified: false,
            FailureCode: failureCode,
            Artifact: null,
            ManifestArtifact: null,
            ByteLength: null,
            Sha256: null,
            Manifest: null);
    }

    public static SafetyBackupResult Verified(
        SafetyBackupManifest manifest,
        string manifestArtifact)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestArtifact);
        return new(
            IsVerified: true,
            FailureCode: null,
            Artifact: manifest.Artifact,
            ManifestArtifact: manifestArtifact,
            ByteLength: manifest.ByteLength,
            Sha256: manifest.Sha256,
            Manifest: manifest);
    }
}
