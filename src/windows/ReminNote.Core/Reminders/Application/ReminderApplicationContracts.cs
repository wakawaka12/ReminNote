using NodaTime;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Tasks;
using ResolutionActionKind = ReminNote.Core.Reminders.Domain.ResolutionAction;

namespace ReminNote.Core.Reminders.Application;

/// <summary>
/// Presentation-facing state for a read snapshot. The UI may keep displaying
/// the last complete model when the next read is unavailable, but it must not
/// treat that model as writable or current.
/// </summary>
public enum ReminderSnapshotStatus
{
    Fresh,
    Stale,
    Unavailable
}

public static class ReminderSnapshotCodes
{
    public const string QueryNotConfigured = "reminder.query.not_configured";
    public const string SnapshotNotLoaded = "reminder.snapshot.not_loaded";
}

/// <summary>
/// The bounded query contract used by Main and Widget. It intentionally has no
/// storage or SQL concepts; the adapter owns the read-only transaction.
/// </summary>
public sealed record ReminderQuery
{
    public const int DefaultMaxItems = 50;
    public const int MaxAllowedItems = 128;

    public ReminderQuery(bool includeResolved = false, int maxItems = DefaultMaxItems)
    {
        if (maxItems is < 1 or > MaxAllowedItems)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.query.max_items.invalid",
                $"Reminder query maxItems must be between 1 and {MaxAllowedItems}.",
                nameof(maxItems)));
        }

        IncludeResolved = includeResolved;
        MaxItems = maxItems;
    }

    public bool IncludeResolved { get; }

    public int MaxItems { get; }

    public static ReminderQuery ActiveOnly { get; } = new();
}

/// <summary>
/// Complete query row for a Task reminder. P3's current adapter maps
/// OccurrenceId to TaskId, while keeping both identities in the seam so a
/// future recurring Task adapter cannot silently collapse occurrences.
/// </summary>
public sealed record ReminderReadModel
{
    public ReminderReadModel(
        ReminderInstanceId instanceId,
        ReminderScheduleId scheduleId,
        ReminderRuleId ruleId,
        OccurrenceId occurrenceId,
        TaskId taskId,
        string title,
        ReminderPurpose purpose,
        ReminderPriority priority,
        bool pinned,
        Instant triggeredAtUtc,
        ReminderLifecycle lifecycle,
        ResolutionActionKind? resolutionAction = null)
    {
        _ = ReminderInstanceId.From(instanceId.Value);
        _ = ReminderScheduleId.From(scheduleId.Value);
        _ = ReminderRuleId.From(ruleId.Value);
        _ = OccurrenceId.From(occurrenceId.Value);
        _ = TaskId.From(taskId.Value);

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.read_model.title.empty",
                "Reminder query title cannot be empty.",
                nameof(title)));
        }

        if (!Enum.IsDefined(purpose) ||
            purpose is ReminderPurpose.ANIME_PRE_AIRING or ReminderPurpose.ANIME_AIRING or ReminderPurpose.ANIME_CUSTOM)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.read_model.purpose.unsupported",
                "Reminder query only exposes executable Task purposes.",
                nameof(purpose)));
        }

        if (!Enum.IsDefined(priority) || !Enum.IsDefined(lifecycle))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.read_model.state.invalid",
                "Reminder query state is not supported.",
                nameof(lifecycle)));
        }

        if (resolutionAction is { } action)
        {
            if (!Enum.IsDefined(action) ||
                action == ResolutionActionKind.WATCHED ||
                action == ResolutionActionKind.WATCH_LATER)
            {
                throw new DomainValidationException(new DomainValidationError(
                    "reminder.read_model.resolution.unsupported",
                    "Task reminders cannot expose Anime resolution actions.",
                    nameof(resolutionAction)));
            }
        }

        if (lifecycle == ReminderLifecycle.RESOLVED && resolutionAction is null)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.read_model.resolution.missing",
                "A resolved reminder must carry its resolution action.",
                nameof(resolutionAction)));
        }

        if (lifecycle != ReminderLifecycle.RESOLVED && resolutionAction is not null)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.read_model.resolution.unexpected",
                "Only a resolved reminder may carry a resolution action.",
                nameof(resolutionAction)));
        }

        InstanceId = instanceId;
        ScheduleId = scheduleId;
        RuleId = ruleId;
        OccurrenceId = occurrenceId;
        TaskId = taskId;
        Title = title;
        Purpose = purpose;
        Priority = priority;
        Pinned = pinned;
        TriggeredAtUtc = triggeredAtUtc;
        Lifecycle = lifecycle;
        ResolutionAction = resolutionAction;
    }

    public ReminderInstanceId InstanceId { get; }

    public ReminderScheduleId ScheduleId { get; }

    public ReminderRuleId RuleId { get; }

    public OccurrenceId OccurrenceId { get; }

    public TaskId TaskId { get; }

    public string Title { get; }

    public ReminderPurpose Purpose { get; }

    public ReminderPriority Priority { get; }

    public bool Pinned { get; }

    public Instant TriggeredAtUtc { get; }

    public ReminderLifecycle Lifecycle { get; }

    public ResolutionActionKind? ResolutionAction { get; }

    public bool IsUnread => Lifecycle == ReminderLifecycle.UNREAD;

    public bool IsResolved => Lifecycle == ReminderLifecycle.RESOLVED;
}

