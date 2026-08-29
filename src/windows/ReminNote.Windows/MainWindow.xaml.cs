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
    private TodayDetailsWindow? _todayDetailsWindow;
    private IInputElement? _focusedElementBeforeDetails;
    private IInputElement? _focusedElementBeforeTodayDetails;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.TodayPage.QuickTaskAdded += OnQuickTaskAdded;
        _viewModel.TodayPage.DetailsRequested += OnTodayDetailsRequested;
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

    private void OnTodayDetailsRequested(TodayTaskViewModel task)
    {
        if (!ReferenceEquals(_viewModel.TodayPage.SelectedTask, task))
        {
            return;
        }

        if (_todayDetailsWindow is { IsVisible: true } existingWindow)
        {
            existingWindow.Activate();
            return;
        }

        _focusedElementBeforeTodayDetails = Keyboard.FocusedElement;
        var detailsWindow = new TodayDetailsWindow(_viewModel.TodayPage)
        {
            Owner = this
        };
        _todayDetailsWindow = detailsWindow;
        detailsWindow.Closed += OnTodayDetailsClosed;
        detailsWindow.ShowDialog();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.TodayPage.QuickTaskAdded -= OnQuickTaskAdded;
        _viewModel.TodayPage.DetailsRequested -= OnTodayDetailsRequested;
        _viewModel.AnimePage.DetailsRequested -= OnDetailsRequested;
        _animeDetailsWindow?.Close();
        _todayDetailsWindow?.Close();
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

    private void OnTodayDetailsClosed(object? sender, EventArgs e)
    {
        if (sender is Window detailsWindow)
        {
            detailsWindow.Closed -= OnTodayDetailsClosed;
        }

        _todayDetailsWindow = null;
        Activate();
        if (_focusedElementBeforeTodayDetails is UIElement focusTarget && focusTarget.Focusable)
        {
            focusTarget.Focus();
        }
        else
        {
            Focus();
        }

        _focusedElementBeforeTodayDetails = null;
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
