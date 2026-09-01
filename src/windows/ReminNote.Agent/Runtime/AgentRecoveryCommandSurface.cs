using System.Runtime.Versioning;
using ReminNote.Agent.Transport;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Agent.Runtime;

public enum AgentRecoveryCommandKind
{
    Status,
    Restore
}

/// <summary>
/// The bounded command shape shared by Bootstrap and Agent. It carries an
/// artifact id only; it never accepts a raw database path or user content.
/// </summary>
public sealed record AgentRecoveryCommandLine(
    string? DataRoot,
    string? RepoRoot,
    string? ProfileName,
    AgentRecoveryCommandKind Kind,
    string? BackupArtifactId,
    bool Confirm)
{
    public static bool IsRecoveryInvocation(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(argument =>
            argument is "--recovery-status" or "--recovery-restore");
    }

    public static AgentRecoveryCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? dataRoot = null;
        string? repoRoot = null;
        string? profileName = null;
        string? artifactId = null;
        var status = false;
        var confirm = false;
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--data-root":
                    if (dataRoot is not null)
                    {
                        throw new AgentRecoveryCommandException(P275MigrationFailureCodes.ArgumentsInvalid);
                    }

                    dataRoot = ReadValue(args, ref index, option);
                    break;
                case "--repo-root":
                    if (repoRoot is not null)
                    {
                        throw new AgentRecoveryCommandException(P275MigrationFailureCodes.ArgumentsInvalid);
                    }

                    repoRoot = ReadValue(args, ref index, option);
                    break;
                case "--profile":
                    if (profileName is not null)
                    {
                        throw new AgentRecoveryCommandException(P275MigrationFailureCodes.ArgumentsInvalid);
                    }

                    profileName = ReadValue(args, ref index, option);
                    break;
                case "--recovery-status":
                    if (status || artifactId is not null)
                    {
                        throw new AgentRecoveryCommandException(P275MigrationFailureCodes.ArgumentsInvalid);
                    }

                    status = true;
                    break;
                case "--recovery-restore":
                    if (status || artifactId is not null)
                    {
                        throw new AgentRecoveryCommandException(P275MigrationFailureCodes.ArgumentsInvalid);
                    }

                    artifactId = ReadValue(args, ref index, option);
                    try
                    {
                        P275ArtifactNames.ValidateBackupArtifactId(artifactId);
                    }
                    catch (ArgumentException exception)
                    {
                        throw new AgentRecoveryCommandException(
                            P275MigrationFailureCodes.ArgumentsInvalid,
                            exception);
                    }

                    break;
                case "--confirm":
                    if (confirm)
                    {
                        throw new AgentRecoveryCommandException(P275MigrationFailureCodes.ArgumentsInvalid);
                    }

                    confirm = true;
                    break;
                default:
                    throw new AgentRecoveryCommandException(P275MigrationFailureCodes.ArgumentsInvalid);
            }
        }

        if ((dataRoot is not null && repoRoot is not null) ||
            (!status && artifactId is null) ||
            (artifactId is not null && !confirm) ||
            (status && confirm))
        {
            throw new AgentRecoveryCommandException(P275MigrationFailureCodes.ArgumentsInvalid);
        }

        return new(
            dataRoot,
            repoRoot,
            profileName,
            artifactId is null ? AgentRecoveryCommandKind.Status : AgentRecoveryCommandKind.Restore,
            artifactId,
            confirm);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count ||
            string.IsNullOrWhiteSpace(args[index]) ||
            args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new AgentRecoveryCommandException(P275MigrationFailureCodes.ArgumentsInvalid);
        }

        return args[index];
    }
}

public sealed class AgentRecoveryCommandException : ArgumentException
{
    public AgentRecoveryCommandException(
        string failureCode,
        Exception? innerException = null)
        : base(failureCode, innerException)
    {
        FailureCode = failureCode;
    }

    public string FailureCode { get; }
}

/// <summary>
/// Resolved profile metadata for recovery operations. Absolute paths stay in
/// the Agent process and are never serialized into the status response.
/// </summary>
public sealed record AgentRecoveryProfile(
    string DataRoot,
    string ProfileName,
    string ProfileRoot,
    string DatabasePath,
    string ProfileScope)
{
    public P275ProfileLayout Layout => new(ProfileRoot);
}

public sealed record AgentRecoveryRestoreRequest(
    AgentRecoveryProfile Profile,
    string BackupArtifactId);

public sealed record AgentRecoveryOperationResult(
    bool Succeeded,
    string? FailureCode)
{
    public static AgentRecoveryOperationResult Failed(string failureCode) => new(false, failureCode);

    public static AgentRecoveryOperationResult SucceededResult() => new(true, null);
}

