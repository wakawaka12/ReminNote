using System.Windows;
using ReminNote.Widget.Startup;
using ReminNote.Widget.ViewModels;

namespace ReminNote.Widget;

public partial class App : Application, IDisposable
{
    private WidgetSingleInstanceCoordinator? _singleInstance;

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

        var viewModel = new WidgetViewModel();
        var window = new WidgetWindow(viewModel);
        MainWindow = window;
        _singleInstance.StartListener(() => Dispatcher.BeginInvoke(new Action(ActivateWidgetWindow)));
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        _singleInstance?.Dispose();
        GC.SuppressFinalize(this);
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
