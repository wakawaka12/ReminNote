using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ReminNote.Windows.Features.Anime;
using ReminNote.Windows.Features.Today;
using ReminNote.Windows.ViewModels;

namespace ReminNote.Windows;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private AnimeDetailsWindow? _animeDetailsWindow;
    private IInputElement? _focusedElementBeforeDetails;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.TodayPage.QuickTaskAdded += OnQuickTaskAdded;
        _viewModel.AnimePage.DetailsRequested += OnDetailsRequested;
        Closed += OnClosed;
    }

    private void OnQuickTaskAdded(TodayTaskViewModel task)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                var scrollViewer = FindVisualChild<ScrollViewer>(MainContent);
                scrollViewer?.ScrollToTop();
                FindVisualChild<FrameworkElement>(
                    MainContent,
                    element => ReferenceEquals(element.DataContext, task))?.BringIntoView();
            }));
    }

    private void OnDetailsRequested(AnimeMockEntry entry)
    {
        if (!ReferenceEquals(_viewModel.AnimePage.SelectedEntry, entry))
        {
            return;
        }

        if (_animeDetailsWindow is { IsVisible: true } existingWindow)
        {
            existingWindow.Activate();
            return;
        }

        _focusedElementBeforeDetails = Keyboard.FocusedElement;
        var detailsWindow = new AnimeDetailsWindow(_viewModel.AnimePage)
        {
            Owner = this
        };
        _animeDetailsWindow = detailsWindow;
        detailsWindow.Closed += OnDetailsClosed;
        detailsWindow.ShowDialog();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.TodayPage.QuickTaskAdded -= OnQuickTaskAdded;
        _viewModel.AnimePage.DetailsRequested -= OnDetailsRequested;
        _animeDetailsWindow?.Close();
    }

    private void OnDetailsClosed(object? sender, EventArgs e)
    {
        if (sender is Window detailsWindow)
        {
            detailsWindow.Closed -= OnDetailsClosed;
        }

        _animeDetailsWindow = null;
        Activate();
        if (_focusedElementBeforeDetails is UIElement focusTarget && focusTarget.Focusable)
        {
            focusTarget.Focus();
        }
        else
        {
            Focus();
        }

        _focusedElementBeforeDetails = null;
    }

    private static T? FindVisualChild<T>(
        DependencyObject parent,
        Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T candidate && (predicate is null || predicate(candidate)))
            {
                return candidate;
            }

            var descendant = FindVisualChild(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
