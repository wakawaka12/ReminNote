using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using ReminNote.Agent.Transport;
using ReminNote.Core.Protocol;
using ReminNote.Core.Transport;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Agent.Runtime;

[SupportedOSPlatform("windows")]
internal static class AgentRuntime
{
    public static async Task<int> RunAsync(string[] args)
        => await RunAsync(args, AgentMigrationStartupComposition.CreateDefault()).ConfigureAwait(false);

    internal static async Task<int> RunAsync(
        string[] args,
        AgentMigrationStartupComposition migrationStartup)
    {
        ArgumentNullException.ThrowIfNull(migrationStartup);
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ReminNote Agent requires Windows named pipes and SID ACLs.");
            return 2;
        }

        try
        {
            var profile = TransportProfileResolver.ResolveForCurrentUser(
                ParseArguments(args),
                new ProtocolTransportProfileContractAdapter());

            P275StartupGateResult migration;
            try
            {
                migration = await migrationStartup
                    .OpenAsync(
                        profile,
                        ProtocolIds.NewEventId().ToString("D"))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Console.Error.WriteLine(
                    $"ReminNote Agent startup blocked by migration gate: " +
                    $"state={P275MigrationState.RecoveryRequired}, " +
                    $"failure={P275MigrationFailureCodes.PathInvalid}.");
                return AgentStartupExitCodes.MigrationBlocked;
            }

            if (!migration.Ready ||
                !migration.Writable ||
                migration.Migration.State != P275MigrationState.Ready)
            {
                Console.Error.WriteLine(
                    $"ReminNote Agent startup blocked by migration gate: " +
                    $"state={migration.Migration.State}, " +
                    $"failure={migration.Migration.FailureCode ?? "none"}.");
                return AgentStartupExitCodes.MigrationBlocked;
            }

            P25StorageStore store;
            try
            {
                store = await OpenReadyStoreAsync(profile).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Console.Error.WriteLine(
                    $"ReminNote Agent startup blocked by migration gate: " +
                    $"state={P275MigrationState.RecoveryRequired}, " +
                    $"failure={P275MigrationFailureCodes.VerifyFailed}.");
                return AgentStartupExitCodes.MigrationBlocked;
            }

            await using var storeLease = store;
            var agentInstanceId = ProtocolIds.NewEventId();
            var limits = AgentTransportDefaults.CreateLimits();
            var businessEndpoint = new NamedPipeTransportEndpoint(profile, NamedPipeEndpointKind.Business);
            var controlEndpoint = new NamedPipeTransportEndpoint(profile, NamedPipeEndpointKind.Control);
            var profileAdapter = new ProtocolTransportProfileContractAdapter();
            var handshake = new AgentProtocolHandshake(
                agentInstanceId,
                profile.ProfileScope,
                async () => (await store.ReadRevisionStateAsync().ConfigureAwait(false)).CurrentRevision);
            var dispatcher = new AgentProtocolDispatcher(
                store,
                profile.ProfileScope,
                profile.DatabasePath,
                agentInstanceId);
            var payloadValidator = new ProtocolTransportPayloadValidator();

            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                Console.WriteLine($"ReminNote Agent ready for profile {profile.ProfileScope}.");
                using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                var businessTask = RunBusinessPipeAsync(
                    businessEndpoint,
                    profile,
                    profileAdapter,
                    handshake,
                    dispatcher,
                    payloadValidator,
                    limits,
                    agentInstanceId,
                    store,
                    runtimeCancellation.Token);
                var controlTask = RunControlPipeAsync(
                    controlEndpoint,
                    profile,
                    profileAdapter,
                    limits,
                    agentInstanceId,
                    runtimeCancellation.Token);
                try
                {
                    var firstCompleted = await Task.WhenAny(businessTask, controlTask).ConfigureAwait(false);
                    await firstCompleted.ConfigureAwait(false);
                }
                finally
                {
                    runtimeCancellation.Cancel();
                    await IgnorePipeTaskAsync(businessTask).ConfigureAwait(false);
                    await IgnorePipeTaskAsync(controlTask).ConfigureAwait(false);
                }
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ReminNote Agent failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task IgnorePipeTaskAsync(Task pipeTask)
    {
        try
        {
            await pipeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task RunControlPipeAsync(
        NamedPipeTransportEndpoint endpoint,
        ResolvedTransportProfile profile,
        ITransportProfileContractAdapter profileAdapter,
        TransportLimits limits,
        Guid agentInstanceId,
        CancellationToken cancellationToken)
    {
        var frameCodec = new LengthPrefixedFrameCodec(limits);
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = endpoint.CreateServerStream();
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                NamedPipeAcl.EnsureClientMatchesProfile(server, profile, profileAdapter);
                var request = await frameCodec.ReadAsync(
                        server,
                        limits.HelloQueryStatusDeadline,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    continue;
                }

                AgentControlProtocol.ValidateHealthRequest(request, profile.ProfileScope);
                var response = AgentControlProtocol.CreateHealthResponse(
                    profile.ProfileScope,
                    agentInstanceId);
                await frameCodec.WriteAsync(
                        server,
                        response,
                        limits.HelloQueryStatusDeadline,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or
                TimeoutException or
                TransportFailureException or
                UnauthorizedAccessException or
                InvalidOperationException)
            {
                Console.Error.WriteLine($"ReminNote Agent control session closed: {exception.Message}");
            }
        }
    }

    private static async Task RunBusinessPipeAsync(
        NamedPipeTransportEndpoint endpoint,
        ResolvedTransportProfile profile,
        ITransportProfileContractAdapter profileAdapter,
        ITransportHandshake handshake,
        ITransportRequestDispatcher dispatcher,
        ITransportPayloadValidator payloadValidator,
        TransportLimits limits,
        Guid agentInstanceId,
        P25StorageStore store,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = endpoint.CreateServerStream();
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                NamedPipeAcl.EnsureClientMatchesProfile(server, profile, profileAdapter);
                var peer = new TransportPeerIdentity(profile.UserSid, profile.ProfileScope);
                await using var session = new NamedPipeTransportSession(
                    payloadValidator,
                    handshake,
                    dispatcher,
                    new ProtocolOverloadResponder(
                        agentInstanceId,
                        () => store.ReadRevisionStateAsync().AsTask().GetAwaiter().GetResult().CurrentRevision),
                    limits);
                await session.RunAsync(server, peer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or TransportFailureException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"ReminNote Agent pipe session closed: {exception.Message}");
            }
        }
    }

