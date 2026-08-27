using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ReminNote.Windows.Features.Anime;

public sealed class AnimeSectionItem : ObservableObject
{
    private int _count;
    private bool _isSelected;

    public AnimeSectionItem(string key, string label, string englishLabel)
    {
        Key = key;
        Label = label;
        EnglishLabel = englishLabel;
    }

    public string Key { get; }

    public string Label { get; }

    public string EnglishLabel { get; }

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(CountLabel));
            }
        }
    }

    public string CountLabel => Count.ToString("00", CultureInfo.InvariantCulture);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class AnimeMockEntry : ObservableObject
{
    private bool _isReminderArmed;
    private bool _isTracked;
    private bool _isWatched;
    private bool _isWatchLater;
    private int _watchedEpisodes;

    private AnimeMockEntry(
        string key,
        string displayTitle,
        string subtitle,
        string coverKicker,
        string coverGlyph,
        string coverTagline,
        string episodeLabel,
        string airingLabel,
        string countdownLabel,
        string genreLabel,
        string detailDescription,
        string accentColor,
        int watchedEpisodes,
        int totalEpisodes,
        bool isThisSeason,
        bool isTracked,
        bool isWatchLater,
        bool isPlanToWatch)
    {
        Key = key;
        DisplayTitle = displayTitle;
        Subtitle = subtitle;
        CoverKicker = coverKicker;
        CoverGlyph = coverGlyph;
        CoverTagline = coverTagline;
        EpisodeLabel = episodeLabel;
        AiringLabel = airingLabel;
        CountdownLabel = countdownLabel;
        GenreLabel = genreLabel;
        DetailDescription = detailDescription;
        AccentBrush = CreateBrush(accentColor);
        WatchedEpisodes = watchedEpisodes;
        TotalEpisodes = totalEpisodes;
        IsThisSeason = isThisSeason;
        IsTracked = isTracked;
        IsWatchLater = isWatchLater;
        IsPlanToWatch = isPlanToWatch;
    }

    public string Key { get; }

    public string DisplayTitle { get; }

    public string Subtitle { get; }

    public string CoverKicker { get; }

    public string CoverGlyph { get; }

    public string CoverTagline { get; }

    public string EpisodeLabel { get; }

    public string AiringLabel { get; }

    public string CountdownLabel { get; }

    public string GenreLabel { get; }

    public string DetailDescription { get; }

    public Brush AccentBrush { get; }

    public int TotalEpisodes { get; }

    public bool IsThisSeason { get; }

    public bool IsPlanToWatch { get; }

    public bool IsTracked
    {
        get => _isTracked;
        set
        {
            if (SetProperty(ref _isTracked, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(StatusDetailLabel));
                OnPropertyChanged(nameof(TrackingButtonText));
            }
        }
    }

    public bool IsWatchLater
    {
        get => _isWatchLater;
        set
        {
            if (SetProperty(ref _isWatchLater, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(StatusDetailLabel));
                OnPropertyChanged(nameof(WatchLaterButtonText));
                OnPropertyChanged(nameof(AiringStatusLabel));
            }
        }
    }

    public bool IsWatched
    {
        get => _isWatched;
        set
        {
            if (SetProperty(ref _isWatched, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(StatusDetailLabel));
                OnPropertyChanged(nameof(MarkWatchedButtonText));
                OnPropertyChanged(nameof(AiringStatusLabel));
            }
        }
    }

    public bool IsReminderArmed
    {
        get => _isReminderArmed;
        set
        {
            if (SetProperty(ref _isReminderArmed, value))
            {
                OnPropertyChanged(nameof(ReminderButtonText));
                OnPropertyChanged(nameof(ReminderStatusLabel));
            }
        }
    }

    public int WatchedEpisodes
    {
        get => _watchedEpisodes;
        set
        {
            if (SetProperty(ref _watchedEpisodes, value))
            {
                OnPropertyChanged(nameof(ProgressLabel));
                OnPropertyChanged(nameof(ProgressValue));
            }
        }
    }

    public double ProgressValue => TotalEpisodes == 0
        ? 0
        : Math.Round(WatchedEpisodes * 100d / TotalEpisodes, 0);

    public string ProgressLabel => $"已看 {WatchedEpisodes:00} / {TotalEpisodes:00} 集";

    public string StatusLabel => IsWatched
        ? "已看"
        : IsWatchLater
            ? "WATCH LATER"
            : IsTracked
                ? "追番中"
                : IsPlanToWatch
                    ? "PLAN TO WATCH"
                    : "未分类";

    public string StatusDetailLabel => IsWatched
        ? "CURRENT EPISODE WATCHED"
        : IsWatchLater
            ? "READY TO WATCH"
            : IsTracked
                ? "TRACKING SUBSCRIPTION"
                : IsPlanToWatch
                    ? "QUEUED FOR LATER"
                    : "LOCAL ENTRY";

    public string AiringStatusLabel => IsWatched
        ? "已完成本集"
        : IsWatchLater
            ? "已播 · 等待观看"
            : "下一集播出";

    public string WatchLaterButtonText => IsWatchLater ? "移出待看" : "加入待看";

    public string TrackingButtonText => IsTracked ? "关闭追番" : "开启追番";

    public string ReminderButtonText => IsReminderArmed ? "提醒已模拟" : "模拟提醒";

    public string ReminderStatusLabel => IsReminderArmed ? "MOCK REMINDER ON" : "NO REMINDER";

    public string MarkWatchedButtonText => IsWatched ? "已看" : "标记已看";

    public static IReadOnlyList<AnimeMockEntry> CreateCatalog() =>
    [
        new AnimeMockEntry(
            "anime-001",
            "星屑列车",
            "Stardust Local",
            "NIGHT ROUTE",
            "A-01",
            "向夜色深处出发",
            "EP 07",
            "今晚 · 23:30",
            "02:18:00",
            "科幻 · 群像",
            "一班只在夜里出现的列车，载着旅人穿过尚未命名的星座。下一集即将抵达。",
            "#6D95F5",
            6,
            12,
            true,
            true,
            false,
            false),
        new AnimeMockEntry(
            "anime-002",
            "雾港来信",
            "Letters from Mist Harbor",
            "MIST ARCHIVE",
            "B-03",
            "潮汐替我保管秘密",
            "EP 03",
            "已播 · 昨日 22:00",
            "WATCH LATER",
            "悬疑 · 日常",
            "港口的旧灯每晚亮三次，第三次亮起时，总会有一封没有寄件人的信。",
            "#7D9AA8",
            2,
            12,
            true,
            true,
            true,
            false),
        new AnimeMockEntry(
            "anime-003",
            "四月的玻璃海",
            "Glass Sea in April",
            "APRIL TIDE",
            "C-05",
            "把晴天折成一封信",
            "EP 09",
            "明天 · 21:00",
            "1D 21:04:00",
            "青春 · 音乐",
            "练习室窗外是一片透明的海。每一次合奏，都让他们更接近毕业前的答案。",
            "#E58BA3",
            8,
            13,
            true,
            true,
            false,
            false),
        new AnimeMockEntry(
            "anime-004",
            "猫与月光邮局",
            "Moonlit Post Office",
            "SOFT SIGNAL",
            "D-02",
            "今晚也替你寄出思念",
            "EP 01",
            "本季 · 等待开看",
            "PLAN TO WATCH",
            "治愈 · 奇幻",
            "月光邮局只收不会说出口的心事，夜班邮差是一只戴着蓝围巾的猫。",
            "#D6A55D",
            0,
            10,
            true,
            false,
            false,
            true),
        new AnimeMockEntry(
            "anime-005",
            "白昼终焉",
            "The Last Daylight",
            "AFTERGLOW",
            "E-11",
            "日落以后仍有答案",
            "EP 05",
            "已播 · 周一 20:00",
            "WATCH LATER",
            "动作 · 冒险",
            "世界的白昼正在缩短，五名少年沿着最后一束光寻找失踪的夏天。",
            "#AD8BE8",
            4,
            24,
            false,
            false,
            true,
            false)
    ];

    private static SolidColorBrush CreateBrush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
