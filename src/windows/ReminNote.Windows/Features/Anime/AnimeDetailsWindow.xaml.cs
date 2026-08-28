using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ReminNote.Windows.Features.Anime;

public partial class AnimeDetailsWindow : Window
{
    public AnimeDetailsWindow(AnimePageViewModel viewModel)
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
