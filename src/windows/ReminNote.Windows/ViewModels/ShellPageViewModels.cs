using CommunityToolkit.Mvvm.ComponentModel;

namespace ReminNote.Windows.ViewModels;

public abstract class ShellPageViewModel : ObservableObject
{
    protected ShellPageViewModel(string description)
    {
        Description = description;
    }

    public string Description { get; }
}

public sealed class TodayPageViewModel : ShellPageViewModel
{
    public TodayPageViewModel()
        : base("TODAY 的导航入口已经建立，当前不读取真实任务数据。")
    {
    }
}

public sealed class AnimePageViewModel : ShellPageViewModel
{
    public AnimePageViewModel()
        : base("ANIME 的导航入口已经建立，当前不读取网络或本地动画数据。")
    {
    }
}
