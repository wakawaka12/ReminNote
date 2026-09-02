using NodaTime;
using ReminNote.Core.Reminders.Domain;

namespace ReminNote.Infrastructure.Persistence.Reminders;

public sealed class ReminderDeliveryAttemptEntity
{
    public Guid AttemptId { get; set; }

    public Guid InstanceId { get; set; }

    public ReminderDeliveryChannel Channel { get; set; }

    public Instant AttemptedAtUtc { get; set; }

    public ReminderDeliveryOutcome Outcome { get; set; }

    public string? ErrorCode { get; set; }

    public static ReminderDeliveryAttemptEntity FromDomain(ReminderDeliveryAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        return new ReminderDeliveryAttemptEntity
        {
            AttemptId = attempt.AttemptId.Value,
            InstanceId = attempt.InstanceId.Value,
            Channel = attempt.Channel,
            AttemptedAtUtc = attempt.AttemptedAtUtc,
            Outcome = attempt.Outcome,
            ErrorCode = attempt.ErrorCode
        };
    }

    public ReminderDeliveryAttempt ToDomain() => ReminderDeliveryAttempt.Rehydrate(
        ReminderDeliveryAttemptId.From(AttemptId),
        ReminderInstanceId.From(InstanceId),
        Channel,
        AttemptedAtUtc,
        Outcome,
        ErrorCode);
}
