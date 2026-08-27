using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReminNote.Windows.Features.Anime;
using ReminNote.Windows.Features.Today;
using ReminNote.Windows.Resources.Localization;

namespace ReminNote.Windows.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly TodayPageViewModel _todayPage;
    private readonly AnimePageViewModel _animePage;
    private ShellPageViewModel _currentPage;

    public MainWindowViewModel(
        TodayPageViewModel todayPage,
        AnimePageViewModel animePage)
    {
        _todayPage = todayPage;
        _animePage = animePage;
        NavigationItems =
        [
            new NavigationItemViewModel(ShellPage.Today, UiText.ShellTodayTitle, UiText.ShellTodaySubtitle),
            new NavigationItemViewModel(ShellPage.Anime, UiText.ShellAnimeTitle, UiText.ShellAnimeSubtitle)
        ];
        NavigateCommand = new RelayCommand<NavigationItemViewModel?>(Navigate);
        _currentPage = _todayPage;
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; }

    public IRelayCommand<NavigationItemViewModel?> NavigateCommand { get; }

    public ShellPageViewModel CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string ActiveTitle => CurrentPage switch
    {
        TodayPageViewModel => UiText.ShellTodayTitle,
        AnimePageViewModel => UiText.ShellAnimeTitle,
        _ => UiText.ShellFallbackTitle
    };

    private void Navigate(NavigationItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        CurrentPage = item.Page switch
        {
            ShellPage.Today => _todayPage,
            ShellPage.Anime => _animePage,
            _ => _todayPage
        };
        OnPropertyChanged(nameof(ActiveTitle));
    }
}
