using ReminNote.Windows.ViewModels;

namespace ReminNote.Windows.Features.Anime;

public sealed class AnimePageViewModel : ShellPageViewModel
{
    public AnimePageViewModel()
        : base("ANIME 的导航入口已经建立，当前不读取网络或本地动画数据。")
    {
    }
}
