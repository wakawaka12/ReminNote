using System.Windows;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var repositoryRoot = ResolveRepositoryRoot(e.Args);
        Directory.CreateDirectory(Path.Combine(repositoryRoot, ".devdata"));
        var profileName = ResolveProfileName(e.Args);
        var agentClient = new AgentTaskClient(
            repositoryRoot,
            ProtocolClientKinds.Main,
            profileName);
        _singleInstance = new SingleInstanceCoordinator(
            MainInstanceIdentity.GetMutexName(agentClient.ProfileScope),
            MainInstanceIdentity.GetPipeName(agentClient.ProfileScope));
        if (!_singleInstance.IsPrimary)
        {
            agentClient.Dispose();
            Shutdown(_singleInstance.TryActivateExisting() ? 0 : 1);
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
        builder.Services.AddSingleton<IReminderCommandClient>(serviceProvider =>
            serviceProvider.GetRequiredService<AgentTaskClient>());
        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<WindowsNotificationChannelHost>(serviceProvider =>
            WindowsNotificationChannelComposition.CreateHostOwned(
                serviceProvider.GetRequiredService<IClock>(),
                () => Application.Current?.MainWindow,
                Dispatcher));
        builder.Services.AddSingleton<INotificationChannelCatalog>(serviceProvider =>
            serviceProvider.GetRequiredService<WindowsNotificationChannelHost>().Catalog);
        builder.Services.AddTodayFeature();
        builder.Services.AddAnimeFeature();
        builder.Services.AddSingleton<ReminderCenterViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        _host.Start();
        _ = _host.Services.GetRequiredService<INotificationChannelCatalog>();

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        _singleInstance.StartListener(() => Dispatcher.BeginInvoke(new Action(ActivateMainWindow)));
        window.Show();
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
            _host?.Dispose();
            Dispose();
            base.OnExit(e);
        }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        window.ActivateFromExternalRequest();
    }

    public void Dispose()
    {
        _singleInstance?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string ResolveRepositoryRoot(string[] args)
    {
        var repositoryRoot = Directory.GetCurrentDirectory();
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], "--repo-root", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++index >= args.Length)
            {
                throw new ArgumentException("--repo-root 必须带路径。", nameof(args));
            }

            repositoryRoot = args[index];
        }

        return AgentStartupPaths.ValidateRepositoryRoot(repositoryRoot);
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
}
