using NodaTime;
using ReminNote.Core.Tasks;

namespace ReminNote.Core.Reminders.Calculation;

/// <summary>
/// Pure schedule derivation and rebuild decisions. The caller owns UUID v7
/// allocation, timestamps, persistence, and Agent idempotency transactions.
/// </summary>
public static class ReminderSchedulePlanner
{
    public static ReminderScheduleDerivationResult DeriveFirstSchedule(
        ReminderRuleCalculationInput rule,
        ReminderScheduleRequest schedule,
        DateTimeZone? timeZone,
        ReminderCalculationLimits? limits = null)
    {
        ReminderCalculationValidation.RequireRule(rule);
        ReminderCalculationValidation.RequireScheduleRequest(schedule, requiredRevision: 1);
        ReminderCalculationValidation.ValidateRuleTimingShape(rule);
        var calculationLimits = limits ?? ReminderCalculationLimits.Default;
        ValidateRuleOffset(rule, calculationLimits);

        if (!rule.Enabled)
        {
            return ReminderScheduleDerivationResult.NotScheduled(ReminderCalculationCodes.RuleDisabled);
        }

        return DerivePendingSchedule(rule, schedule, ReminderScheduleCause.RULE, null, timeZone, calculationLimits);
    }

    /// <summary>Alias emphasizing that the first schedule is a rule projection.</summary>
    public static ReminderScheduleDerivationResult DeriveInitialSchedule(
        ReminderRuleCalculationInput rule,
        ReminderScheduleRequest schedule,
        DateTimeZone? timeZone,
        ReminderCalculationLimits? limits = null) =>
        DeriveFirstSchedule(rule, schedule, timeZone, limits);

    /// <summary>
    /// Applies a future occurrence adapter's Task.TimeSpec without defining a
    /// recurrence model in this slice.
    /// </summary>
    public static ReminderScheduleDerivationResult CalculateForOccurrence(
        ReminderRuleCalculationInput rule,
        ReminderOccurrenceInput occurrence,
        ReminderScheduleRequest schedule,
        DateTimeZone? timeZone,
        ReminderCalculationLimits? limits = null)
    {
        ReminderCalculationValidation.RequireRule(rule);
        ReminderCalculationValidation.RequireOccurrence(occurrence);

        if (rule.OccurrenceId != occurrence.OccurrenceId)
        {
            ReminderCalculationValidation.Fail(
                "reminder.occurrence.scope_mismatch",
                "Occurrence input does not belong to the reminder rule.",
                nameof(occurrence.OccurrenceId));
        }

        return DeriveFirstSchedule(rule with { TaskTimeSpec = occurrence.TaskTimeSpec }, schedule, timeZone, limits);
    }

