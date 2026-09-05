using ReminNote.Agent.Transport;
using ReminNote.Infrastructure.Persistence.Export;
using System.Runtime.Versioning;

namespace ReminNote.Agent.Runtime;

/// <summary>
/// Product command-line entry for structured user-data export and Candidate
/// restore. The command never accepts a database path: Active is resolved by
/// the same profile contract as the Agent, while the artifact/Candidate paths
/// are independently validated and remain outside Active.
/// </summary>
public enum AgentUserDataCommandKind
{
    Export,
    Restore,
    VerifyCandidate,
    PromoteCandidate
}

public sealed record AgentUserDataCommandLine(
    AgentUserDataCommandKind Kind,
    string ArtifactPath,
    string? CandidateRoot,
    string? DataRoot,
    string? RepoRoot,
    string? ProfileName,
    bool Confirm,
    bool ImportCandidate)
{
    public static bool IsInvocation(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(argument =>
            argument is "--user-data-export" or
                "--user-data-restore" or
                "--user-data-verify" or
                "--user-data-promote");
    }

    public static AgentUserDataCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        AgentUserDataCommandKind? kind = null;
        string? artifactPath = null;
        string? candidateRoot = null;
        string? dataRoot = null;
        string? repoRoot = null;
        string? profileName = null;
        var confirm = false;
        var importCandidate = false;

        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            switch (option)
            {
                case "--user-data-export":
                    RequireUniqueKind(kind, AgentUserDataCommandKind.Export);
                    kind = AgentUserDataCommandKind.Export;
                    artifactPath = ReadPath(args, ref index, option);
                    break;
                case "--user-data-restore":
                    RequireUniqueKind(kind, AgentUserDataCommandKind.Restore);
                    kind = AgentUserDataCommandKind.Restore;
                    artifactPath = ReadPath(args, ref index, option);
                    break;
                case "--user-data-verify":
                    RequireUniqueKind(kind, AgentUserDataCommandKind.VerifyCandidate);
                    kind = AgentUserDataCommandKind.VerifyCandidate;
                    artifactPath = ReadPath(args, ref index, option);
                    break;
                case "--user-data-promote":
                    RequireUniqueKind(kind, AgentUserDataCommandKind.PromoteCandidate);
                    kind = AgentUserDataCommandKind.PromoteCandidate;
                    artifactPath = ReadPath(args, ref index, option);
                    break;
                case "--candidate-root":
                    if (candidateRoot is not null)
                    {
                        throw InvalidArguments();
                    }

                    candidateRoot = ReadPath(args, ref index, option);
                    break;
                case "--data-root":
                    if (dataRoot is not null)
                    {
                        throw InvalidArguments();
                    }

                    dataRoot = ReadPath(args, ref index, option);
                    break;
                case "--repo-root":
                    if (repoRoot is not null)
                    {
                        throw InvalidArguments();
                    }

                    repoRoot = ReadPath(args, ref index, option);
                    break;
                case "--profile":
                    if (profileName is not null)
                    {
                        throw InvalidArguments();
                    }

                    profileName = ReadValue(args, ref index, option);
                    break;
                case "--confirm":
                    if (confirm)
                    {
                        throw InvalidArguments();
                    }

                    confirm = true;
                    break;
                case "--import-candidate":
                    if (importCandidate)
                    {
                        throw InvalidArguments();
                    }

                    importCandidate = true;
                    break;
                default:
                    throw InvalidArguments();
            }
        }

        if (kind is null || artifactPath is null ||
            (dataRoot is not null && repoRoot is not null) ||
            (kind is AgentUserDataCommandKind.Export or AgentUserDataCommandKind.VerifyCandidate or AgentUserDataCommandKind.PromoteCandidate) && candidateRoot is not null ||
            (kind is AgentUserDataCommandKind.Export or AgentUserDataCommandKind.VerifyCandidate or AgentUserDataCommandKind.PromoteCandidate) && importCandidate ||
            kind == AgentUserDataCommandKind.Restore && candidateRoot is null ||
            kind == AgentUserDataCommandKind.Restore && importCandidate && !confirm ||
            kind == AgentUserDataCommandKind.Export && confirm ||
            kind == AgentUserDataCommandKind.VerifyCandidate && confirm ||
            kind == AgentUserDataCommandKind.PromoteCandidate && !confirm)
        {
            throw InvalidArguments();
        }

        return new(
            kind.Value,
            artifactPath,
            candidateRoot,
            dataRoot,
            repoRoot,
            profileName,
            confirm,
            importCandidate);
    }

    private static void RequireUniqueKind(
        AgentUserDataCommandKind? existing,
        AgentUserDataCommandKind requested)
    {
        if (existing is not null && existing != requested)
        {
            throw InvalidArguments();
        }

        if (existing == requested)
        {
            throw InvalidArguments();
        }
    }

    private static string ReadPath(
        IReadOnlyList<string> args,
        ref int index,
        string option)
    {
        var value = ReadValue(args, ref index, option);
        if (!Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
        {
            throw InvalidArguments();
        }

        try
        {
            var fullPath = Path.GetFullPath(value);
            if (fullPath.Any(char.IsControl))
            {
                throw InvalidArguments();
            }

            var root = Path.GetPathRoot(fullPath);
            return root is not null &&
                fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                ? root
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            throw InvalidArguments();
        }
        catch (NotSupportedException)
        {
            throw InvalidArguments();
        }
        catch (IOException)
        {
            throw InvalidArguments();
        }
    }

    private static string ReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string option)
    {
        if (++index >= args.Count ||
            string.IsNullOrWhiteSpace(args[index]) ||
            args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw InvalidArguments();
        }

        return args[index];
    }

    private static ArgumentException InvalidArguments() =>
        new("用户数据命令参数无效。", "args");
}