/// <summary>
/// A query result is always explicit about whether its revision is usable.
/// Items are copied so an adapter cannot mutate the model after an atomic UI
/// swap.
/// </summary>
public sealed class ReminderReadSnapshot
{
    public ReminderReadSnapshot(
        long? snapshotRevision,
        IReadOnlyList<ReminderReadModel> items,
        ReminderSnapshotStatus status,
        string? statusCode = null)
    {
        if (snapshotRevision is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshotRevision));
        }

        ArgumentNullException.ThrowIfNull(items);
        if (status is not ReminderSnapshotStatus.Fresh and not ReminderSnapshotStatus.Stale and not ReminderSnapshotStatus.Unavailable)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if ((status is ReminderSnapshotStatus.Fresh or ReminderSnapshotStatus.Stale) && snapshotRevision is null)
        {
            throw new DomainValidationException(new DomainValidationError(
                status == ReminderSnapshotStatus.Fresh
                    ? "reminder.snapshot.fresh_revision_missing"
                    : "reminder.snapshot.stale_revision_missing",
                "A usable reminder snapshot must carry its revision.",
                nameof(snapshotRevision)));
        }

        if (status != ReminderSnapshotStatus.Fresh && string.IsNullOrWhiteSpace(statusCode))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.snapshot.status_code_missing",
                "A stale or unavailable reminder snapshot must carry a stable status code.",
                nameof(statusCode)));
        }

        if (status == ReminderSnapshotStatus.Unavailable &&
            (snapshotRevision is not null || items.Count != 0))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.snapshot.unavailable_payload_invalid",
                "An unavailable reminder snapshot cannot carry a revision or rows.",
                nameof(items)));
        }

        SnapshotRevision = snapshotRevision;
        Items = Array.AsReadOnly(items.ToArray());
        Status = status;
        StatusCode = statusCode;
    }

    public long? SnapshotRevision { get; }

    public IReadOnlyList<ReminderReadModel> Items { get; }

    public ReminderSnapshotStatus Status { get; }

    public string? StatusCode { get; }

    public static ReminderReadSnapshot Fresh(long snapshotRevision, IReadOnlyList<ReminderReadModel> items) =>
        new(snapshotRevision, items, ReminderSnapshotStatus.Fresh);

    public static ReminderReadSnapshot Stale(
        long? snapshotRevision,
        IReadOnlyList<ReminderReadModel> items,
        string statusCode) =>
        new(snapshotRevision, items, ReminderSnapshotStatus.Stale, statusCode);

    public static ReminderReadSnapshot Unavailable(string statusCode) =>
        new(null, Array.Empty<ReminderReadModel>(), ReminderSnapshotStatus.Unavailable, statusCode);
}

/// <summary>
/// Client-side state for the last complete reminder model. A failed read does
/// not erase that model, but it does change the state to Unavailable and makes
/// the revision unusable for commands. Keeping this transition in Core makes
/// Main and Widget obey the same stale/unavailable contract.
/// </summary>
public sealed class ReminderSnapshotState
{
    private ReminderSnapshotState(
        bool hasSnapshot,
        long? snapshotRevision,
        IReadOnlyList<ReminderReadModel> items,
        ReminderSnapshotStatus status,
        string? statusCode)
    {
        if (snapshotRevision is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshotRevision));
        }

        ArgumentNullException.ThrowIfNull(items);
        if (status is not ReminderSnapshotStatus.Fresh and not ReminderSnapshotStatus.Stale and not ReminderSnapshotStatus.Unavailable)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if ((status is ReminderSnapshotStatus.Fresh or ReminderSnapshotStatus.Stale) && snapshotRevision is null)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.snapshot.state.revision_missing",
                "A fresh or stale reminder state must carry its revision.",
                nameof(snapshotRevision)));
        }

        if (status != ReminderSnapshotStatus.Fresh && string.IsNullOrWhiteSpace(statusCode))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.snapshot.state.status_code_missing",
                "A stale or unavailable reminder state must carry a stable status code.",
                nameof(statusCode)));
        }

        HasSnapshot = hasSnapshot;
        SnapshotRevision = snapshotRevision;
        Items = Array.AsReadOnly(items.ToArray());
        Status = status;
        StatusCode = statusCode;
    }

    public static ReminderSnapshotState Empty { get; } = new(
        hasSnapshot: false,
        snapshotRevision: null,
        items: Array.Empty<ReminderReadModel>(),
        status: ReminderSnapshotStatus.Unavailable,
        statusCode: ReminderSnapshotCodes.SnapshotNotLoaded);

    public bool HasSnapshot { get; }

    public long? SnapshotRevision { get; }

    public IReadOnlyList<ReminderReadModel> Items { get; }

    public ReminderSnapshotStatus Status { get; }

    public string? StatusCode { get; }

    public bool HasItems => Items.Count > 0;

    public bool CanWrite =>
        HasSnapshot &&
        Status == ReminderSnapshotStatus.Fresh &&
        SnapshotRevision is not null;

    public ReminderSnapshotState Apply(ReminderReadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Status == ReminderSnapshotStatus.Unavailable)
        {
            return new(
                HasSnapshot,
                SnapshotRevision,
                Items,
                ReminderSnapshotStatus.Unavailable,
                snapshot.StatusCode);
        }

        return new(
            hasSnapshot: true,
            snapshotRevision: snapshot.SnapshotRevision,
            items: snapshot.Items,
            status: snapshot.Status,
            statusCode: snapshot.StatusCode);
    }

    public ReminderSnapshotState MarkStale(string statusCode) => WithStatus(
        ReminderSnapshotStatus.Stale,
        statusCode);

    public ReminderSnapshotState MarkUnavailable(string statusCode) => WithStatus(
        ReminderSnapshotStatus.Unavailable,
        statusCode);

    private ReminderSnapshotState WithStatus(
        ReminderSnapshotStatus status,
        string statusCode) => new(
            HasSnapshot,
            SnapshotRevision,
            Items,
            status,
            statusCode);
}