    /// <summary>
    /// Rebuilds pending schedules after a semantic rule/task/time-zone change.
    /// Existing pending rows become terminal snapshots; historical instances
    /// are intentionally not accepted as input and are never recalculated.
    /// </summary>
    public static ReminderScheduleRebuildPlan Rebuild(
        ReminderRuleCalculationInput previousRule,
        ReminderRuleCalculationInput nextRule,
        IReadOnlyList<PendingScheduleSnapshot> pendingSchedules,
        ReminderScheduleRequest? replacementSchedule,
        DateTimeZone? timeZone,
        ReminderCalculationLimits? limits = null)
    {
        ReminderCalculationValidation.RequireRule(previousRule, nameof(previousRule));
        ReminderCalculationValidation.RequireRule(nextRule, nameof(nextRule));
        ReminderCalculationValidation.ValidateRuleTimingShape(previousRule);
        ReminderCalculationValidation.ValidateRuleTimingShape(nextRule);
        var calculationLimits = limits ?? ReminderCalculationLimits.Default;
        ValidateRuleOffset(previousRule, calculationLimits);
        ValidateRuleOffset(nextRule, calculationLimits);
        ArgumentNullException.ThrowIfNull(pendingSchedules, nameof(pendingSchedules));

        if (previousRule.RuleId != nextRule.RuleId)
        {
            ReminderCalculationValidation.Fail(
                "reminder.rule.scope_mismatch",
                "A rebuild must operate on one ReminderRule identity.",
                nameof(nextRule.RuleId));
        }

        if (previousRule.OccurrenceId != nextRule.OccurrenceId)
        {
            ReminderCalculationValidation.Fail(
                "reminder.occurrence.scope_mismatch",
                "A rebuild must operate on one occurrence identity.",
                nameof(nextRule.OccurrenceId));
        }

        var orderedPending = ValidateAndOrderPending(
            pendingSchedules,
            previousRule.RuleId,
            previousRule.OccurrenceId);

        var timeZoneChanged = HasPendingTimeZoneChange(orderedPending, nextRule, timeZone);

        if (HasSameRuleSemantics(previousRule, nextRule) && !timeZoneChanged)
        {
            if (nextRule.RuleRevision != previousRule.RuleRevision)
            {
                ReminderCalculationValidation.Fail(
                    "reminder.rule.noop_revision",
                    "A semantic no-op must not advance RuleRevision.",
                    nameof(nextRule.RuleRevision));
            }

            if (nextRule.LogicalReminderId != previousRule.LogicalReminderId)
            {
                ReminderCalculationValidation.Fail(
                    "reminder.rule.noop_logical_id",
                    "A semantic no-op must retain LogicalReminderId.",
                    nameof(nextRule.LogicalReminderId));
            }

            return new ReminderScheduleRebuildPlan(true, Array.Empty<ReminderScheduleTerminalTransition>(), null, null);
        }

        RequireNextRuleRevision(previousRule, nextRule);
        if (nextRule.LogicalReminderId == previousRule.LogicalReminderId)
        {
            ReminderCalculationValidation.Fail(
                "reminder.logical_id.must_change",
                "A semantic rule rebuild must start a new logical reminder chain.",
                nameof(nextRule.LogicalReminderId));
        }

        if (!nextRule.Enabled)
        {
            var cancelled = orderedPending
                .Select(snapshot => new ReminderScheduleTerminalTransition(
                    snapshot.ScheduleId,
                    ReminderScheduleState.CANCELLED,
                    ReminderScheduleTerminalReason.RULE_DISABLED,
                    null))
                .ToArray();

            return new ReminderScheduleRebuildPlan(
                false,
                cancelled,
                null,
                ReminderScheduleTerminalReason.RULE_DISABLED);
        }

        ReminderCalculationValidation.RequireScheduleRequest(
            replacementSchedule,
            parameterName: nameof(replacementSchedule));
        var requestedReplacement = replacementSchedule!;
        EnsureReplacementRevisionIsFresh(orderedPending, requestedReplacement);
        EnsureReplacementIdIsFresh(orderedPending, requestedReplacement.ScheduleId);

        var replacement = DerivePendingSchedule(
            nextRule,
            requestedReplacement,
            ReminderScheduleCause.RULE,
            null,
            timeZone,
            calculationLimits).Schedule!;

        var reason = DetermineRebuildReason(previousRule, nextRule, orderedPending, replacement);
        var superseded = orderedPending
            .Select(snapshot => new ReminderScheduleTerminalTransition(
                snapshot.ScheduleId,
                ReminderScheduleState.SUPERSEDED,
                reason,
                replacement.ScheduleId))
            .ToArray();

        return new ReminderScheduleRebuildPlan(false, superseded, replacement, reason);
    }

    /// <summary>
    /// Convenience overload for adapters that hold the replacement id and
    /// revision as separate persistence values.
    /// </summary>
    public static ReminderScheduleRebuildPlan Rebuild(
        ReminderRuleCalculationInput previousRule,
        ReminderRuleCalculationInput nextRule,
        IReadOnlyList<PendingScheduleSnapshot> pendingSchedules,
        Guid replacementScheduleId,
        long replacementScheduleRevision,
        DateTimeZone? timeZone,
        ReminderCalculationLimits? limits = null) =>
        Rebuild(
            previousRule,
            nextRule,
            pendingSchedules,
            new ReminderScheduleRequest(replacementScheduleId, replacementScheduleRevision),
            timeZone,
            limits);