/// <summary>
/// Executes the user-data command for both the standalone Agent executable
/// and Bootstrap. Restore is dry-run by default; only the explicit pair
/// <c>--confirm --import-candidate</c> creates a Candidate database.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentUserDataCommandExecutor
{
    public static async ValueTask<int> ExecuteAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var command = AgentUserDataCommandLine.Parse(args);
            var roots = AgentStartupPaths.ResolveDataRoot(args);
            Directory.CreateDirectory(roots.DataRoot);
            var profile = TransportProfileResolver.ResolveForCurrentUser(
                new TransportProfileArguments(
                    DataRoot: roots.DataRoot,
                    RepoRoot: null,
                    Profile: command.ProfileName),
                new ProtocolTransportProfileContractAdapter());

            if (command.Kind == AgentUserDataCommandKind.Export)
            {
                var artifact = await UserDataExportService
                    .CreateAsync(profile.DatabasePath, profile.DataRoot, cancellationToken)
                    .ConfigureAwait(false);
                var written = await UserDataExportService
                    .WriteArtifactAsync(
                        artifact,
                        command.ArtifactPath,
                        profile.DatabasePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine("status=exported");
                Console.WriteLine($"path={written.Path}");
                Console.WriteLine($"checksum={written.Checksum}");
                return 0;
            }

            if (command.Kind is AgentUserDataCommandKind.VerifyCandidate or AgentUserDataCommandKind.PromoteCandidate)
            {
                if (command.Kind == AgentUserDataCommandKind.VerifyCandidate)
                {
                    var validation = await UserDataCandidateService
                        .ValidateAsync(
                            command.ArtifactPath,
                            profile.DatabasePath,
                            profile.ProfileScope,
                            cancellationToken)
                        .ConfigureAwait(false);
                    Console.WriteLine($"status={(validation.IsValid ? UserDataCandidateStatus.Verified : UserDataCandidateStatus.Blocked)}");
                    Console.WriteLine($"candidateDatabase={validation.CandidateDatabasePath}");
                    Console.WriteLine($"checksum={validation.SourceChecksum}");
                    Console.WriteLine($"failureCode={validation.FailureCode ?? string.Empty}");
                    foreach (var issue in validation.Issues)
                    {
                        Console.WriteLine($"issue={issue}");
                    }

                    return validation.IsValid ? 0 : 1;
                }

                var promotion = await UserDataCandidateService
                    .PromoteAsync(
                        new UserDataCandidatePromotionRequest(
                            command.ArtifactPath,
                            profile.DataRoot,
                            profile.ProfileName,
                            profile.ProfileScope,
                            profile.DatabasePath,
                            command.Confirm),
                        cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine($"status={promotion.Status}");
                Console.WriteLine($"candidateDatabase={promotion.CandidateDatabasePath}");
                Console.WriteLine($"checksum={promotion.SourceChecksum}");
                Console.WriteLine($"backupArtifact={promotion.BackupArtifact ?? string.Empty}");
                Console.WriteLine($"historyDirectory={promotion.HistoryDirectoryPath ?? string.Empty}");
                Console.WriteLine($"failureCode={promotion.FailureCode ?? string.Empty}");
                foreach (var issue in promotion.Issues)
                {
                    Console.WriteLine($"issue={issue}");
                }

                return promotion.Status == UserDataCandidateStatus.Promoted ? 0 : 1;
            }

            var result = await UserDataRestoreService
                .ValidateAndStageAsync(
                    new UserDataRestoreRequest(
                        command.ArtifactPath,
                        command.CandidateRoot!,
                        profile.DatabasePath,
                        profile.ProfileScope,
                        DryRun: !command.Confirm,
                        Confirmed: command.Confirm,
                        ImportCandidate: command.ImportCandidate),
                    cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"status={result.Status}");
            Console.WriteLine($"checksum={result.SourceChecksum}");
            Console.WriteLine($"stagedArtifact={result.StagedArtifactPath ?? string.Empty}");
            Console.WriteLine($"candidateDatabase={result.CandidateDatabasePath ?? string.Empty}");
            foreach (var issue in result.Issues)
            {
                Console.WriteLine($"issue={issue}");
            }

            return result.Status is UserDataRestoreStatus.Conflict or UserDataRestoreStatus.Blocked
                ? 1
                : 0;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
        catch (UserDataExportContractException exception)
        {
            Console.Error.WriteLine($"failureCode={exception.Code}");
            return 1;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("failureCode=user_data.command_failed");
            Console.Error.WriteLine($"detail={exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("failureCode=user_data.command_failed");
            Console.Error.WriteLine($"detail={exception.Message}");
            return 1;
        }
    }
}
