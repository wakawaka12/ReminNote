using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Services.AddTodayFeature();
        builder.Services.AddAnimeFeature();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
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
            _host?.StopAsync().GetAwaiter().GetResult();
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
        if (MainWindow is not { } window)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Focus();
    }

    public void Dispose()
    {
        _singleInstance?.Dispose();
        GC.SuppressFinalize(this);
    }
}