    /// <summary>Creates terminal CANCELLED transitions for one occurrence only.</summary>
    public static IReadOnlyList<ReminderScheduleTerminalTransition> CancelPending(
        Guid occurrenceId,
        IReadOnlyList<PendingScheduleSnapshot> pendingSchedules,
        ReminderScheduleTerminalReason reason)
    {
        ReminderCalculationValidation.RequireUuidV7(occurrenceId, nameof(occurrenceId));
        ReminderCalculationValidation.RequireTerminalReason(reason);
        if (reason is not (ReminderScheduleTerminalReason.RULE_DISABLED or
            ReminderScheduleTerminalReason.TASK_RESULT_RECORDED or
            ReminderScheduleTerminalReason.TASK_DELETED or
            ReminderScheduleTerminalReason.RECOVERY_OBSOLETE))
        {
            ReminderCalculationValidation.Fail(
                "reminder.cancel.reason_invalid",
                "The supplied reason is not a cancellation reason.",
                nameof(reason));
        }

        ArgumentNullException.ThrowIfNull(pendingSchedules, nameof(pendingSchedules));
        var ordered = ValidateAndOrderPending(pendingSchedules, expectedOccurrenceId: occurrenceId);

        return ordered
            .Select(snapshot => new ReminderScheduleTerminalTransition(
                snapshot.ScheduleId,
                ReminderScheduleState.CANCELLED,
                reason,
                null))
            .ToArray();
    }

    /// <summary>Derives a future SNOOZE schedule from a consumed origin.</summary>
    public static ReminderScheduleDerivationResult DeriveSnooze(
        ReminderScheduleOrigin origin,
        Guid newScheduleId,
        long newScheduleRevision,
        Instant triggerAtUtc)
    {
        ReminderCalculationValidation.RequireOrigin(origin);
        var request = new ReminderScheduleRequest(newScheduleId, newScheduleRevision);
        ReminderCalculationValidation.RequireScheduleRequest(request);
        RequireDerivedScheduleRevision(origin, request);

        if (triggerAtUtc <= origin.TriggerAtUtc)
        {
            ReminderCalculationValidation.Fail(
                "reminder.snooze.trigger_not_after_origin",
                "A snooze trigger must be later than the origin trigger.",
                nameof(triggerAtUtc));
        }

        return ReminderScheduleDerivationResult.Scheduled(new ReminderSchedulePlan(
            request.ScheduleId,
            origin.RuleId,
            origin.TargetId,
            origin.OccurrenceId,
            origin.LogicalReminderId,
            origin.Purpose,
            origin.Priority,
            origin.Pinned,
            ReminderScheduleCause.SNOOZE,
            origin.ScheduleId,
            origin.RuleRevision,
            request.ScheduleRevision,
            triggerAtUtc,
            origin.TimeZoneId,
            ReminderScheduleState.PENDING));
    }

