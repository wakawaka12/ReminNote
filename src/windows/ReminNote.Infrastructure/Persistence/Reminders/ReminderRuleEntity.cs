using NodaTime;
using ReminNote.Core.Reminders.Domain;
using OccurrenceIdentity = ReminNote.Core.Reminders.Domain.OccurrenceId;

namespace ReminNote.Infrastructure.Persistence.Reminders;

/// <summary>ReminderRule 的纯关系投影；不自动注册到现有 P2 DbContext。</summary>
public sealed class ReminderRuleEntity
{
    public Guid Id { get; set; }

    public ReminderTargetKind TargetKind { get; set; }

    public Guid TargetId { get; set; }

    public Guid OccurrenceId { get; set; }

    public ReminderPurpose Purpose { get; set; }

    public ReminderTimingKind TimingKind { get; set; }

    public ReminderAnchor? TimingAnchor { get; set; }

    public long? OffsetSeconds { get; set; }

    public Instant? AbsoluteAtUtc { get; set; }

    public ReminderPriority Priority { get; set; }

    public bool Pinned { get; set; }

    public bool RepeatEnabled { get; set; }

    public long? RepeatIntervalSeconds { get; set; }

    public int? RepeatMaxCount { get; set; }

    public WakePolicy WakePolicy { get; set; }

    public bool Enabled { get; set; }

    public long RuleRevision { get; set; }

    public Instant CreatedAtUtc { get; set; }

    public Instant UpdatedAtUtc { get; set; }

    public static ReminderRuleEntity FromDomain(ReminderRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var entity = new ReminderRuleEntity
        {
            Id = rule.Id.Value,
            TargetKind = rule.TargetKind,
            TargetId = rule.TargetId,
            OccurrenceId = rule.OccurrenceId.Value,
            Purpose = rule.Purpose,
            TimingKind = rule.Timing.Kind,
            Priority = rule.Priority,
            Pinned = rule.Pinned,
            RepeatEnabled = rule.RepeatPolicy.Enabled,
            RepeatIntervalSeconds = rule.RepeatPolicy.IntervalSeconds,
            RepeatMaxCount = rule.RepeatPolicy.MaxCount,
            WakePolicy = rule.WakePolicy,
            Enabled = rule.Enabled,
            RuleRevision = rule.RuleRevision,
            CreatedAtUtc = rule.CreatedAtUtc,
            UpdatedAtUtc = rule.UpdatedAtUtc
        };

        switch (rule.Timing)
        {
            case RelativeReminderTiming relative:
                entity.TimingAnchor = relative.Anchor;
                entity.OffsetSeconds = relative.OffsetSeconds;
                entity.AbsoluteAtUtc = null;
                break;
            case AbsoluteReminderTiming absolute:
                entity.TimingAnchor = null;
                entity.OffsetSeconds = null;
                entity.AbsoluteAtUtc = absolute.AtUtc;
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported reminder timing: {rule.Timing.GetType().Name}.");
        }

        return entity;
    }

    public void Apply(ReminderRule rule)
    {
        var updated = FromDomain(rule);

        Id = updated.Id;
        TargetKind = updated.TargetKind;
        TargetId = updated.TargetId;
        OccurrenceId = updated.OccurrenceId;
        Purpose = updated.Purpose;
        TimingKind = updated.TimingKind;
        TimingAnchor = updated.TimingAnchor;
        OffsetSeconds = updated.OffsetSeconds;
        AbsoluteAtUtc = updated.AbsoluteAtUtc;
        Priority = updated.Priority;
        Pinned = updated.Pinned;
        RepeatEnabled = updated.RepeatEnabled;
        RepeatIntervalSeconds = updated.RepeatIntervalSeconds;
        RepeatMaxCount = updated.RepeatMaxCount;
        WakePolicy = updated.WakePolicy;
        Enabled = updated.Enabled;
        RuleRevision = updated.RuleRevision;
        CreatedAtUtc = updated.CreatedAtUtc;
        UpdatedAtUtc = updated.UpdatedAtUtc;
    }

    public ReminderRule ToDomain() => ReminderRule.Rehydrate(
        ReminderRuleId.From(Id),
        TargetKind,
        TargetId,
        OccurrenceIdentity.From(OccurrenceId),
        Purpose,
        ReminderTiming.FromPersistence(TimingKind, TimingAnchor, OffsetSeconds, AbsoluteAtUtc),
        Priority,
        Pinned,
        new RepeatPolicy(RepeatEnabled, RepeatIntervalSeconds, RepeatMaxCount),
        WakePolicy,
        Enabled,
        RuleRevision,
        CreatedAtUtc,
        UpdatedAtUtc);
}