/// <summary>
/// Sole Agent-side seam for recovery writes. Implementations owned by the
/// Agent may open SQLite, create a recovery Candidate and later integrate the
/// approved verify/promote pipeline. Bootstrap and this adapter do not perform
/// those operations.
/// </summary>
public interface IAgentRecoveryActor
{
    ValueTask<AgentRecoveryOperationResult> RestoreAsync(
        AgentRecoveryRestoreRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Converts the CLI request to the existing P2.5 profile resolver and exposes
/// bounded marker/status operations. It contains no migration or restore
/// implementation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentRecoveryCommandAdapter
{
    private readonly ITransportProfileContractAdapter profileContractAdapter;

    public AgentRecoveryCommandAdapter()
        : this(new ProtocolTransportProfileContractAdapter())
    {
    }

    internal AgentRecoveryCommandAdapter(ITransportProfileContractAdapter profileContractAdapter)
    {
        this.profileContractAdapter = profileContractAdapter ??
            throw new ArgumentNullException(nameof(profileContractAdapter));
    }

    public AgentRecoveryProfile ResolveProfile(AgentRecoveryCommandLine command)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var profile = TransportProfileResolver.ResolveForCurrentUser(
                new TransportProfileArguments(
                    command.DataRoot,
                    command.RepoRoot,
                    command.ProfileName),
                profileContractAdapter);
            var profileRoot = profile.ProfileName == "default"
                ? profile.DataRoot
                : Path.Combine(profile.DataRoot, "profiles", profile.ProfileName);
            return new(
                profile.DataRoot,
                profile.ProfileName,
                Path.GetFullPath(profileRoot),
                profile.DatabasePath,
                profile.ProfileScope);
        }
        catch (TransportProfileResolutionException exception)
        {
            throw new AgentRecoveryCommandException(
                exception.FailureKind is
                    TransportProfileFailureKind.AmbiguousRoot or
                    TransportProfileFailureKind.RootRequired or
                    TransportProfileFailureKind.InvalidProfile
                    ? P275MigrationFailureCodes.ArgumentsInvalid
                    : P275MigrationFailureCodes.PathInvalid,
                exception);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            PlatformNotSupportedException)
        {
            throw new AgentRecoveryCommandException(P275MigrationFailureCodes.PathInvalid, exception);
        }
    }

    public static async ValueTask<P275RecoveryStatus> ReadStatusAsync(
        AgentRecoveryProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var reader = new P275RecoveryStatusReader(profile.Layout);
        return await reader.ReadAsync(profile.ProfileScope, cancellationToken).ConfigureAwait(false);
    }

    public static AgentRecoveryRestoreRequest CreateRestoreRequest(
        AgentRecoveryProfile profile,
        AgentRecoveryCommandLine command)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind != AgentRecoveryCommandKind.Restore ||
            command.BackupArtifactId is null ||
            !command.Confirm)
        {
            throw new AgentRecoveryCommandException(P275MigrationFailureCodes.ArgumentsInvalid);
        }

        try
        {
            _ = profile.Layout.GetBackupPath(command.BackupArtifactId);
        }
        catch (P275PathValidationException exception)
        {
            throw new AgentRecoveryCommandException(exception.FailureCode, exception);
        }

        return new(profile, command.BackupArtifactId);
    }
}

/// <summary>
/// Executes a recovery command after profile resolution. The restore branch is
/// intentionally delegated to IAgentRecoveryActor; a missing actor can only
/// fail closed and cannot fall back to a direct writer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentRecoveryCommandExecutor
{
    private readonly AgentRecoveryCommandAdapter adapter;
    private readonly IAgentRecoveryActor recoveryActor;

    public AgentRecoveryCommandExecutor(IAgentRecoveryActor recoveryActor)
        : this(new AgentRecoveryCommandAdapter(), recoveryActor)
    {
    }

    internal AgentRecoveryCommandExecutor(
        AgentRecoveryCommandAdapter adapter,
        IAgentRecoveryActor recoveryActor)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.recoveryActor = recoveryActor ?? throw new ArgumentNullException(nameof(recoveryActor));
    }

    public async ValueTask<AgentRecoveryCommandResult> ExecuteAsync(
        AgentRecoveryCommandLine command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var profile = adapter.ResolveProfile(command);
        if (command.Kind == AgentRecoveryCommandKind.Status)
        {
            var status = await AgentRecoveryCommandAdapter.ReadStatusAsync(profile, cancellationToken)
                .ConfigureAwait(false);
            return new(
                status.Mode == P275RecoveryStatusMode.Unavailable ? 1 : 0,
                status,
                null);
        }

        var restoreRequest = AgentRecoveryCommandAdapter.CreateRestoreRequest(profile, command);
        var result = await recoveryActor.RestoreAsync(restoreRequest, cancellationToken).ConfigureAwait(false);
        var restoreStatus = await AgentRecoveryCommandAdapter.ReadStatusAsync(profile, cancellationToken)
            .ConfigureAwait(false);
        return new(
            result.Succeeded ? 0 : 1,
            restoreStatus,
            result.FailureCode);
    }
}

public sealed record AgentRecoveryCommandResult(
    int ExitCode,
    P275RecoveryStatus? Status,
    string? FailureCode);