    /// <summary>
    /// Derives one repeat occurrence. Null maxCount is capped by the injected
    /// product limit; no recurrence model or future occurrence is invented.
    /// </summary>
    public static ReminderScheduleDerivationResult DeriveRepeat(
        ReminderScheduleOrigin origin,
        Guid newScheduleId,
        long newScheduleRevision,
        int nextAttemptOrdinal,
        ReminderRepeatPolicy policy,
        ReminderCalculationLimits? limits = null)
    {
        ReminderCalculationValidation.RequireOrigin(origin);
        ArgumentNullException.ThrowIfNull(policy, nameof(policy));
        limits ??= ReminderCalculationLimits.Default;

        if (!policy.Enabled)
        {
            return ReminderScheduleDerivationResult.NotScheduled(ReminderCalculationCodes.RepeatDisabled);
        }

        var request = new ReminderScheduleRequest(newScheduleId, newScheduleRevision);
        ReminderCalculationValidation.RequireScheduleRequest(request);
        RequireDerivedScheduleRevision(origin, request);

        if (nextAttemptOrdinal < 2)
        {
            ReminderCalculationValidation.Fail(
                "reminder.repeat.ordinal.invalid",
                "A repeat attempt ordinal starts at two because the first trigger is ordinal one.",
                nameof(nextAttemptOrdinal));
        }

        var intervalSeconds = policy.IntervalSeconds.GetValueOrDefault();
        if (policy.IntervalSeconds is null or <= 0)
        {
            ReminderCalculationValidation.Fail(
                "reminder.repeat.interval.invalid",
                "An enabled repeat policy requires a positive interval.",
                nameof(policy.IntervalSeconds));
        }

        if (intervalSeconds > limits.MaxRepeatIntervalSeconds)
        {
            ReminderCalculationValidation.Fail(
                "reminder.repeat.interval.out_of_range",
                "Repeat interval is outside the configured calculator bounds.",
                nameof(policy.IntervalSeconds));
        }

        var effectiveMaxCount = policy.MaxCount ?? limits.MaxRepeatCount;
        if (effectiveMaxCount <= 0 || effectiveMaxCount > limits.MaxRepeatCount)
        {
            ReminderCalculationValidation.Fail(
                "reminder.repeat.max_count.out_of_range",
                "Repeat maxCount is outside the configured calculator bounds.",
                nameof(policy.MaxCount));
        }

        if (nextAttemptOrdinal > effectiveMaxCount)
        {
            return ReminderScheduleDerivationResult.NotScheduled(ReminderCalculationCodes.RepeatLimitReached);
        }

        Instant triggerAtUtc;
        try
        {
            triggerAtUtc = origin.TriggerAtUtc.Plus(Duration.FromSeconds(intervalSeconds));
        }
        catch (ArgumentOutOfRangeException)
        {
            ReminderCalculationValidation.Fail(
                "reminder.repeat.time.overflow",
                "The repeat trigger is outside Noda Time's representable range.",
                nameof(policy.IntervalSeconds));
            throw;
        }
        catch (OverflowException)
        {
            ReminderCalculationValidation.Fail(
                "reminder.repeat.time.overflow",
                "The repeat trigger is outside Noda Time's representable range.",
                nameof(policy.IntervalSeconds));
            throw;
        }

        return ReminderScheduleDerivationResult.Scheduled(new ReminderSchedulePlan(
            request.ScheduleId,
            origin.RuleId,
            origin.TargetId,
            origin.OccurrenceId,
            origin.LogicalReminderId,
            origin.Purpose,
            origin.Priority,
            origin.Pinned,
            ReminderScheduleCause.REPEAT,
            origin.ScheduleId,
            origin.RuleRevision,
            request.ScheduleRevision,
            triggerAtUtc,
            origin.TimeZoneId,
            ReminderScheduleState.PENDING));
    }

    private static ReminderScheduleDerivationResult DerivePendingSchedule(
        ReminderRuleCalculationInput rule,
        ReminderScheduleRequest schedule,
        ReminderScheduleCause cause,
        Guid? originScheduleId,
        DateTimeZone? timeZone,
        ReminderCalculationLimits? limits)
    {
        var time = ReminderTimeCalculator.Calculate(rule.TaskTimeSpec, rule.Timing, timeZone, limits);
        return ReminderScheduleDerivationResult.Scheduled(new ReminderSchedulePlan(
            schedule.ScheduleId,
            rule.RuleId,
            rule.TargetId,
            rule.OccurrenceId,
            rule.LogicalReminderId,
            rule.Purpose,
            rule.Priority,
            rule.Pinned,
            cause,
            originScheduleId,
            rule.RuleRevision,
            schedule.ScheduleRevision,
            time.TriggerAtUtc,
            time.TimeZoneId,
            ReminderScheduleState.PENDING));
    }

