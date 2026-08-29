using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ReminNote.Core.Tasks;
using ReminNote.Windows.Features.Anime;
using ReminNote.Windows.Features.Today;
using ReminNote.Windows.ViewModels;

namespace ReminNote.Windows;

public partial class MainWindow : Window, IDisposable
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _todayRefreshTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private AnimeDetailsWindow? _animeDetailsWindow;
    private TodayDetailsWindow? _todayDetailsWindow;
    private IInputElement? _focusedElementBeforeDetails;
    private IInputElement? _focusedElementBeforeTodayDetails;
    private bool _hasLoaded;
    private bool _todayRefreshInProgress;
    private bool _isClosing;
    private bool _lifetimeDisposed;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _todayRefreshTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2),
            DispatcherPriority.Background,
            OnTodayRefreshTimerTick,
            Dispatcher);
        _viewModel.TodayPage.QuickTaskAdded += OnQuickTaskAdded;
        _viewModel.TodayPage.DetailsRequested += OnTodayDetailsRequested;
        _viewModel.TodayPage.SelectedTaskInvalidated += OnTodaySelectedTaskInvalidated;
        _viewModel.AnimePage.DetailsRequested += OnDetailsRequested;
        Loaded += OnLoaded;
        Activated += OnActivated;
        Closed += OnClosed;
    }

    public void ActivateFromExternalRequest()
    {
        if (_isClosing)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Focus();
        RequestTodayRefresh();
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

    private void OnTodaySelectedTaskInvalidated(TaskId taskId)
    {
        if (_todayDetailsWindow is { IsVisible: true } detailsWindow)
        {
            detailsWindow.Close();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hasLoaded)
        {
            return;
        }

        _hasLoaded = true;
        _todayRefreshTimer.Start();
        RequestTodayRefresh();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        RequestTodayRefresh();
    }

    private async void OnTodayRefreshTimerTick(object? sender, EventArgs e)
    {
        await RefreshTodayAsync().ConfigureAwait(true);
    }

    private void RequestTodayRefresh()
    {
        if (!_hasLoaded || _isClosing || _todayRefreshInProgress)
        {
            return;
        }

        _ = RefreshTodayAsync();
    }

    private async System.Threading.Tasks.Task RefreshTodayAsync()
    {
        if (!_hasLoaded || _isClosing || _todayRefreshInProgress)
        {
            return;
        }

        _todayRefreshInProgress = true;
        var cancellationToken = _lifetimeCancellation.Token;
        try
        {
            await _viewModel.TodayPage
                .RefreshAsync(cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Main TODAY refresh failed: {exception}");
        }
        finally
        {
            _todayRefreshInProgress = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosing = true;
        Dispose();
        Loaded -= OnLoaded;
        Activated -= OnActivated;
        Closed -= OnClosed;
        _viewModel.TodayPage.QuickTaskAdded -= OnQuickTaskAdded;
        _viewModel.TodayPage.DetailsRequested -= OnTodayDetailsRequested;
        _viewModel.TodayPage.SelectedTaskInvalidated -= OnTodaySelectedTaskInvalidated;
        _viewModel.AnimePage.DetailsRequested -= OnDetailsRequested;
        _animeDetailsWindow?.Close();
        _todayDetailsWindow?.Close();
    }

    public void Dispose()
    {
        if (_lifetimeDisposed)
        {
            return;
        }

        _lifetimeDisposed = true;
        _todayRefreshTimer.Stop();
        _todayRefreshTimer.Tick -= OnTodayRefreshTimerTick;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnDetailsClosed(object? sender, EventArgs e)
    {
        if (sender is Window detailsWindow)
        {
            detailsWindow.Closed -= OnDetailsClosed;
        }

        _animeDetailsWindow = null;
        if (_isClosing)
        {
            _focusedElementBeforeDetails = null;
            return;
        }

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
        if (_isClosing)
        {
            _focusedElementBeforeTodayDetails = null;
            return;
        }

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
