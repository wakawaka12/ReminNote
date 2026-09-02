using NodaTime;

#pragma warning disable CA1707 // Frozen wire names use uppercase underscore codes.
namespace ReminNote.Core.Reminders.Notifications;

/// <summary>
/// Read-only input to presentation policy. Quiet Hours and other product
/// policy rules live behind this seam; this contract does not duplicate them.
/// </summary>
public sealed record NotificationPresentationContext
{
    public NotificationPresentationContext(
        NotificationTriggerFact coreTrigger,
        NotificationChannelStatus channelStatus,
        Instant evaluatedAtUtc)
    {
        CoreTrigger = coreTrigger ?? throw new ArgumentNullException(nameof(coreTrigger));
        ChannelStatus = channelStatus ?? throw new ArgumentNullException(nameof(channelStatus));
        if (CoreTrigger is null)
        {
            throw new ArgumentNullException(nameof(coreTrigger));
        }

        if (ChannelStatus is null)
        {
            throw new ArgumentNullException(nameof(channelStatus));
        }

        EvaluatedAtUtc = evaluatedAtUtc;
    }

    public NotificationTriggerFact CoreTrigger { get; }

    public NotificationChannelStatus ChannelStatus { get; }

    public Instant EvaluatedAtUtc { get; }
}

public enum NotificationPresentationDecisionKind
{
    PRESENT,
    SUPPRESSED_QUIET_HOURS,
}

/// <summary>
/// Result of a presentation policy evaluation. A policy may suppress
/// presentation, but it cannot erase or defer the core trigger fact.
/// </summary>
public sealed record NotificationPresentationDecision
{
    public NotificationPresentationDecision(
        NotificationPresentationDecisionKind kind,
        string? reasonCode = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "Presentation decision is not supported.",
                nameof(kind));
        }

        NotificationValidation.ValidateOptionalStableCode(reasonCode, nameof(reasonCode));
        if (kind == NotificationPresentationDecisionKind.SUPPRESSED_QUIET_HOURS &&
            reasonCode is null)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.PolicySuppressedQuietHours,
                "A suppressed presentation must include a stable reason code.",
                nameof(reasonCode));
        }

        if (kind == NotificationPresentationDecisionKind.PRESENT && reasonCode is not null)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "A present decision cannot carry a suppression reason.",
                nameof(reasonCode));
        }

        Kind = kind;
        ReasonCode = reasonCode;
    }

    public NotificationPresentationDecisionKind Kind { get; }

    public string? ReasonCode { get; }

    public static NotificationPresentationDecision Present { get; } =
        new(NotificationPresentationDecisionKind.PRESENT);

    public static NotificationPresentationDecision SuppressedQuietHours(string reasonCode) =>
        new(NotificationPresentationDecisionKind.SUPPRESSED_QUIET_HOURS, reasonCode);
}

/// <summary>
/// Adapter seam for Quiet Hours, focus mode, or other presentation policy.
/// Implementations must not mutate Rule/Schedule/Instance facts.
/// </summary>
public interface INotificationPresentationPolicy
{
    NotificationPresentationDecision Evaluate(NotificationPresentationContext context);
}

/// <summary>
/// Test/development default that makes no policy decision beyond allowing
/// presentation. It intentionally contains no Quiet Hours rules.
/// </summary>
public sealed class AllowAllNotificationPresentationPolicy : INotificationPresentationPolicy
{
    public NotificationPresentationDecision Evaluate(NotificationPresentationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return NotificationPresentationDecision.Present;
    }
}

#pragma warning restore CA1707