    private static bool HasSameRuleSemantics(
        ReminderRuleCalculationInput previousRule,
        ReminderRuleCalculationInput nextRule) =>
        previousRule.TargetKind == nextRule.TargetKind &&
        previousRule.TargetId == nextRule.TargetId &&
        previousRule.OccurrenceId == nextRule.OccurrenceId &&
        previousRule.Purpose == nextRule.Purpose &&
        Equals(previousRule.Timing, nextRule.Timing) &&
        previousRule.Priority == nextRule.Priority &&
        previousRule.Pinned == nextRule.Pinned &&
        previousRule.Enabled == nextRule.Enabled &&
        Equals(previousRule.TaskTimeSpec, nextRule.TaskTimeSpec);

    private static void ValidateRuleOffset(
        ReminderRuleCalculationInput rule,
        ReminderCalculationLimits limits)
    {
        if (rule.Timing is not ReminderTiming.Relative relative)
        {
            return;
        }

        if (relative.OffsetSeconds < limits.MinRelativeOffsetSeconds ||
            relative.OffsetSeconds > limits.MaxRelativeOffsetSeconds)
        {
            ReminderCalculationValidation.Fail(
                "reminder.offset.out_of_range",
                "Relative offset is outside the configured calculator bounds.",
                nameof(relative.OffsetSeconds));
        }
    }

    private static void RequireNextRuleRevision(
        ReminderRuleCalculationInput previousRule,
        ReminderRuleCalculationInput nextRule)
    {
        if (previousRule.RuleRevision == long.MaxValue ||
            nextRule.RuleRevision != previousRule.RuleRevision + 1)
        {
            ReminderCalculationValidation.Fail(
                "reminder.rule_revision.sequence",
                "A semantic rule change must increment RuleRevision exactly once.",
                nameof(nextRule.RuleRevision));
        }
    }

    private static void EnsureReplacementRevisionIsFresh(
        PendingScheduleSnapshot[] pendingSchedules,
        ReminderScheduleRequest replacementSchedule)
    {
        if (pendingSchedules.Length == 0)
        {
            // Consumed/terminal schedule revisions are outside this pure
            // input. The Agent must supply the durable next revision.
            return;
        }

        var maxPendingRevision = pendingSchedules.Max(snapshot => snapshot.ScheduleRevision);
        if (replacementSchedule.ScheduleRevision <= maxPendingRevision)
        {
            ReminderCalculationValidation.Fail(
                "reminder.schedule_revision.not_fresh",
                "Replacement schedule revision must be greater than every pending revision.",
                nameof(replacementSchedule.ScheduleRevision));
        }
    }

    private static void EnsureReplacementIdIsFresh(
        IReadOnlyList<PendingScheduleSnapshot> pendingSchedules,
        Guid replacementScheduleId)
    {
        if (pendingSchedules.Any(snapshot => snapshot.ScheduleId == replacementScheduleId))
        {
            ReminderCalculationValidation.Fail(
                "reminder.schedule_id.reused",
                "Replacement schedule id must not reuse a pending schedule id.",
                nameof(replacementScheduleId));
        }
    }

    private static void RequireDerivedScheduleRevision(
        ReminderScheduleOrigin origin,
        ReminderScheduleRequest request)
    {
        if (request.ScheduleRevision <= origin.ScheduleRevision)
        {
            ReminderCalculationValidation.Fail(
                "reminder.schedule_revision.not_fresh",
                "A derived schedule revision must be greater than its origin revision.",
                nameof(request.ScheduleRevision));
        }
    }

