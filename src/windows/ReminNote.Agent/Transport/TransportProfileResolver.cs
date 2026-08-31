using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

namespace ReminNote.Agent.Transport;

internal sealed record TransportProfileArguments(
    string? DataRoot = null,
    string? RepoRoot = null,
    string? Profile = null);

/// <summary>
/// Profile data used by the Agent transport endpoints. ProfileScope and the
/// endpoint names are supplied by the single P2.5-01 protocol adapter.
/// </summary>
internal sealed class ResolvedTransportProfile
{
    internal ResolvedTransportProfile(
        string userSid,
        string profileName,
        string dataRoot,
        string databasePath,
        string profileScope,
        string businessPipeName,
        string controlPipeName)
    {
        UserSid = userSid;
        ProfileName = profileName;
        DataRoot = dataRoot;
        DatabasePath = databasePath;
        ProfileScope = profileScope;
        BusinessPipeName = businessPipeName;
        ControlPipeName = controlPipeName;
    }

    internal string UserSid { get; }

    internal string ProfileName { get; }

    internal string DataRoot { get; }

    internal string DatabasePath { get; }

    internal string ProfileScope { get; }

    internal string BusinessPipeName { get; }

    internal string ControlPipeName { get; }
}

internal enum TransportProfileFailureKind
{
    RootRequired,
    AmbiguousRoot,
    InvalidRoot,
    InvalidProfile,
    InvalidUserIdentity,
    ProfileContractUnavailable
}

internal sealed class TransportProfileResolutionException : ArgumentException
{
    public TransportProfileResolutionException(
        TransportProfileFailureKind failureKind,
        string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    internal TransportProfileFailureKind FailureKind { get; }
}

/// <summary>
/// Explicit integration seam for the unique P2.5-01 protocol contract.
/// GetProfileScope must call ReminNote.Core.Protocol.ProtocolProfileScope;
/// this transport project intentionally contains neither its hash formula
/// nor its prefix/validation constants. Endpoint names are also supplied by
/// the serial integration owner so this slice cannot create a second naming
/// authority.
/// </summary>
internal interface ITransportProfileContractAdapter
{
    string GetProfileScope(string actualUserSid, string canonicalDatabasePath);

    string GetBusinessPipeName(string profileScope);

    string GetControlPipeName(string profileScope);
}

[SupportedOSPlatform("windows")]
internal static class TransportProfileResolver
{
    internal static ResolvedTransportProfile ResolveForCurrentUser(
        TransportProfileArguments arguments,
        ITransportProfileContractAdapter contractAdapter) =>
        Resolve(arguments, WindowsUserIdentity.GetCurrentSid(), contractAdapter);

    internal static ResolvedTransportProfile Resolve(
        TransportProfileArguments arguments,
        string actualUserSid,
        ITransportProfileContractAdapter contractAdapter)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(contractAdapter);
        var normalizedSid = WindowsUserIdentity.NormalizeSid(actualUserSid);

