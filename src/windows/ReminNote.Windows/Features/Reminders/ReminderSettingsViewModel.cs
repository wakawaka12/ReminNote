using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NodaTime;
using NodaTime.Text;
using ReminNote.Core.Application;
using ReminNote.Core.Reminders.Application;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Tasks;
using ReminNote.Windows.ViewModels;

namespace ReminNote.Windows.Features.Reminders;

/// <summary>
/// User-facing rule editor. It deliberately talks only to the Agent query and
/// command ports: a settings edit cannot open SQLite or create a second writer.
/// </summary>
public sealed class ReminderSettingsViewModel : ShellPageViewModel, IDisposable
{
    private readonly ITaskQueryService taskQueryService;
    private readonly IReminderRuleQueryService ruleQueryService;
    private readonly IReminderRuleCommandClient ruleCommandClient;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private string interactionMessage = "提醒规则设置会通过 Agent 单写者保存。";
    private bool isRefreshing;
    private int disposed;

    public ReminderSettingsViewModel(
        ITaskQueryService taskQueryService,
        IReminderRuleQueryService ruleQueryService,
        IReminderRuleCommandClient ruleCommandClient)
        : base("提醒规则 · 为每个 Task 配置提前、开始、重复和优先级")
    {
        this.taskQueryService = taskQueryService ?? throw new ArgumentNullException(nameof(taskQueryService));
        this.ruleQueryService = ruleQueryService ?? throw new ArgumentNullException(nameof(ruleQueryService));
        this.ruleCommandClient = ruleCommandClient ?? throw new ArgumentNullException(nameof(ruleCommandClient));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsRefreshing);
    }

    public ObservableCollection<ReminderTaskSettingsItemViewModel> Tasks { get; } = [];

    public bool HasTasks => Tasks.Count > 0;

    public bool HasNoTasks => !HasTasks;

    public bool IsRefreshing
    {
        get => isRefreshing;
        private set
        {
            if (SetProperty(ref isRefreshing, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string InteractionMessage
    {
        get => interactionMessage;
        private set => SetProperty(ref interactionMessage, value);
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public async System.Threading.Tasks.Task RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var entered = false;
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        entered = true;
        IsRefreshing = true;
        try
        {
            var tasks = new List<TaskSnapshot>();
            await foreach (var task in taskQueryService
                .ListAsync(new TaskQuery(), cancellationToken)
                .ConfigureAwait(true))
            {
                tasks.Add(task);
                if (tasks.Count >= ReminderRuleQuery.MaxAllowedItems)
                {
                    break;
                }
            }

            var rules = await ruleQueryService
                .GetAsync(new ReminderRuleQuery(maxItems: ReminderRuleQuery.MaxAllowedItems), cancellationToken)
                .ConfigureAwait(true);
            if (rules.Status != ReminderSnapshotStatus.Fresh)
            {
                InteractionMessage = $"提醒规则读取失败 · {rules.StatusCode ?? "unknown"} · 请点击刷新重试。";
                return;
            }

            var byTask = rules.Items
                .GroupBy(rule => rule.TaskId)
                .ToDictionary(group => group.Key, group => group.ToArray());
            Tasks.Clear();
            foreach (var task in tasks.OrderBy(value => value.TimeSpec.LocalDate).ThenBy(value => value.SortOrder).ThenBy(value => value.Id.Value))
            {
                byTask.TryGetValue(task.Id, out var taskRules);
                Tasks.Add(new ReminderTaskSettingsItemViewModel(
                    task,
                    taskRules ?? Array.Empty<ReminderRuleReadModel>(),
                    SaveRuleAsync,
                    AddRule));
            }

            OnPropertyChanged(nameof(HasTasks));
            OnPropertyChanged(nameof(HasNoTasks));
            InteractionMessage = $"已读取 {Tasks.Count.ToString(CultureInfo.InvariantCulture)} 个 Task 的提醒规则。";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            InteractionMessage = $"提醒规则读取失败 · {GetFailureCode(exception)} · 请点击刷新重试。";
        }
        finally
        {
            if (entered)
            {
                IsRefreshing = false;
                refreshGate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            lifetimeCancellation.Cancel();
            // An in-flight refresh may still be unwinding on the UI context.
            // Keep the gate alive until that continuation releases it; the
            // view model is short-lived and the semaphore is then collectible.
            lifetimeCancellation.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void AddRule(ReminderTaskSettingsItemViewModel task)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.AddRule();
        InteractionMessage = $"已为「{task.Title}」添加新的本地规则草稿 · 点击保存后才会写入。";
    }

    private async System.Threading.Tasks.Task SaveRuleAsync(
        ReminderTaskSettingsItemViewModel task,
        ReminderRuleEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(editor);
        if (!editor.TryBuildOptions(task.Task, out var options, out var validationMessage))
        {
            editor.SetFeedback(validationMessage);
            InteractionMessage = validationMessage;
            return;
        }

        var result = await ruleCommandClient
            .UpsertAsync(task.Task.Id, options!, editor.RuleId, lifetimeCancellation.Token)
            .ConfigureAwait(true);
        if (!result.Succeeded)
        {
            var code = result.ErrorCode ?? result.Outcome.ToString();
            var message = $"规则保存失败 · {code}";
            editor.SetFeedback(message);
            InteractionMessage = message;
            return;
        }

        if (editor.RuleId is null && result.RuleId is { } createdRuleId)
        {
            editor.SetRuleId(createdRuleId);
        }

        editor.SetFeedback(editor.RuleId is null ? "已保存规则。" : "已保存规则 · Agent 已提交。");
        InteractionMessage = $"已保存「{task.Title}」的提醒规则 · revision {result.CommittedRevision?.ToString(CultureInfo.InvariantCulture) ?? "—"}。";
        await RefreshAsync(lifetimeCancellation.Token).ConfigureAwait(true);
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(disposed != 0, this);

    private static string GetFailureCode(Exception exception) => exception switch
    {
        Agent.Runtime.AgentCommandException agentException => agentException.Code,
        FileNotFoundException or DirectoryNotFoundException or IOException => "storage.not_ready",
        _ => "agent.unavailable"
    };

    private static bool IsFatal(Exception exception) => exception switch
    {
        OutOfMemoryException or StackOverflowException or AccessViolationException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsFatal),
        _ => exception.InnerException is not null && IsFatal(exception.InnerException)
    };
}

public sealed class ReminderTaskSettingsItemViewModel : ObservableObject
{
    private readonly Action<ReminderTaskSettingsItemViewModel> addRule;
    private readonly Func<ReminderTaskSettingsItemViewModel, ReminderRuleEditorViewModel, System.Threading.Tasks.Task> saveRule;

    public ReminderTaskSettingsItemViewModel(
        TaskSnapshot task,
        IReadOnlyList<ReminderRuleReadModel> rules,
        Func<ReminderTaskSettingsItemViewModel, ReminderRuleEditorViewModel, System.Threading.Tasks.Task> saveRule,
        Action<ReminderTaskSettingsItemViewModel> addRule)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        ArgumentNullException.ThrowIfNull(rules);
        this.saveRule = saveRule ?? throw new ArgumentNullException(nameof(saveRule));
        this.addRule = addRule ?? throw new ArgumentNullException(nameof(addRule));
        AddRuleCommand = new RelayCommand(() => this.addRule(this));
        foreach (var rule in rules)
        {
            Rules.Add(new ReminderRuleEditorViewModel(this, task, rule, this.saveRule));
        }
    }

    public TaskSnapshot Task { get; }

    public string Title => Task.Title;

    public string TimeLabel => Task.TimeSpec.Type.ToString();

    public ObservableCollection<ReminderRuleEditorViewModel> Rules { get; } = [];

    public bool HasRules => Rules.Count > 0;

    public bool HasNoRules => !HasRules;

    public IRelayCommand AddRuleCommand { get; }

    internal void AddRule()
    {
        Rules.Add(new ReminderRuleEditorViewModel(this, Task, model: null, saveRule));
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasNoRules));
    }
}

public sealed class ReminderRuleEditorViewModel : ObservableObject
{
    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'");

    private readonly Func<ReminderTaskSettingsItemViewModel, ReminderRuleEditorViewModel, System.Threading.Tasks.Task> saveRule;
    private readonly ReminderTaskSettingsItemViewModel taskItem;
    private readonly TaskSnapshot task;
    private ReminderRuleId? ruleId;
    private string purpose;
    private string offsetMinutesText;
    private string customUtcText;
    private string priority;
    private bool pinned;
    private bool repeatEnabled;
    private string repeatIntervalMinutesText;
    private string repeatMaxCountText;
    private string wakePolicy;
    private bool enabled;
    private bool isBusy;
    private string feedback = string.Empty;

    public ReminderRuleEditorViewModel(
        ReminderTaskSettingsItemViewModel taskItem,
        TaskSnapshot task,
        ReminderRuleReadModel? model,
        Func<ReminderTaskSettingsItemViewModel, ReminderRuleEditorViewModel, System.Threading.Tasks.Task> saveRule)
    {
        this.taskItem = taskItem ?? throw new ArgumentNullException(nameof(taskItem));
        this.task = task ?? throw new ArgumentNullException(nameof(task));
        this.saveRule = saveRule ?? throw new ArgumentNullException(nameof(saveRule));
        ruleId = model?.RuleId;
        purpose = model?.Purpose.ToString() ?? ReminderPurpose.TASK_START.ToString();
        offsetMinutesText = FormatOffsetMinutes(model?.Timing as RelativeReminderTiming);
        customUtcText = model?.Timing is AbsoluteReminderTiming absolute
            ? InstantPattern.Format(absolute.AtUtc)
            : string.Empty;
        priority = (model?.Priority ?? ReminderPriority.NORMAL).ToString();
        pinned = model?.Pinned ?? false;
        repeatEnabled = model?.RepeatPolicy.Enabled ?? false;
        repeatIntervalMinutesText = model?.RepeatPolicy.IntervalSeconds is { } interval
            ? (interval / 60d).ToString("0.##", CultureInfo.InvariantCulture)
            : "15";
        repeatMaxCountText = model?.RepeatPolicy.MaxCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        wakePolicy = (model?.WakePolicy ?? global::ReminNote.Core.Reminders.Domain.WakePolicy.DEFAULT).ToString();
        enabled = model?.Enabled ?? true;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
    }

    public static IReadOnlyList<string> PurposeOptions { get; } =
        [nameof(ReminderPurpose.TASK_PRE_START), nameof(ReminderPurpose.TASK_START), nameof(ReminderPurpose.TASK_RANGE_END), nameof(ReminderPurpose.TASK_CUSTOM)];

    public static IReadOnlyList<string> PriorityOptions { get; } =
        [nameof(ReminderPriority.LOW), nameof(ReminderPriority.NORMAL), nameof(ReminderPriority.HIGH)];

    public static IReadOnlyList<string> WakePolicyOptions { get; } =
        [nameof(global::ReminNote.Core.Reminders.Domain.WakePolicy.DEFAULT),
            nameof(global::ReminNote.Core.Reminders.Domain.WakePolicy.YES),
            nameof(global::ReminNote.Core.Reminders.Domain.WakePolicy.NO)];

    public ReminderRuleId? RuleId => ruleId;

    public string RuleIdLabel => ruleId?.ToString() ?? "新规则";

    public string Purpose
    {
        get => purpose;
        set
        {
            if (SetProperty(ref purpose, value))
            {
                OnPropertyChanged(nameof(IsCustomTiming));
                OnPropertyChanged(nameof(IsRangeEnd));
            }
        }
    }

    public string OffsetMinutesText
    {
        get => offsetMinutesText;
        set => SetProperty(ref offsetMinutesText, value);
    }

    public string CustomUtcText
    {
        get => customUtcText;
        set => SetProperty(ref customUtcText, value);
    }

    public string Priority
    {
        get => priority;
        set => SetProperty(ref priority, value);
    }

    public bool Pinned
    {
        get => pinned;
        set => SetProperty(ref pinned, value);
    }

    public bool RepeatEnabled
    {
        get => repeatEnabled;
        set => SetProperty(ref repeatEnabled, value);
    }

    public string RepeatIntervalMinutesText
    {
        get => repeatIntervalMinutesText;
        set => SetProperty(ref repeatIntervalMinutesText, value);
    }

    public string RepeatMaxCountText
    {
        get => repeatMaxCountText;
        set => SetProperty(ref repeatMaxCountText, value);
    }

    public string WakePolicy
    {
        get => wakePolicy;
        set => SetProperty(ref wakePolicy, value);
    }

    public bool Enabled
    {
        get => enabled;
        set => SetProperty(ref enabled, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                SaveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string Feedback
    {
        get => feedback;
        private set
        {
            if (SetProperty(ref feedback, value))
            {
                OnPropertyChanged(nameof(HasFeedback));
            }
        }
    }

    public bool HasFeedback => !string.IsNullOrWhiteSpace(Feedback);

    public bool IsCustomTiming => string.Equals(Purpose, nameof(ReminderPurpose.TASK_CUSTOM), StringComparison.Ordinal);

    public bool IsRangeEnd => string.Equals(Purpose, nameof(ReminderPurpose.TASK_RANGE_END), StringComparison.Ordinal);

    public IAsyncRelayCommand SaveCommand { get; }

    internal void SetFeedback(string message)
    {
        Feedback = message ?? string.Empty;
    }

    internal void SetRuleId(ReminderRuleId value)
    {
        ruleId = value;
        OnPropertyChanged(nameof(RuleId));
        OnPropertyChanged(nameof(RuleIdLabel));
    }

    public bool TryBuildOptions(
        TaskSnapshot taskSnapshot,
        out ReminderRuleOptions? options,
        out string validationMessage)
    {
        ArgumentNullException.ThrowIfNull(taskSnapshot);
        options = null;
        if (!Enum.TryParse<ReminderPurpose>(Purpose, false, out var parsedPurpose) ||
            parsedPurpose is ReminderPurpose.ANIME_PRE_AIRING or ReminderPurpose.ANIME_AIRING or ReminderPurpose.ANIME_CUSTOM)
        {
            validationMessage = "用途必须是 TASK_PRE_START、TASK_START、TASK_RANGE_END 或 TASK_CUSTOM。";
            return false;
        }

        if (!Enum.TryParse<ReminderPriority>(Priority, false, out var parsedPriority) || !Enum.IsDefined(parsedPriority))
        {
            validationMessage = "优先级无效。";
            return false;
        }

        if (!Enum.TryParse<WakePolicy>(WakePolicy, false, out var parsedWake) || !Enum.IsDefined(parsedWake))
        {
            validationMessage = "唤醒策略无效。";
            return false;
        }

        ReminderTiming timing;
        if (parsedPurpose == ReminderPurpose.TASK_CUSTOM)
        {
            var parse = InstantPattern.Parse(CustomUtcText.Trim());
            if (!parse.Success)
            {
                validationMessage = "TASK_CUSTOM 需要 canonical UTC，例如 2030-01-01T10:00:00.000000000Z。";
                return false;
            }

            timing = ReminderTiming.AbsoluteUtc(parse.Value);
        }
        else
        {
            if (!double.TryParse(OffsetMinutesText, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) ||
                double.IsNaN(minutes) || double.IsInfinity(minutes) ||
                minutes < -60 * 24 * 365 * 10 || minutes > 60 * 24 * 365 * 10)
            {
                validationMessage = "提前分钟必须是有限数字，范围为 ±10 年。";
                return false;
            }

            var offsetSeconds = checked((long)Math.Round(minutes * 60, MidpointRounding.AwayFromZero));
            if (parsedPurpose == ReminderPurpose.TASK_PRE_START && offsetSeconds >= 0)
            {
                validationMessage = "TASK_PRE_START 的提前分钟必须小于 0。";
                return false;
            }

            if (parsedPurpose == ReminderPurpose.TASK_RANGE_END && taskSnapshot.TimeSpec is not TimeRangeSpec)
            {
                validationMessage = "TASK_RANGE_END 只能用于 RANGE Task。";
                return false;
            }

            var anchor = parsedPurpose == ReminderPurpose.TASK_RANGE_END
                ? ReminderAnchor.RANGE_END
                : taskSnapshot.TimeSpec is TimeRangeSpec
                    ? ReminderAnchor.RANGE_START
                    : ReminderAnchor.TASK_TIME;
            timing = ReminderTiming.Relative(anchor, offsetSeconds);
        }

        RepeatPolicy repeat;
        if (!repeatEnabled)
        {
            repeat = RepeatPolicy.Disabled;
        }
        else
        {
            if (!double.TryParse(RepeatIntervalMinutesText, NumberStyles.Float, CultureInfo.InvariantCulture, out var intervalMinutes) ||
                intervalMinutes <= 0 || intervalMinutes > 60 * 24 * 365 * 10)
            {
                validationMessage = "重复间隔必须是大于 0 的分钟数，且不超过 10 年。";
                return false;
            }

            int? maxCount = null;
            if (!string.IsNullOrWhiteSpace(RepeatMaxCountText))
            {
                if (!int.TryParse(RepeatMaxCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMax) || parsedMax <= 0)
                {
                    validationMessage = "重复最大次数必须是正整数，留空表示使用安全上限。";
                    return false;
                }

                maxCount = parsedMax;
            }

            repeat = new RepeatPolicy(
                enabled: true,
                intervalSeconds: checked((long)Math.Round(intervalMinutes * 60, MidpointRounding.AwayFromZero)),
                maxCount);
        }

        options = new ReminderRuleOptions(
            parsedPurpose,
            timing,
            parsedPriority,
            Pinned,
            repeat,
            parsedWake,
            Enabled);
        validationMessage = string.Empty;
        return true;
    }

    private async System.Threading.Tasks.Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Feedback = string.Empty;
        try
        {
            await saveRule(taskItem, this).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatOffsetMinutes(RelativeReminderTiming? timing) =>
        timing is null
            ? "0"
            : (timing.OffsetSeconds / 60d).ToString("0.##", CultureInfo.InvariantCulture);
}