    private static ReminderScheduleTerminalReason DetermineRebuildReason(
        ReminderRuleCalculationInput previousRule,
        ReminderRuleCalculationInput nextRule,
        IReadOnlyList<PendingScheduleSnapshot> pendingSchedules,
        ReminderSchedulePlan replacement)
    {
        var previousTimeZoneIds = pendingSchedules
            .Select(snapshot => snapshot.TimeZoneId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (replacement.TimeZoneId is not null &&
            previousTimeZoneIds.Any(previousId => !string.Equals(previousId, replacement.TimeZoneId, StringComparison.Ordinal)))
        {
            return ReminderScheduleTerminalReason.TIME_ZONE_CHANGED;
        }

        if (!Equals(previousRule.TaskTimeSpec, nextRule.TaskTimeSpec))
        {
            return ReminderScheduleTerminalReason.TASK_TIME_CHANGED;
        }

        return ReminderScheduleTerminalReason.RULE_REVISED;
    }

    private static bool HasPendingTimeZoneChange(
        PendingScheduleSnapshot[] pendingSchedules,
        ReminderRuleCalculationInput nextRule,
        DateTimeZone? timeZone)
    {
        if (pendingSchedules.Length == 0 || nextRule.Timing is not ReminderTiming.Relative || timeZone is null)
        {
            return false;
        }

        return pendingSchedules.Any(snapshot =>
            snapshot.TimeZoneId is not null &&
            !string.Equals(snapshot.TimeZoneId, timeZone.Id, StringComparison.Ordinal));
    }

    private static PendingScheduleSnapshot[] ValidateAndOrderPending(
        IReadOnlyList<PendingScheduleSnapshot> pendingSchedules,
        Guid? expectedRuleId = null,
        Guid? expectedOccurrenceId = null)
    {
        var seenScheduleIds = new HashSet<Guid>();
        foreach (var snapshot in pendingSchedules)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ReminderCalculationValidation.RequireUuidV7(snapshot.ScheduleId, nameof(snapshot.ScheduleId));
            ReminderCalculationValidation.RequireUuidV7(snapshot.RuleId, nameof(snapshot.RuleId));
            ReminderCalculationValidation.RequireUuidV7(snapshot.TargetId, nameof(snapshot.TargetId));
            ReminderCalculationValidation.RequireUuidV7(snapshot.OccurrenceId, nameof(snapshot.OccurrenceId));
            ReminderCalculationValidation.RequireUuidV7(snapshot.LogicalReminderId, nameof(snapshot.LogicalReminderId));

            if (!seenScheduleIds.Add(snapshot.ScheduleId))
            {
                ReminderCalculationValidation.Fail(
                    "reminder.schedule_id.duplicate",
                    "Pending schedule ids must be unique.",
                    nameof(snapshot.ScheduleId));
            }

            if (!Enum.IsDefined(snapshot.Purpose))
            {
                ReminderCalculationValidation.Fail("reminder.purpose.invalid", "Reminder purpose is not supported.", nameof(snapshot.Purpose));
            }

            if (!Enum.IsDefined(snapshot.Priority))
            {
                ReminderCalculationValidation.Fail("reminder.priority.invalid", "Reminder priority is not supported.", nameof(snapshot.Priority));
            }

            if (snapshot.RuleRevision <= 0 || snapshot.ScheduleRevision <= 0)
            {
                ReminderCalculationValidation.Fail(
                    "reminder.schedule_revision.invalid",
                    "Pending schedule revisions must be positive.",
                    nameof(snapshot.ScheduleRevision));
            }

            if (expectedRuleId is not null && snapshot.RuleId != expectedRuleId.Value)
            {
                ReminderCalculationValidation.Fail(
                    "reminder.rule.scope_mismatch",
                    "Pending schedule belongs to another ReminderRule.",
                    nameof(snapshot.RuleId));
            }

            if (expectedOccurrenceId is not null && snapshot.OccurrenceId != expectedOccurrenceId.Value)
            {
                ReminderCalculationValidation.Fail(
                    "reminder.occurrence.scope_mismatch",
                    "Pending schedule belongs to another occurrence.",
                    nameof(snapshot.OccurrenceId));
            }
        }

        return pendingSchedules
            .OrderBy(snapshot => snapshot.ScheduleRevision)
            .ThenBy(snapshot => snapshot.ScheduleId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
    }
}
