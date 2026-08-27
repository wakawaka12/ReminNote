using ReminNote.Windows.ViewModels;

namespace ReminNote.Windows.Features.Today;

public sealed class TodayPageViewModel : ShellPageViewModel
{
    public TodayPageViewModel()
        : base("TODAY 的导航入口已经建立，当前不读取真实任务数据。")
    {
    }
}
