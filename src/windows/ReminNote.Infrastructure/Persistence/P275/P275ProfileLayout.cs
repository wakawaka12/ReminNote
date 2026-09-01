using System.Security;

namespace ReminNote.Infrastructure.Persistence.P275;

/// <summary>
/// The profile-local paths used by the P2.75 marker and Candidate verifier.
/// The class never creates a profile and never resolves a caller-supplied
/// database path. It only derives fixed children from an already resolved
/// profile root.
/// </summary>
public sealed class P275ProfileLayout
{
    public P275ProfileLayout(string profileRoot)
    {
        if (string.IsNullOrWhiteSpace(profileRoot) ||
            !Path.IsPathFullyQualified(profileRoot) ||
            IsUnc(profileRoot))
        {
            throw new P275PathValidationException(P275MigrationFailureCodes.PathInvalid);
        }

        try
        {
            ProfileRoot = Normalize(Path.GetFullPath(profileRoot));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            IOException or
            SecurityException)
        {
            throw new P275PathValidationException(P275MigrationFailureCodes.PathInvalid, exception);
        }

        RuntimeDirectory = Path.Combine(ProfileRoot, "runtime");
        BackupsDirectory = Path.Combine(ProfileRoot, "backups");
        RecoveryDirectory = Path.Combine(ProfileRoot, "recovery");
        StagingDirectory = Path.Combine(RecoveryDirectory, "staging");
        FailedDirectory = Path.Combine(RecoveryDirectory, "failed");
        MigrationStatePath = Path.Combine(RuntimeDirectory, "migration-state.json");
        ActiveDatabasePath = Path.Combine(ProfileRoot, "reminnote.sqlite");

        EnsureWithinProfile(ProfileRoot, RuntimeDirectory);
        EnsureWithinProfile(ProfileRoot, BackupsDirectory);
        EnsureWithinProfile(ProfileRoot, RecoveryDirectory);
        EnsureWithinProfile(ProfileRoot, StagingDirectory);
        EnsureWithinProfile(ProfileRoot, FailedDirectory);
        EnsureWithinProfile(ProfileRoot, MigrationStatePath);
        EnsureWithinProfile(ProfileRoot, ActiveDatabasePath);
    }

    public string ProfileRoot { get; }

    public string RuntimeDirectory { get; }

    public string BackupsDirectory { get; }

    public string RecoveryDirectory { get; }

    public string StagingDirectory { get; }

    public string FailedDirectory { get; }

    public string MigrationStatePath { get; }

    public string ActiveDatabasePath { get; }

    public string GetCandidatePath(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new P275PathValidationException(P275MigrationFailureCodes.PathInvalid);
        }

        var path = Path.Combine(StagingDirectory, runId.ToString("D"), "candidate.sqlite");
        EnsureWithinProfile(ProfileRoot, path);
        return path;
    }

    public static string GetCandidateArtifact(Guid runId) =>
        $"recovery/staging/{runId:D}/candidate.sqlite";

    public static string GetActiveArtifact() => "reminnote.sqlite";

    public void EnsureActiveForRead()
    {
        EnsureProfileRootForRead();
        EnsureExistingFile(ActiveDatabasePath);
        EnsureNotReparse(ActiveDatabasePath);
    }

    public string GetBackupPath(string artifactId)
    {
        try
        {
            P275ArtifactNames.ValidateBackupArtifactId(artifactId);
        }
        catch (ArgumentException exception)
        {
            throw new P275PathValidationException(P275MigrationFailureCodes.PathInvalid, exception);
        }

        var path = Path.Combine(BackupsDirectory, artifactId);
        EnsureWithinProfile(ProfileRoot, path);
        return path;
    }

    public void EnsureProfileRootForRead()
    {
        EnsureExistingDirectory(ProfileRoot);
        EnsureNotReparse(ProfileRoot);
    }

    public void EnsureRuntimeDirectoryForWrite()
    {
        EnsureProfileRootForRead();
        EnsureDirectoryIfMissing(RuntimeDirectory);
        EnsureNotReparse(RuntimeDirectory);
    }

    public void EnsureCandidateForRead(Guid runId)
    {
        EnsureProfileRootForRead();
        EnsureExistingDirectory(StagingDirectory);
        EnsureNotReparse(StagingDirectory);
        var runDirectory = Path.Combine(StagingDirectory, runId.ToString("D"));
        EnsureExistingDirectory(runDirectory);
        EnsureNotReparse(runDirectory);
        var candidatePath = GetCandidatePath(runId);
        EnsureExistingFile(candidatePath);
        EnsureNotReparse(candidatePath);
    }

    public void EnsureBackupForRead(string artifactId)
    {
        EnsureProfileRootForRead();
        EnsureExistingDirectory(BackupsDirectory);
        EnsureNotReparse(BackupsDirectory);
        var path = GetBackupPath(artifactId);
        EnsureExistingFile(path);
        EnsureNotReparse(path);
    }

    private static void EnsureExistingDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new P275PathValidationException(P275MigrationFailureCodes.PathInvalid);
        }
    }

    private static void EnsureDirectoryIfMissing(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SecurityException)
        {
            throw new P275PathValidationException(P275MigrationFailureCodes.PathInvalid, exception);
        }
    }

    private static void EnsureExistingFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new P275PathValidationException(P275MigrationFailureCodes.PathInvalid);
        }
    }

    private static void EnsureNotReparse(string path)
    {
        try
        {
            FileSystemInfo info = File.Exists(path)
                ? new FileInfo(path)
                : new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new P275PathValidationException(P275MigrationFailureCodes.PathInvalid);
            }
        }
        catch (P275PathValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SecurityException)
        {
            throw new P275PathValidationException(P275MigrationFailureCodes.PathInvalid, exception);
        }
    }

    private static void EnsureWithinProfile(string root, string candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        if (!normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !normalizedCandidate.StartsWith(
                normalizedRoot.EndsWith('\\')
                    ? normalizedRoot
                    : string.Concat(normalizedRoot, '\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new P275PathValidationException(P275MigrationFailureCodes.PathInvalid);
        }
    }

    private static string Normalize(string value)
    {
        var normalized = value.Replace('/', '\\').TrimEnd('\\');
        if (normalized.Length == 2 && normalized[1] == ':')
        {
            normalized += '\\';
        }

        return normalized;
    }

    private static bool IsUnc(string value) =>
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        value.StartsWith("//", StringComparison.Ordinal);
}

public sealed class P275PathValidationException : IOException
{
    public P275PathValidationException(string failureCode, Exception? innerException = null)
        : base(failureCode, innerException)
    {
        FailureCode = failureCode;
    }

    public string FailureCode { get; }
}
