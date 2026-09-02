using NodaTime;
using ReminNote.Core;

namespace ReminNote.Core.Reminders.Domain;

/// <summary>
/// A discriminated timing shape. Relative timing retains a signed integer
/// offset; absolute timing is already resolved to a UTC Instant.
/// </summary>
public abstract record ReminderTiming
{
    protected ReminderTiming(ReminderTimingKind kind)
    {
        Kind = kind;
    }

    public ReminderTimingKind Kind { get; }

    public static RelativeReminderTiming Relative(ReminderAnchor anchor, long offsetSeconds) =>
        new(anchor, offsetSeconds);

    public static AbsoluteReminderTiming AbsoluteUtc(Instant atUtc) =>
        new(atUtc);

    /// <summary>
    /// Reconstructs the two nullable persistence columns without accepting a
    /// mixed relative/absolute state.
    /// </summary>
    public static ReminderTiming FromPersistence(
        ReminderTimingKind kind,
        ReminderAnchor? anchor,
        long? offsetSeconds,
        Instant? absoluteAtUtc)
    {
        return kind switch
        {
            ReminderTimingKind.RELATIVE
                when anchor is not null && offsetSeconds is not null && absoluteAtUtc is null =>
                Relative(anchor.Value, offsetSeconds.Value),
            ReminderTimingKind.ABSOLUTE_UTC
                when anchor is null && offsetSeconds is null && absoluteAtUtc is not null =>
                AbsoluteUtc(absoluteAtUtc.Value),
            ReminderTimingKind.RELATIVE or ReminderTimingKind.ABSOLUTE_UTC =>
                throw MixedState(),
            _ => throw new DomainValidationException(new DomainValidationError(
                "reminder.timing.kind.invalid",
                "Reminder timing kind is not supported.",
                nameof(kind)))
        };
    }

    private static DomainValidationException MixedState() =>
        new(new DomainValidationError(
            "reminder.timing.mixed_columns",
            "Reminder timing columns must describe exactly one timing shape.",
            nameof(ReminderTiming)));
}

public sealed record RelativeReminderTiming : ReminderTiming
{
    public RelativeReminderTiming(ReminderAnchor anchor, long offsetSeconds)
        : base(ReminderTimingKind.RELATIVE)
    {
        if (!Enum.IsDefined(anchor))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.timing.anchor.invalid",
                "Reminder timing anchor is not supported.",
                nameof(anchor)));
        }

        // Exercise Noda Time's representability check at the domain boundary.
        // The value remains an integer number of seconds for persistence and
        // wire purposes; P3-02 will apply its product-specific bound.
        try
        {
            _ = Duration.FromSeconds(offsetSeconds);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.timing.offset_out_of_range",
                "Reminder timing offset cannot be represented by Noda Time.",
                nameof(offsetSeconds)));
        }

        Anchor = anchor;
        OffsetSeconds = offsetSeconds;
    }

    public ReminderAnchor Anchor { get; }

    public long OffsetSeconds { get; }
}

public sealed record AbsoluteReminderTiming : ReminderTiming
{
    public AbsoluteReminderTiming(Instant atUtc)
        : base(ReminderTimingKind.ABSOLUTE_UTC)
    {
        AtUtc = atUtc;
    }

    public Instant AtUtc { get; }
}
