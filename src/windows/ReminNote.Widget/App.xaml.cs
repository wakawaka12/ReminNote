using System.Windows;
using ReminNote.Widget.ViewModels;

namespace ReminNote.Widget;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var viewModel = new WidgetViewModel();
        var window = new WidgetWindow(viewModel);
        MainWindow = window;
        window.Show();
    }
}
