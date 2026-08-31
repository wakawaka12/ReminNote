using System.Diagnostics;
using System.Runtime.Versioning;
using ReminNote.Agent.Runtime;

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
            var options = BootstrapOptions.Parse(args);
            Directory.CreateDirectory(Path.Combine(options.RepositoryRoot, ".devdata"));

            var control = new AgentControlClient(options.RepositoryRoot, options.ProfileName);
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
                await control.WaitForHealthyAsync(
                        TimeSpan.FromSeconds(10),
                        cancellationToken)
                    .ConfigureAwait(false);
                return candidate;
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
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(options.RepositoryRoot);
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
        string? ProfileName,
        bool NoWidget,
        string WidgetInstanceId)
    {
        public static BootstrapOptions Parse(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);

            var repositoryRoot = Directory.GetCurrentDirectory();
            string? profileName = null;
            var noWidget = false;
            var widgetInstanceId = "default";
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--repo-root":
                        repositoryRoot = ReadValue(args, ref index, "--repo-root");
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

            repositoryRoot = AgentStartupPaths.ValidateRepositoryRoot(repositoryRoot);

            if (widgetInstanceId.Length is < 1 or > 128 ||
                widgetInstanceId.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("--widget-instance 标识长度或字符非法。", nameof(args));
            }

            return new(repositoryRoot, profileName, noWidget, widgetInstanceId);
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
