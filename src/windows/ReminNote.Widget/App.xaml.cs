using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ReminNote.Widget.Startup;
using ReminNote.Widget.ViewModels;
using NodaTime;
using ReminNote.Infrastructure.Application;

namespace ReminNote.Widget;

public partial class App : Application, IDisposable
{
    private WidgetSingleInstanceCoordinator? _singleInstance;
    private TaskWorkspace? _taskWorkspace;
    private WidgetViewModel? _viewModel;
    private DispatcherTimer? _refreshTimer;
    private CancellationTokenSource? _lifetimeCancellation;
    private bool _refreshInProgress;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new WidgetSingleInstanceCoordinator(
            WidgetInstanceIdentity.MutexName,
            WidgetInstanceIdentity.PipeName);
        if (!_singleInstance.IsPrimary)
        {
            Shutdown(_singleInstance.TryActivateExisting() ? 0 : 1);
            return;
        }

        try
        {
            var repositoryRoot = WidgetStartupOptions.ResolveRepositoryRoot(e.Args);
            _taskWorkspace = new TaskWorkspace(repositoryRoot);
            _taskWorkspace.Initialize();

            _viewModel = new WidgetViewModel(
                _taskWorkspace,
                _taskWorkspace,
                SystemClock.Instance);

            var window = new WidgetWindow(_viewModel);
            MainWindow = window;
            _lifetimeCancellation = new CancellationTokenSource();
            _singleInstance.StartListener(() =>
                Dispatcher.BeginInvoke(new Action(() => _ = RefreshAndActivateAsync())));
            window.Show();
            StartRefreshTimer();
            _ = InitializeWidgetAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Widget startup failed: {exception}");
            Dispose();
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
        _lifetimeCancellation?.Cancel();
        _lifetimeCancellation?.Dispose();
        _lifetimeCancellation = null;
        _viewModel?.Dispose();
        _viewModel = null;
        _taskWorkspace?.Dispose();
        _taskWorkspace = null;
        _singleInstance?.Dispose();
        _singleInstance = null;
        GC.SuppressFinalize(this);
    }

    private void StartRefreshTimer()
    {
        _refreshTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            OnRefreshTimerTick,
            Dispatcher);
        _refreshTimer.Start();
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        await RefreshAsync().ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task InitializeWidgetAsync()
    {
        await RefreshAsync().ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task RefreshAndActivateAsync()
    {
        await RefreshAsync().ConfigureAwait(true);
        ActivateWidgetWindow();
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_viewModel is null || _refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            await _viewModel.RefreshAsync(_lifetimeCancellation?.Token ?? default)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation?.IsCancellationRequested == true)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Widget refresh failed: {exception}");
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private void ActivateWidgetWindow()
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
}
