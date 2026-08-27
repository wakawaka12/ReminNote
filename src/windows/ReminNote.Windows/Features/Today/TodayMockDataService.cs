namespace ReminNote.Windows.Features.Today;

public interface ITodayMockDataService
{
    TodayMockSnapshot Load();
}

public sealed class TodayMockDataService : ITodayMockDataService
{
    public TodayMockSnapshot Load() => new(
        DateLabel: "2026年8月27日",
        WeekdayLabel: "星期四",
        PrimaryTaskId: "today-focus",
        Tasks:
        [
            new TodayMockTask(
                Id: "overdue-high",
                Title: "整理本周账单",
                Group: TodayTaskGroup.Overdue,
                TimeLabel: "昨天 · 18:00",
                Status: TodayMockStatus.Overdue,
                PriorityLabel: "HIGH",
                CategoryLabel: "生活",
                TimeShapeLabel: "TIME",
                Notes: "保留原计划供复盘；今天可以重新安排，不制造额外的失败记录。",
                IsCompleted: false,
                IsPinned: true,
                IsNeedsReview: false),
            new TodayMockTask(
                Id: "morning-stretch",
                Title: "晨间拉伸 15 分钟",
                Group: TodayTaskGroup.Morning,
                TimeLabel: "08:00",
                Status: TodayMockStatus.Completed,
                PriorityLabel: "NORMAL",
                CategoryLabel: "健康",
                TimeShapeLabel: "TIME",
                Notes: "今天的晨间计划已经记录完成。",
                IsCompleted: true,
                IsPinned: false,
                IsNeedsReview: false),
            new TodayMockTask(
                Id: "today-focus",
                Title: "准备设计评审材料",
                Group: TodayTaskGroup.Morning,
                TimeLabel: "10:30",
                Status: TodayMockStatus.Upcoming,
                PriorityLabel: "HIGH",
                CategoryLabel: "工作",
                TimeShapeLabel: "TIME",
                Notes: "主焦点示例：这是今天最值得先看一眼的计划。",
                IsCompleted: false,
                IsPinned: false,
                IsNeedsReview: false),
            new TodayMockTask(
                Id: "range-review",
                Title: "整理桌面文件",
                Group: TodayTaskGroup.Afternoon,
                TimeLabel: "14:00–16:00",
                Status: TodayMockStatus.AwaitingResult,
                PriorityLabel: "NORMAL",
                CategoryLabel: "工作",
                TimeShapeLabel: "RANGE",
                Notes: "RANGE 计划已结束，但还没有用户结果；请在记录结果后结束这条计划。",
                IsCompleted: false,
                IsPinned: false,
                IsNeedsReview: true),
            new TodayMockTask(
                Id: "afternoon-report",
                Title: "提交周报",
                Group: TodayTaskGroup.Afternoon,
                TimeLabel: "16:30",
                Status: TodayMockStatus.Upcoming,
                PriorityLabel: "HIGH",
                CategoryLabel: "工作",
                TimeShapeLabel: "TIME",
                Notes: "TIME 是计划锚点，不代表硬截止时间。",
                IsCompleted: false,
                IsPinned: false,
                IsNeedsReview: false),
            new TodayMockTask(
                Id: "evening-reading",
                Title: "阅读《人类简史》",
                Group: TodayTaskGroup.Evening,
                TimeLabel: "20:30",
                Status: TodayMockStatus.Upcoming,
                PriorityLabel: "LOW",
                CategoryLabel: "成长",
                TimeShapeLabel: "TIME",
                Notes: "晚间计划示例。TODAY 只展示计划，不追踪阅读时长。",
                IsCompleted: false,
                IsPinned: false,
                IsNeedsReview: false),
            new TodayMockTask(
                Id: "anytime-dentist",
                Title: "预约周五牙医",
                Group: TodayTaskGroup.Anytime,
                TimeLabel: "ANYTIME",
                Status: TodayMockStatus.Planned,
                PriorityLabel: "NORMAL",
                CategoryLabel: "生活",
                TimeShapeLabel: "ANYTIME",
                Notes: "ANYTIME 不因为存在其他提醒而变成 TIME。",
                IsCompleted: false,
                IsPinned: false,
                IsNeedsReview: false),
            new TodayMockTask(
                Id: "completed-review",
                Title: "完成设计评审记录",
                Group: TodayTaskGroup.Morning,
                TimeLabel: "11:30",
                Status: TodayMockStatus.Completed,
                PriorityLabel: "NORMAL",
                CategoryLabel: "工作",
                TimeShapeLabel: "TIME",
                Notes: "完成状态仅为当前进程内的 Mock 状态。",
                IsCompleted: true,
                IsPinned: false,
                IsNeedsReview: false)
        ]);
}

public sealed record TodayMockSnapshot(
    string DateLabel,
    string WeekdayLabel,
    string PrimaryTaskId,
    IReadOnlyList<TodayMockTask> Tasks);

public sealed record TodayMockTask(
    string Id,
    string Title,
    TodayTaskGroup Group,
    string TimeLabel,
    TodayMockStatus Status,
    string PriorityLabel,
    string CategoryLabel,
    string TimeShapeLabel,
    string Notes,
    bool IsCompleted,
    bool IsPinned,
    bool IsNeedsReview);

public enum TodayTaskGroup
{
    Overdue,
    Morning,
    Afternoon,
    Evening,
    Anytime,
    Completed
}

public enum TodayMockStatus
{
    Overdue,
    Completed,
    Upcoming,
    AwaitingResult,
    Planned
}
