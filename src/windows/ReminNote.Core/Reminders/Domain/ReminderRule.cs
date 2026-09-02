using NodaTime;
using ReminNote.Core;

namespace ReminNote.Core.Reminders.Domain;

/// <summary>
/// 当前有效的提醒业务意图。Rule 是可变真相；每一次语义更新都会产生新的
/// <see cref="RuleRevision"/>，而已经发生的 Instance 永远不会被回写。
/// </summary>
public sealed class ReminderRule
{
    private ReminderRule(
        ReminderRuleId id,
        ReminderTargetKind targetKind,
        Guid targetId,
        OccurrenceId occurrenceId,
        ReminderPurpose purpose,
        ReminderTiming timing,
        ReminderPriority priority,
        bool pinned,
        RepeatPolicy repeatPolicy,
        WakePolicy wakePolicy,
        bool enabled,
        long ruleRevision,
        Instant createdAtUtc,
        Instant updatedAtUtc)
    {
        Validate(
            id,
            targetKind,
            targetId,
            occurrenceId,
            purpose,
            timing,
            priority,
            repeatPolicy,
            wakePolicy,
            ruleRevision,
            createdAtUtc,
            updatedAtUtc);

        Id = id;
        TargetKind = targetKind;
        TargetId = targetId;
        OccurrenceId = occurrenceId;
        Purpose = purpose;
        Timing = timing;
        Priority = priority;
        Pinned = pinned;
        RepeatPolicy = repeatPolicy;
        WakePolicy = wakePolicy;
        Enabled = enabled;
        RuleRevision = ruleRevision;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public ReminderRuleId Id { get; }

    public ReminderTargetKind TargetKind { get; private set; }

    public Guid TargetId { get; private set; }

    public OccurrenceId OccurrenceId { get; private set; }

    public ReminderPurpose Purpose { get; private set; }

    public ReminderTiming Timing { get; private set; }

    public ReminderPriority Priority { get; private set; }

    /// <summary>PIN 与优先级是两个独立的业务维度。</summary>
    public bool Pinned { get; private set; }

    public RepeatPolicy RepeatPolicy { get; private set; }

    public WakePolicy WakePolicy { get; private set; }

    public bool Enabled { get; private set; }

    public long RuleRevision { get; private set; }

    public Instant CreatedAtUtc { get; }

    public Instant UpdatedAtUtc { get; private set; }

    public static ReminderRule Create(
        ReminderTargetKind targetKind,
        Guid targetId,
        OccurrenceId occurrenceId,
        ReminderPurpose purpose,
        ReminderTiming timing,
        ReminderPriority priority,
        bool pinned,
        RepeatPolicy repeatPolicy,
        WakePolicy wakePolicy,
        bool enabled,
        Instant createdAtUtc) =>
        Create(
            ReminderRuleId.New(),
            targetKind,
            targetId,
            occurrenceId,
            purpose,
            timing,
            priority,
            pinned,
            repeatPolicy,
            wakePolicy,
            enabled,
            createdAtUtc);

    /// <summary>
    /// Allows an adapter to supply a pre-generated UUID v7 while retaining
    /// the same revision semantics as normal creation.
    /// </summary>
    public static ReminderRule Create(
        ReminderRuleId id,
        ReminderTargetKind targetKind,
        Guid targetId,
        OccurrenceId occurrenceId,
        ReminderPurpose purpose,
        ReminderTiming timing,
        ReminderPriority priority,
        bool pinned,
        RepeatPolicy repeatPolicy,
        WakePolicy wakePolicy,
        bool enabled,
        Instant createdAtUtc) =>
        new(
            id,
            targetKind,
            targetId,
            occurrenceId,
            purpose,
            timing,
            priority,
            pinned,
            repeatPolicy,
            wakePolicy,
            enabled,
            ruleRevision: 1,
            createdAtUtc,
            updatedAtUtc: createdAtUtc);

    /// <summary>
    /// The P3 one-time task adapter uses Task.Id as both target and
    /// occurrence identity. The Task aggregate itself remains outside this
    /// reminder model.
    /// </summary>
    public static ReminderRule CreateForTask(
        Guid taskId,
        ReminderPurpose purpose,
        ReminderTiming timing,
        ReminderPriority priority,
        bool pinned,
        RepeatPolicy repeatPolicy,
        WakePolicy wakePolicy,
        bool enabled,
        Instant createdAtUtc) =>
        Create(
            ReminderTargetKind.TASK_INSTANCE,
            taskId,
            OccurrenceId.From(taskId),
            purpose,
            timing,
            priority,
            pinned,
            repeatPolicy,
            wakePolicy,
            enabled,
            createdAtUtc);

    public static ReminderRule Rehydrate(
        ReminderRuleId id,
        ReminderTargetKind targetKind,
        Guid targetId,
        OccurrenceId occurrenceId,
        ReminderPurpose purpose,
        ReminderTiming timing,
        ReminderPriority priority,
        bool pinned,
        RepeatPolicy repeatPolicy,
        WakePolicy wakePolicy,
        bool enabled,
        long ruleRevision,
        Instant createdAtUtc,
        Instant updatedAtUtc) =>
        new(
            id,
            targetKind,
            targetId,
            occurrenceId,
            purpose,
            timing,
            priority,
            pinned,
            repeatPolicy,
            wakePolicy,
            enabled,
            ruleRevision,
            createdAtUtc,
            updatedAtUtc);

    /// <summary>
    /// Updates all semantic fields at once. Returns <see langword="false"/>
    /// for an exact no-op, preserving both revision and timestamp.
    /// </summary>
    public bool Update(
        ReminderTargetKind targetKind,
        Guid targetId,
        OccurrenceId occurrenceId,
        ReminderPurpose purpose,
        ReminderTiming timing,
        ReminderPriority priority,
        bool pinned,
        RepeatPolicy repeatPolicy,
        WakePolicy wakePolicy,
        bool enabled,
        Instant updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(repeatPolicy);

        if (SameSemantics(
                targetKind,
                targetId,
                occurrenceId,
                purpose,
                timing,
                priority,
                pinned,
                repeatPolicy,
                wakePolicy,
                enabled))
        {
            return false;
        }

        if (updatedAtUtc < UpdatedAtUtc)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.rule.updated_at.not_monotonic",
                "UpdatedAt cannot be earlier than the previous update.",
                nameof(updatedAtUtc)));
        }

        long nextRevision;
        try
        {
            nextRevision = checked(RuleRevision + 1);
        }
        catch (OverflowException)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.rule.revision.overflow",
                "Rule revision cannot be incremented further.",
                nameof(RuleRevision)));
        }

        Validate(
            Id,
            targetKind,
            targetId,
            occurrenceId,
            purpose,
            timing,
            priority,
            repeatPolicy,
            wakePolicy,
            nextRevision,
            CreatedAtUtc,
            updatedAtUtc);

        TargetKind = targetKind;
        TargetId = targetId;
        OccurrenceId = occurrenceId;
        Purpose = purpose;
        Timing = timing;
        Priority = priority;
        Pinned = pinned;
        RepeatPolicy = repeatPolicy;
        WakePolicy = wakePolicy;
        Enabled = enabled;
        RuleRevision = nextRevision;
        UpdatedAtUtc = updatedAtUtc;
        return true;
    }

    /// <summary>Convenience overload for updates that retain target identity.</summary>
    public bool Update(
        ReminderPurpose purpose,
        ReminderTiming timing,
        ReminderPriority priority,
        bool pinned,
        RepeatPolicy repeatPolicy,
        WakePolicy wakePolicy,
        bool enabled,
        Instant updatedAtUtc) =>
        Update(
            TargetKind,
            TargetId,
            OccurrenceId,
            purpose,
            timing,
            priority,
            pinned,
            repeatPolicy,
            wakePolicy,
            enabled,
            updatedAtUtc);

    private bool SameSemantics(
        ReminderTargetKind targetKind,
        Guid targetId,
        OccurrenceId occurrenceId,
        ReminderPurpose purpose,
        ReminderTiming timing,
        ReminderPriority priority,
        bool pinned,
        RepeatPolicy repeatPolicy,
        WakePolicy wakePolicy,
        bool enabled) =>
        TargetKind == targetKind &&
        TargetId == targetId &&
        OccurrenceId == occurrenceId &&
        Purpose == purpose &&
        Equals(Timing, timing) &&
        Priority == priority &&
        Pinned == pinned &&
        Equals(RepeatPolicy, repeatPolicy) &&
        WakePolicy == wakePolicy &&
        Enabled == enabled;

    private static void Validate(
        ReminderRuleId id,
        ReminderTargetKind targetKind,
        Guid targetId,
        OccurrenceId occurrenceId,
        ReminderPurpose purpose,
        ReminderTiming timing,
        ReminderPriority priority,
        RepeatPolicy repeatPolicy,
        WakePolicy wakePolicy,
        long ruleRevision,
        Instant createdAtUtc,
        Instant updatedAtUtc)
    {
        _ = ReminderRuleId.From(id.Value);
        _ = OccurrenceId.From(occurrenceId.Value);

        if (targetKind != ReminderTargetKind.TASK_INSTANCE)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.target_kind.unsupported",
                "Only TASK_INSTANCE reminder targets are supported in P3.",
                nameof(targetKind)));
        }

        _ = ReminderIdentityValidation.Validate(
            targetId,
            "reminder.target_id.empty",
            "reminder.target_id.not_uuid_v7",
            nameof(targetId));

        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(repeatPolicy);

        if (!Enum.IsDefined(priority))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.priority.invalid",
                "Reminder priority is not supported.",
                nameof(priority)));
        }

        if (!Enum.IsDefined(wakePolicy))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.wake_policy.invalid",
                "Reminder wake policy is not supported.",
                nameof(wakePolicy)));
        }

        ValidatePurposeAndTiming(purpose, timing);

        if (ruleRevision < 1)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.rule.revision.invalid",
                "Rule revision must start at one.",
                nameof(ruleRevision)));
        }

        if (updatedAtUtc < createdAtUtc)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.rule.updated_at.before_created_at",
                "UpdatedAt cannot be earlier than CreatedAt.",
                nameof(updatedAtUtc)));
        }
    }

    private static void ValidatePurposeAndTiming(ReminderPurpose purpose, ReminderTiming timing)
    {
        if (!Enum.IsDefined(purpose))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.purpose.invalid",
                "Reminder purpose is not supported.",
                nameof(purpose)));
        }

        if (purpose is ReminderPurpose.ANIME_PRE_AIRING or ReminderPurpose.ANIME_AIRING or ReminderPurpose.ANIME_CUSTOM)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.purpose.unsupported",
                "Anime reminder purposes are reserved and not executable in P3.",
                nameof(purpose)));
        }

        var mismatch = purpose switch
        {
            ReminderPurpose.TASK_CUSTOM => timing is not AbsoluteReminderTiming,
            ReminderPurpose.TASK_RANGE_END => timing is not RelativeReminderTiming { Anchor: ReminderAnchor.RANGE_END },
            ReminderPurpose.TASK_PRE_START => timing is not RelativeReminderTiming { Anchor: ReminderAnchor.TASK_TIME or ReminderAnchor.RANGE_START, OffsetSeconds: < 0 },
            ReminderPurpose.TASK_START => timing is not RelativeReminderTiming { Anchor: ReminderAnchor.TASK_TIME or ReminderAnchor.RANGE_START },
            _ => true
        };

        if (mismatch)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.purpose.timing_mismatch",
                "Reminder purpose and timing shape are incompatible.",
                nameof(timing)));
        }
    }
}
