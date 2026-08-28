using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using ReminNote.Windows.Features.Anime;
using ReminNote.Windows.Features.Today;
using ReminNote.Windows.Resources.Localization;
using ReminNote.Windows.ViewModels;

namespace ReminNote.Windows;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.TodayPage.QuickTaskAdded += OnQuickTaskAdded;
        _viewModel.AnimePage.SelectedAnimeChanged += OnSelectedAnimeChanged;
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

    private void OnSelectedAnimeChanged(AnimeMockEntry entry)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => FindVisualChild<FrameworkElement>(
                MainContent,
                element => AutomationProperties.GetName(element) == UiText.AnimeSelectedAnime)?.BringIntoView()));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.TodayPage.QuickTaskAdded -= OnQuickTaskAdded;
        _viewModel.AnimePage.SelectedAnimeChanged -= OnSelectedAnimeChanged;
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
