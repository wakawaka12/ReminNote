using ReminNote.Core;

namespace ReminNote.Core.Reminders.Domain;

/// <summary>
/// Repeat configuration for one logical reminder chain. maxCount includes
/// the first core trigger; null is intentionally left for a later profile
/// safety policy and does not authorize unbounded row creation.
/// </summary>
public sealed record RepeatPolicy
{
    public RepeatPolicy(bool enabled, long? intervalSeconds, int? maxCount)
    {
        if (intervalSeconds is <= 0)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.repeat.interval.invalid",
                "Repeat interval must be positive when supplied.",
                nameof(intervalSeconds)));
        }

        if (enabled && intervalSeconds is null)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.repeat.interval.required",
                "An enabled repeat policy must specify an interval.",
                nameof(intervalSeconds)));
        }

        if (maxCount is <= 0)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.repeat.max_count.invalid",
                "Repeat max count must be positive when supplied.",
                nameof(maxCount)));
        }

        Enabled = enabled;
        IntervalSeconds = intervalSeconds;
        MaxCount = maxCount;
    }

    public bool Enabled { get; }

    public long? IntervalSeconds { get; }

    public int? MaxCount { get; }

    public static RepeatPolicy Disabled => new(false, null, null);
}
