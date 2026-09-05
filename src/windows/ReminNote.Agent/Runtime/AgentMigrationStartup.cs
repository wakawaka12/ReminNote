using System.Runtime.Versioning;
using ReminNote.Agent.Transport;
using ReminNote.Core.Protocol;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Agent.Runtime;

/// <summary>
/// The single Agent composition root for the P2.75 migration and recovery
/// actors. Provider implementations remain in Infrastructure; this class is
/// the only place that assembles them into the Agent runtime.
/// </summary>
internal sealed class AgentMigrationStartupComposition
{
    private static P275MigrationPlan DefaultPlan => P275MigrationPlanCatalog.Current;

    private readonly Func<ResolvedTransportProfile, IP275MigrationRunner> runnerFactory;
    private readonly P275MigrationPlan migrationPlan;
    private readonly TimeSpan lockTimeout;

    private AgentMigrationStartupComposition(
        Func<ResolvedTransportProfile, IP275MigrationRunner> runnerFactory,
        P275MigrationPlan migrationPlan,
        TimeSpan lockTimeout)
    {
        this.runnerFactory = runnerFactory ?? throw new ArgumentNullException(nameof(runnerFactory));
        this.migrationPlan = migrationPlan ?? throw new ArgumentNullException(nameof(migrationPlan));
        if (lockTimeout <= TimeSpan.Zero || lockTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        }

        this.lockTimeout = lockTimeout;
    }

    internal static AgentMigrationStartupComposition CreateDefault() =>
        new(
            CreateProductionRunner,
            DefaultPlan,
            TimeSpan.FromSeconds(10));

    /// <summary>
    /// Explicit integration point for the total assembly. P2.75-01 supplies
    /// source/backup, P2.75-03 supplies verification/state, and this slice's
    /// runner is composed once here; no provider implementation is copied
    /// into Agent.
    /// </summary>
    internal static AgentMigrationStartupComposition CreateForIntegration(
        IP275MigrationRunner migrationRunner,
        P275MigrationPlan migrationPlan,
        TimeSpan lockTimeout) =>
        new(_ => migrationRunner, migrationPlan, lockTimeout);

    internal ValueTask<P275StartupGateResult> OpenAsync(
        ResolvedTransportProfile profile,
        string runId,
        CancellationToken cancellationToken = default) =>
        OpenAsync(
            profile,
            runId,
            allowRetry: false,
            cancellationToken: cancellationToken);

    internal async ValueTask<P275StartupGateResult> OpenAsync(
        ResolvedTransportProfile profile,
        string runId,
        bool allowRetry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var paths = new P275ProfilePaths(profile.DataRoot, profile.ProfileName);
        var request = new P275MigrationRequest(
            paths,
            profile.ProfileScope,
            runId,
            migrationPlan,
            lockTimeout,
            allowRetry);
        var runner = runnerFactory(profile);
        return await new P275StartupGate(runner)
            .OpenAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<int> RunRecoveryCommandAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var command = AgentRecoveryCommandLine.Parse(args);
            if (command.Kind == AgentRecoveryCommandKind.Retry)
            {
                return await RunExplicitRetryAsync(command, cancellationToken).ConfigureAwait(false);
            }

            var executor = new AgentRecoveryCommandExecutor(
                new P275RecoveryActor(DefaultPlan));
            var result = await executor.ExecuteAsync(command, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status is { } status)
            {
                WriteRecoveryStatus(status);
            }
            else if (result.FailureCode is { } failureCode)
            {
                Console.Error.WriteLine($"failureCode={failureCode}");
            }

            return result.ExitCode;
        }
        catch (AgentRecoveryCommandException exception)
        {
            Console.Error.WriteLine($"failureCode={exception.FailureCode}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine($"failureCode={P275MigrationFailureCodes.RecoveryRequired}");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunExplicitRetryAsync(
        AgentRecoveryCommandLine command,
        CancellationToken cancellationToken)
    {
        var adapter = new AgentRecoveryCommandAdapter();
        var profile = adapter.ResolveProfile(command);
        var resolvedProfile = TransportProfileResolver.ResolveForCurrentUser(
            new TransportProfileArguments(
                profile.DataRoot,
                null,
                profile.ProfileName),
            new ProtocolTransportProfileContractAdapter());
        var migration = await CreateDefault()
            .OpenAsync(
                resolvedProfile,
                ProtocolIds.NewEventId().ToString("D"),
                allowRetry: true,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!migration.Ready ||
            !migration.Writable ||
            migration.Migration.State != P275MigrationState.Ready)
        {
            Console.Error.WriteLine(
                $"failureCode={migration.Migration.FailureCode ?? P275MigrationFailureCodes.RecoveryRequired}");
            return 1;
        }

        WriteRecoveryStatus(
            await AgentRecoveryCommandAdapter
                .ReadStatusAsync(profile, cancellationToken)
                .ConfigureAwait(false));
        return 0;
    }

    private static P275MigrationRunner CreateProductionRunner(
        ResolvedTransportProfile profile)
    {
        var paths = new P275ProfilePaths(profile.DataRoot, profile.ProfileName);
        return new P275MigrationRunner(
            new P275ActiveSourceReader(),
            new P275SafetyBackupProvider(),
            new P275FileWriterQuiescence(),
            new P275FileMigrationLockProvider(),
            new P275EfForwardMigrationApplier(),
            new P275CandidateSidecarFinalizer(),
            new P275ProductionCandidateVerifier(),
            new P275AtomicFilePromoter(),
            new P275ProductionMigrationStatePort(new P275ProfileLayout(paths.ProfileRoot)));
    }

    private static void WriteRecoveryStatus(P275RecoveryStatus status)
    {
        Console.WriteLine($"mode={status.Mode}");
        Console.WriteLine($"state={P275MigrationStateNames.ToValue(status.State)}");
        Console.WriteLine($"ready={status.Ready.ToString().ToLowerInvariant()}");
        Console.WriteLine($"writable={status.Writable.ToString().ToLowerInvariant()}");
        Console.WriteLine($"recoveryRequired={status.RecoveryRequired.ToString().ToLowerInvariant()}");
        Console.WriteLine($"failureCode={status.FailureCode ?? string.Empty}");
        Console.WriteLine($"runId={status.RunId ?? string.Empty}");
        Console.WriteLine($"profileScope={status.ProfileScope ?? string.Empty}");
        Console.WriteLine($"sourceSchema={string.Join(',', status.SourceSchema)}");
        Console.WriteLine($"targetSchema={string.Join(',', status.TargetSchema)}");
        Console.WriteLine($"backupArtifact={status.BackupArtifact ?? string.Empty}");
        Console.WriteLine($"backupHashStatus={status.BackupHashStatus}");
        Console.WriteLine($"manifestStatus={status.ManifestStatus}");
        Console.WriteLine($"manifestArtifact={status.ManifestArtifact ?? string.Empty}");
        Console.WriteLine($"backupByteLength={status.BackupByteLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}");
        Console.WriteLine($"candidateArtifact={status.CandidateArtifact ?? string.Empty}");
        Console.WriteLine($"nextAction={status.NextAction}");
    }
}
