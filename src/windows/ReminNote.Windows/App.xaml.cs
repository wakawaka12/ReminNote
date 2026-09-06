using System.Windows;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using ReminNote.Agent.Notifications;
using ReminNote.Agent.Runtime;
using ReminNote.Core.Application;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Application;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Today;
using ReminNote.Windows.Features.Anime;
using ReminNote.Windows.Features.Reminders;
using ReminNote.Windows.Features.Today;
using ReminNote.Windows.Notifications;
using ReminNote.Windows.Startup;
using ReminNote.Windows.ViewModels;

namespace ReminNote.Windows;

public partial class App : Application, IDisposable
{
    private IHost? _host;
    private SingleInstanceCoordinator? _singleInstance;
    private NotificationHostBridgeServer? _notificationBridge;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var activationArgument = ResolveNotificationActivationArgument(e.Args);
        var roots = AgentStartupPaths.ResolveDataRoot(e.Args);
        var repositoryRoot = roots.RepositoryRoot ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(roots.DataRoot);
        var headless = HasHeadlessFlag(e.Args);
        var profileName = ResolveProfileName(e.Args);
        var agentClient = new AgentTaskClient(
            repositoryRoot,
            ProtocolClientKinds.Main,
            profileName,
            roots.DataRoot);
        _singleInstance = new SingleInstanceCoordinator(
            MainInstanceIdentity.GetMutexName(agentClient.ProfileScope),
            MainInstanceIdentity.GetPipeName(agentClient.ProfileScope));
        if (!_singleInstance.IsPrimary)
        {
            agentClient.Dispose();
            Shutdown(_singleInstance.TryActivateExisting(activationArgument) ? 0 : 1);
            return;
        }

        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Services.AddSingleton(agentClient);
        builder.Services.AddSingleton<ITaskApplicationService>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentTaskClient>());
        builder.Services.AddSingleton<ITaskQueryService>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentTaskClient>());
        builder.Services.AddSingleton<ITodayQueryService>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentTaskClient>());
        builder.Services.AddSingleton<IReminderQueryService>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentTaskClient>());
        builder.Services.AddSingleton<IReminderRuleQueryService>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentTaskClient>());
        builder.Services.AddSingleton<IReminderRuleCommandClient>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentTaskClient>());
        builder.Services.AddSingleton<IReminderCommandClient>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentTaskClient>());
        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<WindowsNotificationChannelHost>(serviceProvider =>
            WindowsNotificationChannelComposition.CreateHostOwned(
                serviceProvider.GetRequiredService<IClock>(),
                () => Application.Current?.MainWindow,
                Dispatcher,
                ResolveNotificationHostOptions(headless, roots.DataRoot, profileName)));
        builder.Services.AddSingleton<INotificationChannelCatalog>(serviceProvider =>
            serviceProvider.GetRequiredService<WindowsNotificationChannelHost>().Catalog);
        builder.Services.AddTodayFeature();
        builder.Services.AddAnimeFeature();
        builder.Services.AddSingleton<ReminderCenterViewModel>();
        builder.Services.AddSingleton<ReminderSettingsViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        _host.Start();
        var notificationHost = _host.Services.GetRequiredService<WindowsNotificationChannelHost>();
        _notificationBridge = NotificationHostBridgeServer.Start(
            agentClient.ProfileScope,
            NotificationHostBridgeKind.Main,
            notificationHost.Catalog);

        if (headless)
        {
            // The isolated process gate exercises host composition and
            // lifetime without taking focus or showing a desktop window.
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            return;
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        _singleInstance.StartListener(argument =>
        {
            var activation = ParseNotificationActivation(argument);
            _ = Dispatcher.BeginInvoke(new Action(() => ActivateMainWindow(activation)));
        });
        window.Show();
        if (activationArgument is not null)
        {
            ActivateMainWindow(ParseNotificationActivation(activationArgument));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var stopTask = _host?.StopAsync(shutdown.Token);
            if (stopTask is not null && !stopTask.Wait(TimeSpan.FromSeconds(2)))
            {
                Debug.WriteLine("Main host shutdown timed out; continuing application cleanup.");
            }
        }
        catch (AggregateException exception)
        {
            Debug.WriteLine($"Main host shutdown failed; continuing application cleanup: {exception.Message}");
        }
        finally
        {
            _notificationBridge?.Dispose();
            _notificationBridge = null;
            _host?.Dispose();
            Dispose();
            base.OnExit(e);
        }
    }

    private void ActivateMainWindow(ReminderNotificationActivation? activation = null)
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        if (activation is null)
        {
            window.ActivateFromExternalRequest();
            return;
        }

        window.ActivateFromExternalRequest(activation);
    }

    private static string? ResolveNotificationActivationArgument(IReadOnlyList<string> args)
    {
        foreach (var argument in args)
        {
            if (ReminderNotificationActivation.TryParse(argument, out _))
            {
                return argument;
            }
        }

        return null;
    }

    private static ReminderNotificationActivation? ParseNotificationActivation(string? argument) =>
        ReminderNotificationActivation.TryParse(argument, out var activation)
            ? activation
            : null;

    public void Dispose()
    {
        _notificationBridge?.Dispose();
        _notificationBridge = null;
        _singleInstance?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string? ResolveProfileName(string[] args)
    {
        string? profile = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], "--profile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException("--profile 必须带 profile key。", nameof(args));
            }

            profile = args[index];
        }

        return profile;
    }

    private static bool HasHeadlessFlag(string[] args) =>
        args.Any(argument =>
            string.Equals(argument, "--headless", StringComparison.OrdinalIgnoreCase));

    private static WindowsNotificationHostOptions ResolveNotificationHostOptions(
        bool headless,
        string dataRoot,
        string? profileName)
    {
        // A command-line switch is not evidence that the OS registration exists.
        // Normal startup creates and reads back the Start-menu shortcut; headless
        // gates intentionally avoid mutating the user's Start menu.
        string? failureCode = null;
        var verified = !headless && WindowsToastRegistration.TryEnsureAndVerify(
            WindowsNotificationHostOptions.DefaultApplicationUserModelId,
            Environment.ProcessPath,
            out failureCode,
            dataRoot,
            profileName);
        if (!verified && !headless && failureCode is not null)
        {
            Debug.WriteLine($"Toast registration not verified: {failureCode}");
        }

        return new WindowsNotificationHostOptions(toastRegistrationVerified: verified);
    }
}
