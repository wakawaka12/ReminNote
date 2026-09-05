using NodaTime;
using ReminNote.Core;
using ReminNote.Core.Reminders.Domain;

namespace ReminNote.Core.Reminders.Policy;

#pragma warning disable CA1707 // Frozen uppercase values are wire/storage tokens.

/// <summary>How a temporary override affects the quiet-hours windows.</summary>
public enum QuietHoursOverrideMode
{
    BYPASS,
    FORCE_QUIET
}

/// <summary>Availability of the channel to which a presentation is targeted.</summary>
public enum ReminderChannelAvailability
{
    AVAILABLE,
    BLOCKED,
    UNAVAILABLE
}

/// <summary>Presentation result; it never changes schedule or instance truth.</summary>
public enum ReminderPresentationDisposition
{
    NOT_TRIGGERED,
    DELIVER,
    SUMMARY,
    SUPPRESSED_QUIET_HOURS,
    BLOCKED,
    UNAVAILABLE
}

#pragma warning restore CA1707

/// <summary>
/// A half-open local-time window. When Start is later than End the window
/// crosses midnight. Start == End is rejected to avoid an implicit and
/// surprising all-day interpretation.
/// </summary>
public sealed record QuietHoursWindow
{
    public QuietHoursWindow(LocalTime start, LocalTime end)
    {
        if (start == end)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.quiet_hours.window.empty",
                "A quiet-hours window must have distinct start and end times.",
                nameof(end)));
        }

        Start = start;
        End = end;
    }

    public LocalTime Start { get; }

    public LocalTime End { get; }

    public bool CrossesMidnight => Start > End;

    /// <summary>Tests the half-open interval [Start, End).</summary>
    public bool Contains(LocalTime time) =>
        CrossesMidnight
            ? time >= Start || time < End
            : time >= Start && time < End;
}

/// <summary>
/// A UTC-bounded temporary override. Overrides are evaluated by start order;
/// when several overlap, the most recently starting active override wins.
/// </summary>
public sealed record QuietHoursOverride
{
    public QuietHoursOverride(
        Instant startsAtUtc,
        Instant endsAtUtc,
        QuietHoursOverrideMode mode)
    {
        if (endsAtUtc <= startsAtUtc)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.quiet_hours.override.range_invalid",
                "A quiet-hours override must have a positive duration.",
                nameof(endsAtUtc)));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.quiet_hours.override.mode_invalid",
                "The quiet-hours override mode is not supported.",
                nameof(mode)));
        }

        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        Mode = mode;
    }

    public Instant StartsAtUtc { get; }

    public Instant EndsAtUtc { get; }

    public QuietHoursOverrideMode Mode { get; }

    public bool Contains(Instant instant) => instant >= StartsAtUtc && instant < EndsAtUtc;
}

/// <summary>
/// Immutable profile policy for local quiet-hours evaluation. It is pure:
/// it never changes an Instant, Rule, Schedule, or ReminderInstance.
/// </summary>
public sealed class QuietHoursPolicy
{
    private readonly QuietHoursWindow[] windows;
    private readonly QuietHoursOverride[] overrides;

    public QuietHoursPolicy(
        IEnumerable<QuietHoursWindow>? windows = null,
        IEnumerable<QuietHoursOverride>? overrides = null)
    {
        this.windows = (windows ?? Array.Empty<QuietHoursWindow>()).ToArray();
        this.overrides = (overrides ?? Array.Empty<QuietHoursOverride>())
            .OrderBy(item => item.StartsAtUtc)
            .ThenBy(item => item.EndsAtUtc)
            .ToArray();
    }

    public IReadOnlyList<QuietHoursWindow> Windows => windows;

    public IReadOnlyList<QuietHoursOverride> Overrides => overrides;

    public bool IsQuiet(Instant instant, DateTimeZone timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var activeOverride = overrides
            .Where(item => item.Contains(instant))
            .LastOrDefault();

        if (activeOverride is not null)
        {
            return activeOverride.Mode == QuietHoursOverrideMode.FORCE_QUIET;
        }

        var localTime = instant.InZone(timeZone).TimeOfDay;
        return windows.Any(window => window.Contains(localTime));
    }

    /// <summary>
    /// Returns the first instant after <paramref name="instant"/> at which
    /// the currently active quiet window ends. This is a presentation-layer
    /// scheduling hint only; it never moves a core ReminderSchedule. A
    /// bypass override or a non-quiet instant returns <c>null</c>.
    /// </summary>
    public Instant? GetActiveQuietHoursEnd(Instant instant, DateTimeZone timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var activeOverride = overrides
            .Where(item => item.Contains(instant))
            .LastOrDefault();
        if (activeOverride is not null)
        {
            return activeOverride.Mode == QuietHoursOverrideMode.FORCE_QUIET
                ? activeOverride.EndsAtUtc
                : null;
        }

        var zoned = instant.InZone(timeZone);
        var endCandidates = new List<Instant>();
        foreach (var window in windows)
        {
            if (!window.Contains(zoned.TimeOfDay))
            {
                continue;
            }

            var endDate = window.CrossesMidnight && zoned.TimeOfDay >= window.Start
                ? zoned.Date.PlusDays(1)
                : zoned.Date;
            var endLocal = endDate.At(window.End);
            var endInstant = timeZone.AtLeniently(endLocal).ToInstant();
            if (endInstant > instant)
            {
                endCandidates.Add(endInstant);
            }
        }

        return endCandidates.Count == 0 ? null : endCandidates.Min();
    }