public static class ReminderCommandLimits
{
    public const long MaxSnoozeSeconds = 7 * 24 * 60 * 60;
}

public sealed record ReminderActionCommand
{
    public ReminderActionCommand(
        ReminderInstanceId instanceId,
        ResolutionActionKind action,
        long expectedRevision,
        long? snoozeSeconds = null)
    {
        _ = ReminderInstanceId.From(instanceId.Value);
        if (expectedRevision < 0)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.command.expected_revision.invalid",
                "Expected revision cannot be negative.",
                nameof(expectedRevision)));
        }

        if (action is not (ResolutionActionKind.DONE or ResolutionActionKind.SNOOZE or ResolutionActionKind.SKIP or ResolutionActionKind.IGNORE))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.command.action.unsupported",
                "The action is not enabled for Task reminders.",
                nameof(action)));
        }

        if (action == ResolutionActionKind.SNOOZE)
        {
            if (snoozeSeconds is not (> 0 and <= ReminderCommandLimits.MaxSnoozeSeconds))
            {
                throw new DomainValidationException(new DomainValidationError(
                    "reminder.command.snooze.invalid",
                    $"Snooze seconds must be between 1 and {ReminderCommandLimits.MaxSnoozeSeconds}.",
                    nameof(snoozeSeconds)));
            }
        }
        else if (snoozeSeconds is not null)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.command.snooze.unexpected",
                "Only SNOOZE accepts snooze seconds.",
                nameof(snoozeSeconds)));
        }

        InstanceId = instanceId;
        Action = action;
        ExpectedRevision = expectedRevision;
        SnoozeSeconds = snoozeSeconds;
    }

    public ReminderInstanceId InstanceId { get; }

    public ResolutionActionKind Action { get; }

    public long ExpectedRevision { get; }

    public long? SnoozeSeconds { get; }
}

public sealed record ReminderMarkReadCommand
{
    public ReminderMarkReadCommand(
        ReminderInstanceId instanceId,
        long expectedRevision)
    {
        _ = ReminderInstanceId.From(instanceId.Value);
        if (expectedRevision < 0)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.command.expected_revision.invalid",
                "Expected revision cannot be negative.",
                nameof(expectedRevision)));
        }

        InstanceId = instanceId;
        ExpectedRevision = expectedRevision;
    }

    public ReminderInstanceId InstanceId { get; }

    public long ExpectedRevision { get; }

    public void Validate()
    {
        _ = ReminderInstanceId.From(InstanceId.Value);
        if (ExpectedRevision < 0)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.command.expected_revision.invalid",
                "Expected revision cannot be negative.",
                nameof(ExpectedRevision)));
        }
    }
}

public enum ReminderCommandOutcome
{
    Changed,
    NoOp,
    Stale,
    Rejected,
    Unavailable
}

public sealed record ReminderCommandResult(
    ReminderCommandOutcome Outcome,
    long? CommittedRevision = null,
    string? ErrorCode = null)
{
    public bool Succeeded => Outcome is ReminderCommandOutcome.Changed or ReminderCommandOutcome.NoOp;

    public bool IsStale => Outcome == ReminderCommandOutcome.Stale;

    public bool IsUnavailable => Outcome == ReminderCommandOutcome.Unavailable;
}

public interface IReminderQueryService
{
    ValueTask<ReminderReadSnapshot> GetAsync(
        ReminderQuery query,
        CancellationToken cancellationToken = default);
}

public interface IReminderCommandClient
{
    ValueTask<ReminderCommandResult> ExecuteAsync(
        ReminderActionCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<ReminderCommandResult> MarkReadAsync(
        ReminderMarkReadCommand command,
        CancellationToken cancellationToken = default);
}
