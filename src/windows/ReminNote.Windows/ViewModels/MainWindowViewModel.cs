using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
            new NavigationItemViewModel(ShellPage.Today, "今天", "任务与提醒"),
            new NavigationItemViewModel(ShellPage.Anime, "动画", "追番与记录")
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
        TodayPageViewModel => "今天",
        AnimePageViewModel => "动画",
        _ => "ReminNote"
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
