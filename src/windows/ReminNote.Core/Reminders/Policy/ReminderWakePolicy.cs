using NodaTime;
using ReminNote.Core;
using ReminNote.Core.Reminders.Domain;

namespace ReminNote.Core.Reminders.Policy;

#pragma warning disable CA1707 // Frozen uppercase values are wire/storage tokens.

public enum WakeCapabilityOutcome
{
    NOT_REQUESTED,
    REQUEST,
    DENIED,
    UNAVAILABLE
}

#pragma warning restore CA1707

/// <summary>OS/device facts supplied by an adapter; no OS calls occur here.</summary>
public sealed record WakeCapability(bool OsSupportsWake, bool Healthy);

/// <summary>Profile and P2.75 gate state supplied by the Agent.</summary>
public sealed record WakeProfile(bool DefaultAllowsWake, bool SafeMode);

/// <summary>Pure wake decision; failures never mutate reminder truth.</summary>
public sealed record WakeDecision(
    WakeCapabilityOutcome Outcome,
    string ReasonCode,
    bool ReminderTruthUnchanged)
{
    public bool MayRequestWake => Outcome == WakeCapabilityOutcome.REQUEST;
}

/// <summary>Pending schedule candidate used to calculate nearest wakeup.</summary>
public sealed record PendingWakeCandidate(
    Guid ScheduleId,
    Instant TriggerAtUtc,
    WakeCapabilityOutcome WakeOutcome);

public static class ReminderWakePolicy
{
    public static WakeDecision Resolve(
        WakePolicy requested,
        WakeProfile profile,
        WakeCapability capability)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capability);
        if (!Enum.IsDefined(requested))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.wake.policy.invalid",
                "Wake policy is not supported.",
                nameof(requested)));
        }

        if (requested == WakePolicy.NO)
        {
            return new WakeDecision(
                WakeCapabilityOutcome.NOT_REQUESTED,
                ReminderPolicyCodes.WakeNotRequested,
                ReminderTruthUnchanged: true);
        }

        if (profile.SafeMode)
        {
            return new WakeDecision(
                WakeCapabilityOutcome.DENIED,
                ReminderPolicyCodes.WakeDeniedSafeMode,
                ReminderTruthUnchanged: true);
        }

        var profileAllows = requested == WakePolicy.YES || profile.DefaultAllowsWake;
        if (!profileAllows)
        {
            return new WakeDecision(
                WakeCapabilityOutcome.DENIED,
                ReminderPolicyCodes.WakeDeniedByProfile,
                ReminderTruthUnchanged: true);
        }

        if (!capability.OsSupportsWake || !capability.Healthy)
        {
            return new WakeDecision(
                WakeCapabilityOutcome.UNAVAILABLE,
                ReminderPolicyCodes.WakeUnavailable,
                ReminderTruthUnchanged: true);
        }

        return new WakeDecision(
            WakeCapabilityOutcome.REQUEST,
            ReminderPolicyCodes.WakeRequested,
            ReminderTruthUnchanged: true);
    }

    /// <summary>
    /// Returns the earliest requested wakeup. A due candidate returns the
    /// supplied current instant so the Agent can perform immediate recovery.
    /// </summary>
    public static Instant? FindNearestWakeup(
        Instant nowUtc,
        IEnumerable<PendingWakeCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var eligible = candidates
            .Where(candidate => candidate.WakeOutcome == WakeCapabilityOutcome.REQUEST)
            .Select(candidate => candidate.TriggerAtUtc <= nowUtc ? nowUtc : candidate.TriggerAtUtc)
            .OrderBy(instant => instant)
            .ToArray();

        return eligible.Length == 0 ? null : eligible[0];
    }
}