        var hasDataRoot = arguments.DataRoot is not null;
        var hasRepoRoot = arguments.RepoRoot is not null;
        if (hasDataRoot && hasRepoRoot)
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.AmbiguousRoot,
                "Exactly one of data-root and repo-root may be provided.");
        }

        if (!hasDataRoot && !hasRepoRoot)
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.RootRequired,
                "An explicit data-root or repo-root is required.");
        }

        var profileName = NormalizeProfileName(arguments.Profile);
        var dataRoot = hasDataRoot
            ? CanonicalizeExistingLocalDirectory(arguments.DataRoot!, requireRepositoryMarkers: false)
            : ResolveRepositoryDataRoot(arguments.RepoRoot!);
        var databaseDirectory = ResolveDatabaseDirectory(dataRoot, profileName);
        var databasePath = ResolveDatabasePath(dataRoot, databaseDirectory);
        var profileScope = RequireContractValue(
            contractAdapter.GetProfileScope(normalizedSid, databasePath),
            nameof(ITransportProfileContractAdapter.GetProfileScope));
        var businessPipeName = RequireContractValue(
            contractAdapter.GetBusinessPipeName(profileScope),
            nameof(ITransportProfileContractAdapter.GetBusinessPipeName));
        var controlPipeName = RequireContractValue(
            contractAdapter.GetControlPipeName(profileScope),
            nameof(ITransportProfileContractAdapter.GetControlPipeName));
        if (businessPipeName.Equals(controlPipeName, StringComparison.Ordinal))
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.ProfileContractUnavailable,
                "Business and control pipes must remain separate.");
        }

        return new ResolvedTransportProfile(
            normalizedSid,
            profileName,
            dataRoot,
            databasePath,
            profileScope,
            businessPipeName,
            controlPipeName);
    }

    private static string ResolveRepositoryDataRoot(string repositoryRoot)
    {
        var canonicalRepositoryRoot = CanonicalizeExistingLocalDirectory(
            repositoryRoot,
            requireRepositoryMarkers: true);
        var dataRoot = NormalizeCanonicalPath(Path.Combine(canonicalRepositoryRoot, ".devdata"));
        if (!Directory.Exists(dataRoot))
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidRoot,
                "The repository data directory does not exist.");
        }

        return CanonicalizeExistingLocalDirectory(dataRoot, requireRepositoryMarkers: false);
    }

    private static string ResolveDatabaseDirectory(string dataRoot, string profileName)
    {
        if (profileName == "default")
        {
            return dataRoot;
        }

        var profilesRoot = NormalizeCanonicalPath(Path.Combine(dataRoot, "profiles"));
        if (Directory.Exists(profilesRoot))
        {
            profilesRoot = EnsureWithinRoot(
                CanonicalizeExistingLocalDirectory(profilesRoot, requireRepositoryMarkers: false),
                dataRoot);
        }

        var profileDirectory = NormalizeCanonicalPath(Path.Combine(profilesRoot, profileName));
        if (Directory.Exists(profileDirectory))
        {
            profileDirectory = EnsureWithinRoot(
                CanonicalizeExistingLocalDirectory(profileDirectory, requireRepositoryMarkers: false),
                dataRoot);
        }

        return EnsureWithinRoot(profileDirectory, dataRoot);
    }

    private static string ResolveDatabasePath(string dataRoot, string databaseDirectory)
    {
        var candidate = EnsureWithinRoot(
            NormalizeCanonicalPath(Path.Combine(databaseDirectory, "reminnote.sqlite")),
            dataRoot);

        if (Directory.Exists(candidate))
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidRoot,
                "The profile database path must be a file, not a directory.");
        }

        try
        {
            var databaseFile = new FileInfo(candidate);
            if (!IsReparsePoint(databaseFile))
            {
                return candidate;
            }

            var resolvedTarget = databaseFile.ResolveLinkTarget(returnFinalTarget: true);
            var finalPath = NormalizeCanonicalPath(
                resolvedTarget?.FullName ?? databaseFile.FullName);
            if (Directory.Exists(finalPath))
            {
                throw new TransportProfileResolutionException(
                    TransportProfileFailureKind.InvalidRoot,
                    "The profile database reparse target must be a file.");
            }

            return EnsureWithinRoot(finalPath, dataRoot);
        }
        catch (TransportProfileResolutionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SecurityException)
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidRoot,
                "The profile database path could not be resolved.");
        }
    }

    private static bool IsReparsePoint(FileSystemInfo fileSystemInfo)
    {
        // On Windows, FileInfo.Attributes is -1 for a missing file. Treat the
        // not-yet-created Agent database as an ordinary path so the Agent can
        // create it and apply the formal EF migration during cold start.
        if (!fileSystemInfo.Exists)
        {
            return false;
        }

        try
        {
            return (fileSystemInfo.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            // A missing database is a valid path for a later Agent-owned
            // initialization. There is no file reparse target to escape from.
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static string CanonicalizeExistingLocalDirectory(
        string value,
        bool requireRepositoryMarkers)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || IsUnc(value))
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidRoot,
                "The transport root must be an absolute local directory.");
        }

        string fullPath;
        try
        {
            fullPath = NormalizeCanonicalPath(Path.GetFullPath(value));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            IOException or
            SecurityException)
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidRoot,
                "The transport root could not be canonicalized.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidRoot,
                "The transport root directory does not exist.");
        }

        string finalPath;
        try
        {
            var directory = new DirectoryInfo(fullPath);
            var resolvedTarget = directory.ResolveLinkTarget(returnFinalTarget: true);
            finalPath = NormalizeCanonicalPath(resolvedTarget?.FullName ?? directory.FullName);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SecurityException)
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidRoot,
                "The transport root could not be resolved.");
        }

        if (IsUnc(finalPath) || !Directory.Exists(finalPath))
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidRoot,
                "UNC, remote or non-directory transport roots are not allowed.");
        }

        var hasSolutionMarker =
            File.Exists(Path.Combine(finalPath, "ReminNote.sln")) ||
            Directory.Exists(Path.Combine(finalPath, "ReminNote.sln"));
        var hasGitMarker =
            File.Exists(Path.Combine(finalPath, ".git")) ||
            Directory.Exists(Path.Combine(finalPath, ".git"));
        if (requireRepositoryMarkers && (!hasSolutionMarker || !hasGitMarker))
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidRoot,
                "The repository root markers are missing.");
        }

        return finalPath;
    }

    private static string NormalizeProfileName(string? value)
    {
        var profile = value ?? "default";
        if (profile.Length is < 1 or > 64 || !IsAsciiLowerOrDigit(profile[0]))
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidProfile,
                "The profile key is not valid.");
        }

        for (var index = 1; index < profile.Length; index++)
        {
            var character = profile[index];
            if (!IsAsciiLowerOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                throw new TransportProfileResolutionException(
                    TransportProfileFailureKind.InvalidProfile,
                    "The profile key is not valid.");
            }
        }

        return profile;
    }

    private static bool IsAsciiLowerOrDigit(char value) =>
        value is >= 'a' and <= 'z' || value is >= '0' and <= '9';

    private static string EnsureWithinRoot(string path, string root)
    {
        if (!IsWithinRoot(path, root))
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidRoot,
                "The profile path leaves the data root.");
        }

        return path;
    }

    private static bool IsWithinRoot(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(
            root.EndsWith('\\') ? root : string.Concat(root, '\\'),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCanonicalPath(string value)
    {
        var normalized = value.Replace('/', '\\').TrimEnd('\\');
        if (normalized.Length == 2 && normalized[1] == ':')
        {
            normalized += '\\';
        }

        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            normalized = char.ToUpperInvariant(normalized[0]) + normalized[1..];
        }

        return normalized.ToUpperInvariant();
    }

    private static string RequireContractValue(string value, string contractMember)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.ProfileContractUnavailable,
                $"The transport profile contract did not provide {contractMember}.");
        }

        return value;
    }

    private static bool IsUnc(string value) =>
        value.StartsWith("\\\\", StringComparison.Ordinal) ||
        value.StartsWith("//", StringComparison.Ordinal);
}

[SupportedOSPlatform("windows")]
internal static class WindowsUserIdentity
{
    internal static string GetCurrentSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Transport identity requires Windows.");
        }

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = identity.User?.Value;
        if (sid is null)
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidUserIdentity,
                "The current Windows user SID is unavailable.");
        }

        return NormalizeSid(sid);
    }

    internal static string NormalizeSid(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !OperatingSystem.IsWindows())
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidUserIdentity,
                "A Windows user SID is required.");
        }

        try
        {
            return new SecurityIdentifier(value).Value;
        }
        catch (ArgumentException)
        {
            throw new TransportProfileResolutionException(
                TransportProfileFailureKind.InvalidUserIdentity,
                "The Windows user SID is invalid.");
        }
    }
}