    private static async ValueTask<P25StorageStore> OpenReadyStoreAsync(ResolvedTransportProfile profile)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = profile.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            await using (var readOnlyHealth = await P25ReadOnlyConnectionFactory
                             .OpenAsync(profile.DatabasePath)
                             .ConfigureAwait(false))
            {
                // P2.75-03 performs the authoritative post-promote checks;
                // this read-only probe prevents a later Active open from
                // turning a missing WAL/foreign-key profile into READY.
            }

            await connection.OpenAsync().ConfigureAwait(false);
            var store = await P25StorageStore.OpenReadyAsync(
                    connection,
                    profile.ProfileScope)
                .ConfigureAwait(false);
            try
            {
                // This is a read-only health assertion. It verifies that the
                // post-promote gate left the P2.5 profile row available before
                // any pipe can report the Agent as healthy.
                await store.ReadRevisionStateAsync().ConfigureAwait(false);
                return store;
            }
            catch
            {
                await store.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static TransportProfileArguments ParseArguments(string[] args)
    {
        string? dataRoot = null;
        string? repoRoot = null;
        string? profile = null;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option is not ("--data-root" or "--repo-root" or "--profile"))
            {
                throw new ArgumentException($"Unknown Agent option '{option}'.");
            }

            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"Agent option '{option}' requires a value.");
            }

            switch (option)
            {
                case "--data-root":
                    dataRoot = args[index];
                    break;
                case "--repo-root":
                    repoRoot = args[index];
                    break;
                case "--profile":
                    profile = args[index];
                    break;
            }
        }

        return new TransportProfileArguments(dataRoot, repoRoot, profile);
    }
}
