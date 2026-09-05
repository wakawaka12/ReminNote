using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ReminNote.Agent.Notifications;
using ReminNote.Agent.Runtime;
using ReminNote.Core.Protocol;
using ReminNote.Widget.Notifications;
using ReminNote.Widget.Startup;
using ReminNote.Widget.ViewModels;
using NodaTime;

namespace ReminNote.Widget;

public partial class App : Application, IDisposable
{
    private WidgetSingleInstanceCoordinator? _singleInstance;
    private AgentTaskClient? _agentClient;
    private WidgetViewModel? _viewModel;
    private WidgetNotificationChannelHost? _notificationHost;
    private NotificationHostBridgeServer? _notificationBridge;
    private DispatcherTimer? _refreshTimer;
    private CancellationTokenSource? _lifetimeCancellation;
    private bool _refreshInProgress;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var roots = WidgetStartupOptions.ResolveDataRoot(e.Args);
            var repositoryRoot = roots.RepositoryRoot ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(roots.DataRoot);
            var headless = HasHeadlessFlag(e.Args);
            var profileName = WidgetStartupOptions.ResolveProfileName(e.Args);
            var widgetInstanceId = WidgetStartupOptions.ResolveWidgetInstanceId(e.Args);
            _agentClient = new AgentTaskClient(
                repositoryRoot,
                ProtocolClientKinds.Widget,
                profileName,
                roots.DataRoot);
            _singleInstance?.Dispose();
            _singleInstance = new WidgetSingleInstanceCoordinator(
                WidgetInstanceIdentity.GetMutexName(_agentClient.ProfileScope, widgetInstanceId),
                WidgetInstanceIdentity.GetPipeName(_agentClient.ProfileScope, widgetInstanceId));
            if (!_singleInstance.IsPrimary)
            {
                _agentClient.Dispose();
                _agentClient = null;
                Shutdown(_singleInstance.TryActivateExisting() ? 0 : 1);
                return;
            }

            _viewModel = new WidgetViewModel(
                _agentClient,
                _agentClient,
                _agentClient,
                _agentClient,
                SystemClock.Instance);

            _notificationHost = WidgetNotificationChannelComposition.CreateHostOwned(
                SystemClock.Instance,
                () => MainWindow,
                Dispatcher);
            _notificationBridge = NotificationHostBridgeServer.Start(
                _agentClient.ProfileScope,
                NotificationHostBridgeKind.Widget,
                _notificationHost.Catalog);
            _lifetimeCancellation = new CancellationTokenSource();

            if (headless)
            {
                // The isolated process gate exercises Widget composition and
                // lifetime without creating a visible window or stealing
                // focus from the operator's desktop session.
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
                return;
            }

            var window = new WidgetWindow(_viewModel);
            MainWindow = window;
            _singleInstance.StartListener(() =>
                Dispatcher.BeginInvoke(new Action(() => _ = RefreshAndActivateAsync())));
            window.Show();
            StartRefreshTimer();
            _ = InitializeWidgetAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!WidgetViewModel.IsFatalException(exception))
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
        _notificationBridge?.Dispose();
        _notificationBridge = null;
        _notificationHost?.Dispose();
        _notificationHost = null;
        _viewModel?.Dispose();
        _viewModel = null;
        _agentClient?.Dispose();
        _agentClient = null;
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!WidgetViewModel.IsFatalException(exception))
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

    private static bool HasHeadlessFlag(string[] args) =>
        args.Any(argument =>
            string.Equals(argument, "--headless", StringComparison.OrdinalIgnoreCase));
}