    public ReminderPresentationDecision Evaluate(ReminderPresentationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.CoreTriggered)
        {
            return ReminderPresentationDecision.NotTriggered;
        }

        if (input.ChannelAvailability == ReminderChannelAvailability.BLOCKED)
        {
            return new ReminderPresentationDecision(
                ReminderPresentationDisposition.BLOCKED,
                ReminderPolicyCodes.ChannelBlocked,
                IsQuietHours: false,
                IsEscalated: false,
                SummaryEligible: false);
        }

        if (input.ChannelAvailability == ReminderChannelAvailability.UNAVAILABLE)
        {
            return new ReminderPresentationDecision(
                ReminderPresentationDisposition.UNAVAILABLE,
                ReminderPolicyCodes.ChannelUnavailable,
                IsQuietHours: false,
                IsEscalated: false,
                SummaryEligible: false);
        }

        var quiet = IsQuiet(input.AtUtc, input.TimeZone);
        if (!quiet)
        {
            return new ReminderPresentationDecision(
                ReminderPresentationDisposition.DELIVER,
                ReminderPolicyCodes.Delivered,
                IsQuietHours: false,
                IsEscalated: false,
                SummaryEligible: false);
        }

        var highPriority = input.Priority == ReminderPriority.HIGH;
        var escalated =
            (input.Pinned && highPriority && input.AllowPinnedHighPriorityDuringQuietHours) ||
            (highPriority && input.AllowHighPriorityDuringQuietHours);

        if (escalated)
        {
            return new ReminderPresentationDecision(
                ReminderPresentationDisposition.DELIVER,
                ReminderPolicyCodes.QuietHoursEscalated,
                IsQuietHours: true,
                IsEscalated: true,
                SummaryEligible: false);
        }

        return input.SummaryAllowed
            ? new ReminderPresentationDecision(
                ReminderPresentationDisposition.SUMMARY,
                ReminderPolicyCodes.QuietHoursSummary,
                IsQuietHours: true,
                IsEscalated: false,
                SummaryEligible: true)
            : new ReminderPresentationDecision(
                ReminderPresentationDisposition.SUPPRESSED_QUIET_HOURS,
                ReminderPolicyCodes.QuietHoursSuppressed,
                IsQuietHours: true,
                IsEscalated: false,
                SummaryEligible: false);
    }
}

/// <summary>Input to the presentation policy; core trigger is explicit.</summary>
public sealed record ReminderPresentationInput
{
    public ReminderPresentationInput(
        Instant atUtc,
        DateTimeZone timeZone,
        ReminderPriority priority,
        bool pinned,
        bool coreTriggered,
        ReminderChannelAvailability channelAvailability,
        bool summaryAllowed = true,
        bool allowHighPriorityDuringQuietHours = true,
        bool allowPinnedHighPriorityDuringQuietHours = true)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        if (!Enum.IsDefined(priority))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.priority.invalid",
                "Reminder priority is not supported.",
                nameof(priority)));
        }

        if (!Enum.IsDefined(channelAvailability))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.channel.availability.invalid",
                "Reminder channel availability is not supported.",
                nameof(channelAvailability)));
        }

        AtUtc = atUtc;
        TimeZone = timeZone;
        Priority = priority;
        Pinned = pinned;
        CoreTriggered = coreTriggered;
        ChannelAvailability = channelAvailability;
        SummaryAllowed = summaryAllowed;
        AllowHighPriorityDuringQuietHours = allowHighPriorityDuringQuietHours;
        AllowPinnedHighPriorityDuringQuietHours = allowPinnedHighPriorityDuringQuietHours;
    }

    public Instant AtUtc { get; }

    public DateTimeZone TimeZone { get; }

    public ReminderPriority Priority { get; }

    public bool Pinned { get; }

    public bool CoreTriggered { get; }

    public ReminderChannelAvailability ChannelAvailability { get; }

    public bool SummaryAllowed { get; }

    public bool AllowHighPriorityDuringQuietHours { get; }

    public bool AllowPinnedHighPriorityDuringQuietHours { get; }
}

/// <summary>Pure result that downstream channel adapters can translate.</summary>
public sealed record ReminderPresentationDecision(
    ReminderPresentationDisposition Disposition,
    string ReasonCode,
    bool IsQuietHours,
    bool IsEscalated,
    bool SummaryEligible)
{
    public static ReminderPresentationDecision NotTriggered { get; } = new(
        ReminderPresentationDisposition.NOT_TRIGGERED,
        ReminderPolicyCodes.CoreNotTriggered,
        IsQuietHours: false,
        IsEscalated: false,
        SummaryEligible: false);
}
