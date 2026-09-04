using NodaTime;

#pragma warning disable CA1707 // Frozen uppercase state names are part of the P3 contract.
namespace ReminNote.Core.Reminders.Notifications;

/// <summary>
/// Bounded retry policy for channel effects. It classifies only stable
/// outcomes/codes; adapter-specific text never becomes a persisted decision.
/// </summary>
public sealed record NotificationRetryPolicy
{
    public NotificationRetryPolicy(
        int maxAttempts = 3,
        TimeSpan? initialBackoff = null,
        TimeSpan? maximumBackoff = null)
    {
        if (maxAttempts < 1 || maxAttempts > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        var initial = initialBackoff ?? TimeSpan.FromSeconds(5);
        var maximum = maximumBackoff ?? TimeSpan.FromMinutes(5);
        if (initial <= TimeSpan.Zero || maximum < initial)
        {
            throw new ArgumentOutOfRangeException(nameof(initialBackoff));
        }

        MaxAttempts = maxAttempts;
        InitialBackoff = initial;
        MaximumBackoff = maximum;
    }

    public int MaxAttempts { get; }

    public TimeSpan InitialBackoff { get; }

    public TimeSpan MaximumBackoff { get; }

    public static bool IsRetryable(NotificationDeliveryOutcome outcome, string? errorCode)
    {
        return outcome switch
        {
            NotificationDeliveryOutcome.DELIVERED => false,
            NotificationDeliveryOutcome.BLOCKED => false,
            NotificationDeliveryOutcome.NOT_ATTEMPTED => IsTransientNotAttempted(errorCode),
            NotificationDeliveryOutcome.UNAVAILABLE => true,
            NotificationDeliveryOutcome.FAILED => !IsPermanentFailure(errorCode),
            NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS => true,
            _ => false,
        };
    }

    public bool CanScheduleNext(int attemptNumber, NotificationDeliveryOutcome outcome, string? errorCode) =>
        attemptNumber < MaxAttempts && IsRetryable(outcome, errorCode);

    public Instant CalculateNextAttempt(
        Instant recordedAtUtc,
        int attemptNumber,
        NotificationDeliveryOutcome outcome,
        string? errorCode)
    {
        if (!CanScheduleNext(attemptNumber, outcome, errorCode))
        {
            throw new InvalidOperationException("The delivery outcome is not eligible for another attempt.");
        }

        var exponent = Math.Clamp(attemptNumber - 1, 0, 30);
        var multiplier = Math.Pow(2, exponent);
        var candidateTicks = InitialBackoff.Ticks * multiplier;
        var maximumTicks = MaximumBackoff.Ticks;
        var boundedTicks = Math.Min(candidateTicks, maximumTicks);
        var delay = TimeSpan.FromTicks(
            checked((long)Math.Max(1, Math.Round(boundedTicks))));
        return recordedAtUtc + Duration.FromTimeSpan(delay);
    }

    private static bool IsTransientNotAttempted(string? errorCode) =>
        string.Equals(errorCode, NotificationErrorCodes.ChannelUnavailable, StringComparison.Ordinal);

    private static bool IsPermanentFailure(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return false;
        }

        return errorCode.StartsWith("notification.channel.", StringComparison.Ordinal) ||
            errorCode.StartsWith("notification.serialization.", StringComparison.Ordinal) ||
            errorCode.StartsWith("notification.idempotency.", StringComparison.Ordinal) ||
            errorCode.StartsWith("notification.lifecycle.", StringComparison.Ordinal) ||
            errorCode is NotificationErrorCodes.CapabilityMissing or
                NotificationErrorCodes.ChannelBlocked or
                NotificationErrorCodes.ChannelMissing or
                NotificationErrorCodes.PolicySuppressedQuietHours;
    }
}

#pragma warning restore CA1707
