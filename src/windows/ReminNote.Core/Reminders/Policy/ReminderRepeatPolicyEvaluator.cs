using ReminNote.Core;
using ReminNote.Core.Reminders.Domain;

namespace ReminNote.Core.Reminders.Policy;

/// <summary>
/// Profile safety bounds for a logical repeat chain. MaxCount includes the
/// first core trigger, and therefore a next ordinal equal to MaxCount is
/// still allowed.
/// </summary>
public sealed record ReminderRepeatSafetyLimits
{
    public ReminderRepeatSafetyLimits(int maxCount = 64, long maxIntervalSeconds = 10L * 365L * 86_400L)
    {
        if (maxCount <= 0)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.repeat.profile_max_count.invalid",
                "The repeat profile maximum must be positive.",
                nameof(maxCount)));
        }

        if (maxIntervalSeconds <= 0)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.repeat.profile_interval.invalid",
                "The repeat profile interval maximum must be positive.",
                nameof(maxIntervalSeconds)));
        }

        MaxCount = maxCount;
        MaxIntervalSeconds = maxIntervalSeconds;
    }

    public int MaxCount { get; }

    public long MaxIntervalSeconds { get; }

    public static ReminderRepeatSafetyLimits Default { get; } = new();
}

/// <summary>Result of validating whether one derived repeat may be created.</summary>
public sealed record ReminderRepeatDecision(
    bool Allowed,
    int EffectiveMaxCount,
    int NextAttemptOrdinal,
    long? IntervalSeconds,
    string ReasonCode)
{
    public static ReminderRepeatDecision Denied(
        int maxCount,
        int nextAttemptOrdinal,
        long? intervalSeconds,
        string reasonCode) =>
        new(false, maxCount, nextAttemptOrdinal, intervalSeconds, reasonCode);

    public static ReminderRepeatDecision AllowedDecision(
        int maxCount,
        int nextAttemptOrdinal,
        long intervalSeconds) =>
        new(true, maxCount, nextAttemptOrdinal, intervalSeconds, ReminderPolicyCodes.RepeatAllowed);
}

/// <summary>Pure repeat safety evaluator; it never creates a Schedule row.</summary>
public static class ReminderRepeatPolicyEvaluator
{
    public static ReminderRepeatDecision Evaluate(
        RepeatPolicy policy,
        int nextAttemptOrdinal,
        ReminderRepeatSafetyLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        limits ??= ReminderRepeatSafetyLimits.Default;

        if (nextAttemptOrdinal < 2)
        {
            return ReminderRepeatDecision.Denied(
                limits.MaxCount,
                nextAttemptOrdinal,
                policy.IntervalSeconds,
                ReminderPolicyCodes.RepeatOrdinalInvalid);
        }

        if (!policy.Enabled)
        {
            return ReminderRepeatDecision.Denied(
                limits.MaxCount,
                nextAttemptOrdinal,
                policy.IntervalSeconds,
                ReminderPolicyCodes.RepeatDisabled);
        }

        if (policy.IntervalSeconds is not > 0)
        {
            return ReminderRepeatDecision.Denied(
                limits.MaxCount,
                nextAttemptOrdinal,
                policy.IntervalSeconds,
                ReminderPolicyCodes.RepeatIntervalInvalid);
        }

        if (policy.IntervalSeconds > limits.MaxIntervalSeconds)
        {
            return ReminderRepeatDecision.Denied(
                limits.MaxCount,
                nextAttemptOrdinal,
                policy.IntervalSeconds,
                ReminderPolicyCodes.RepeatIntervalOutOfRange);
        }

        var effectiveMaxCount = policy.MaxCount ?? limits.MaxCount;
        if (effectiveMaxCount <= 0 || effectiveMaxCount > limits.MaxCount)
        {
            return ReminderRepeatDecision.Denied(
                limits.MaxCount,
                nextAttemptOrdinal,
                policy.IntervalSeconds,
                ReminderPolicyCodes.RepeatMaxCountOutOfRange);
        }

        if (nextAttemptOrdinal > effectiveMaxCount)
        {
            return ReminderRepeatDecision.Denied(
                effectiveMaxCount,
                nextAttemptOrdinal,
                policy.IntervalSeconds,
                ReminderPolicyCodes.RepeatLimitReached);
        }

        return ReminderRepeatDecision.AllowedDecision(
            effectiveMaxCount,
            nextAttemptOrdinal,
            policy.IntervalSeconds.Value);
    }
}
