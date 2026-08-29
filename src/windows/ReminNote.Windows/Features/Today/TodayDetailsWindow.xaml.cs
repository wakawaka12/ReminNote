using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ReminNote.Windows.Features.Today;

public partial class TodayDetailsWindow : Window
{
    public TodayDetailsWindow(TodayPageViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                Activate();
                CloseButton.Focus();
                Keyboard.Focus(CloseButton);
            }));
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
