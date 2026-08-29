using System.Windows;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodaTime;
using ReminNote.Core.Application;
using ReminNote.Core.Today;
using ReminNote.Infrastructure.Application;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Windows.Features.Anime;
using ReminNote.Windows.Features.Today;
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

        _singleInstance = new SingleInstanceCoordinator(
            MainInstanceIdentity.MutexName,
            MainInstanceIdentity.PipeName);
        if (!_singleInstance.IsPrimary)
        {
            Shutdown(_singleInstance.TryActivateExisting() ? 0 : 1);
            return;
        }

        var repositoryRoot = ResolveRepositoryRoot(e.Args);
        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Services.AddSingleton(new TaskWorkspace(repositoryRoot));
        builder.Services.AddSingleton<ITaskApplicationService>(serviceProvider =>
            serviceProvider.GetRequiredService<TaskWorkspace>());
        builder.Services.AddSingleton<ITaskQueryService>(serviceProvider =>
            serviceProvider.GetRequiredService<TaskWorkspace>());
        builder.Services.AddSingleton<ITodayQueryService>(serviceProvider =>
            serviceProvider.GetRequiredService<TaskWorkspace>());
        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddTodayFeature();
        builder.Services.AddAnimeFeature();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        _host.Services.GetRequiredService<TaskWorkspace>().Initialize();
        _host.Start();

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

        return ReminNoteDatabase.ValidateRepositoryRoot(repositoryRoot);
    }
}
