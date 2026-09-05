using System.Diagnostics;
using System.Runtime.Versioning;
using ReminNote.Agent.Runtime;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Bootstrap;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static readonly TimeSpan[] AgentRetryBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5)
    ];

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ReminNote Bootstrap requires Windows named pipes and SID ACLs.");
            return 2;
        }

        try
        {
            if (AgentUserDataCommandLine.IsInvocation(args))
            {
                return await AgentUserDataCommandExecutor
                    .ExecuteAsync(args)
                    .ConfigureAwait(false);
            }

            if (AgentRecoveryCommandLine.IsRecoveryInvocation(args))
            {
                return await RunRecoveryCommandAsync(args).ConfigureAwait(false);
            }

            var options = BootstrapOptions.Parse(args);
            Directory.CreateDirectory(options.DataRoot);

            var control = new AgentControlClient(
                options.RepositoryRoot,
                options.ProfileName,
                options.DataRoot);
            using var instance = new Semaphore(
                initialCount: 1,
                maximumCount: 1,
                $@"Local\ReminNote.Bootstrap.{control.ProfileScope}");
            if (!instance.WaitOne(TimeSpan.Zero))
            {
                Console.WriteLine("ReminNote Bootstrap is already running for this profile.");
                return 0;
            }

            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            Process? agent = null;
            Process? main = null;
            Process? widget = null;
            try
            {
                agent = await StartAgentWithReadinessAsync(options, control, shutdown.Token)
                    .ConfigureAwait(false);
                main = StartChild(
                    options,
                    "ReminNote.Windows",
                    "net10.0-windows",
                    includeWidgetInstance: false);
                if (!options.NoWidget)
                {
                    widget = StartChild(
                        options,
                        "ReminNote.Widget",
                        "net10.0-windows",
                        includeWidgetInstance: true);
                }

                Console.WriteLine(
                    $"ReminNote Bootstrap started Agent, Main and {(options.NoWidget ? "no Widget" : "Widget")} for {control.ProfileScope}.");
                var mainExit = main.WaitForExitAsync(shutdown.Token);
                var agentExit = agent.WaitForExitAsync(shutdown.Token);
                var firstExit = await Task.WhenAny(mainExit, agentExit).ConfigureAwait(false);
                if (firstExit == agentExit && !main.HasExited)
                {
                    Console.Error.WriteLine("ReminNote Agent exited while Main was running; UI shutdown is required.");
                    return 1;
                }

                await mainExit.ConfigureAwait(false);
                return main.ExitCode;
            }
            finally
            {
                await StopProcessAsync(widget).ConfigureAwait(false);
                await StopProcessAsync(main).ConfigureAwait(false);
                await StopProcessAsync(agent).ConfigureAwait(false);
                Console.CancelKeyPress -= cancelHandler;
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ReminNote Bootstrap failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunRecoveryCommandAsync(string[] args)
    {
        try
        {
            var command = AgentRecoveryCommandLine.Parse(args);
            var adapter = new AgentRecoveryCommandAdapter();
            var profile = adapter.ResolveProfile(command);
            if (command.Kind == AgentRecoveryCommandKind.Status)
            {
                var status = await AgentRecoveryCommandAdapter.ReadStatusAsync(profile).ConfigureAwait(false);
                WriteRecoveryStatus(status);
                return status.Mode == P275RecoveryStatusMode.Unavailable
                    ? 1
                    : 0;
            }

            if (command.Kind == AgentRecoveryCommandKind.Restore)
            {
                _ = AgentRecoveryCommandAdapter.CreateRestoreRequest(profile, command);
            }

            using var agent = StartRecoveryAgent(command, args);
            var errorOutputTask = agent.StandardError.ReadToEndAsync();
            await agent.WaitForExitAsync().ConfigureAwait(false);
            var errorOutput = await errorOutputTask.ConfigureAwait(false);
            WriteRecoveryAgentFailure(errorOutput, agent.ExitCode);
            return agent.ExitCode;
        }
        catch (AgentRecoveryCommandException exception)
        {
            Console.Error.WriteLine($"failureCode={exception.FailureCode}");
            return 2;
        }
        catch (Exception)
        {
            Console.Error.WriteLine(
                $"failureCode={P275MigrationFailureCodes.RecoveryRequired}");
            return 1;
        }
    }

    private static Process StartRecoveryAgent(
        AgentRecoveryCommandLine command,
        IReadOnlyList<string> originalArguments)
    {
        var searchRoot = command.RepoRoot ?? Directory.GetCurrentDirectory();
        var executable = ResolveExecutable(searchRoot, "ReminNote.Agent", "net10.0");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Directory.Exists(searchRoot)
                ? Path.GetFullPath(searchRoot)
                : AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var argument in originalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the Agent recovery actor.");
    }

    private static void WriteRecoveryAgentFailure(string output, int exitCode)
    {
        if (exitCode == 0)
        {
            return;
        }

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "failureCode=";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var failureCode = line[prefix.Length..];
            if (P275MigrationFailureCodes.IsKnown(failureCode))
            {
                Console.Error.WriteLine($"failureCode={failureCode}");
                return;
            }
        }

        Console.Error.WriteLine($"failureCode={P275MigrationFailureCodes.RestoreFailed}");
    }

    private static void WriteRecoveryStatus(
        P275RecoveryStatus status)
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

    private static async Task<Process> StartAgentWithReadinessAsync(
        BootstrapOptions options,
        AgentControlClient control,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < AgentRetryBackoff.Length; attempt++)
        {
            Process? candidate = null;
            try
            {
                candidate = StartChild(
                    options,
                    "ReminNote.Agent",
                    "net10.0",
                    includeWidgetInstance: false);
                using var readinessCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var readiness = control.WaitForHealthyAsync(
                        TimeSpan.FromSeconds(10),
                        readinessCancellation.Token)
                    .AsTask();
                var processExit = candidate.WaitForExitAsync(cancellationToken);
                var firstCompleted = await Task.WhenAny(readiness, processExit).ConfigureAwait(false);
                if (firstCompleted == processExit)
                {
                    readinessCancellation.Cancel();
                    await IgnoreReadinessTaskAsync(readiness).ConfigureAwait(false);
                    await processExit.ConfigureAwait(false);
                    if (candidate.ExitCode == AgentStartupExitCodes.MigrationBlocked)
                    {
                        throw new AgentMigrationBlockedException();
                    }

                    throw new InvalidOperationException(
                        $"Agent exited before readiness with code {candidate.ExitCode}.");
                }

                await readiness.ConfigureAwait(false);
                return candidate;
            }
            catch (AgentMigrationBlockedException)
            {
                await StopProcessAsync(candidate).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or
                TimeoutException or
                InvalidOperationException or
                UnauthorizedAccessException)
            {
                lastFailure = exception;
                await StopProcessAsync(candidate).ConfigureAwait(false);
            }

            if (attempt < AgentRetryBackoff.Length - 1)
            {
                await Task.Delay(AgentRetryBackoff[attempt], cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            "Agent did not become healthy after the bounded startup attempts.",
            lastFailure);
    }

    private static async Task IgnoreReadinessTaskAsync(Task readiness)
    {
        try
        {
            await readiness.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Process exit is the authoritative startup result; observe the
            // canceled/failed health probe so it cannot become unobserved.
        }
    }

    private sealed class AgentMigrationBlockedException : InvalidOperationException
    {
        public AgentMigrationBlockedException()
            : base("Agent startup was blocked by the migration gate; Main/Widget were not started.")
        {
        }
    }

    private static Process StartChild(
        BootstrapOptions options,
        string assemblyName,
        string targetFramework,
        bool includeWidgetInstance)
    {
        var executable = ResolveExecutable(options.RepositoryRoot, assemblyName, targetFramework);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = options.RepositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--data-root");
        startInfo.ArgumentList.Add(options.DataRoot);
        if (options.ProfileName is not null)
        {
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add(options.ProfileName);
        }

        if (includeWidgetInstance)
        {
            startInfo.ArgumentList.Add("--widget-instance");
            startInfo.ArgumentList.Add(options.WidgetInstanceId);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {assemblyName}.");
    }

    private static string ResolveExecutable(
        string repositoryRoot,
        string assemblyName,
        string targetFramework)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.exe"),
            Path.Combine(repositoryRoot, "src", "windows", assemblyName, "bin", "Release", targetFramework, $"{assemblyName}.exe"),
            Path.Combine(repositoryRoot, "src", "windows", assemblyName, "bin", "Debug", targetFramework, $"{assemblyName}.exe")
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            $"Cannot find the built {assemblyName} executable. Build the Release solution first.",
            candidates[1]);
    }

    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            NotSupportedException or
            System.ComponentModel.Win32Exception or
            TimeoutException)
        {
            Console.Error.WriteLine($"ReminNote Bootstrap child cleanup failed: {exception.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    private sealed record BootstrapOptions(
        string RepositoryRoot,
        string DataRoot,
        string? ProfileName,
        bool NoWidget,
        string WidgetInstanceId)
    {
        public static BootstrapOptions Parse(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);

            string? profileName = null;
            var noWidget = false;
            var widgetInstanceId = "default";
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--repo-root":
                    case "--data-root":
                        _ = ReadValue(args, ref index, args[index]);
                        break;
                    case "--profile":
                        profileName = ReadValue(args, ref index, "--profile");
                        break;
                    case "--widget-instance":
                        widgetInstanceId = ReadValue(args, ref index, "--widget-instance");
                        break;
                    case "--no-widget":
                        noWidget = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown Bootstrap option '{args[index]}'.", nameof(args));
                }
            }

            var roots = AgentStartupPaths.ResolveDataRoot(args);
            var repositoryRoot = roots.RepositoryRoot ?? AppContext.BaseDirectory;

            if (widgetInstanceId.Length is < 1 or > 128 ||
                widgetInstanceId.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("--widget-instance 标识长度或字符非法。", nameof(args));
            }

            return new(repositoryRoot, roots.DataRoot, profileName, noWidget, widgetInstanceId);
        }

        private static string ReadValue(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"{option} 必须带值。", nameof(args));
            }

            return args[index];
        }
    }
}
