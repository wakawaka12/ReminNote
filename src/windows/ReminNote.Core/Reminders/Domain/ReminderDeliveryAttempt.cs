using System.Text;
using NodaTime;
using ReminNote.Core;

namespace ReminNote.Core.Reminders.Domain;

/// <summary>
/// 通知通道的一次不可变尝试事实。重试必须追加新行，不得覆盖上一次结果。
/// </summary>
public sealed class ReminderDeliveryAttempt
{
    public const int MaxErrorCodeBytes = 160;

    private ReminderDeliveryAttempt(
        ReminderDeliveryAttemptId attemptId,
        ReminderInstanceId instanceId,
        ReminderDeliveryChannel channel,
        Instant attemptedAtUtc,
        ReminderDeliveryOutcome outcome,
        string? errorCode)
    {
        Validate(attemptId, instanceId, channel, outcome, errorCode);

        AttemptId = attemptId;
        InstanceId = instanceId;
        Channel = channel;
        AttemptedAtUtc = attemptedAtUtc;
        Outcome = outcome;
        ErrorCode = errorCode is null ? null : errorCode.Trim();
    }

    public ReminderDeliveryAttemptId AttemptId { get; }

    public ReminderInstanceId InstanceId { get; }

    public ReminderDeliveryChannel Channel { get; }

    public Instant AttemptedAtUtc { get; }

    public ReminderDeliveryOutcome Outcome { get; }

    public string? ErrorCode { get; }

    public static ReminderDeliveryAttempt Create(
        ReminderInstanceId instanceId,
        ReminderDeliveryChannel channel,
        Instant attemptedAtUtc,
        ReminderDeliveryOutcome outcome,
        string? errorCode = null,
        ReminderDeliveryAttemptId? attemptId = null) =>
        new(
            attemptId ?? ReminderDeliveryAttemptId.New(),
            instanceId,
            channel,
            attemptedAtUtc,
            outcome,
            errorCode);

    public static ReminderDeliveryAttempt Rehydrate(
        ReminderDeliveryAttemptId attemptId,
        ReminderInstanceId instanceId,
        ReminderDeliveryChannel channel,
        Instant attemptedAtUtc,
        ReminderDeliveryOutcome outcome,
        string? errorCode) =>
        new(attemptId, instanceId, channel, attemptedAtUtc, outcome, errorCode);

    private static void Validate(
        ReminderDeliveryAttemptId attemptId,
        ReminderInstanceId instanceId,
        ReminderDeliveryChannel channel,
        ReminderDeliveryOutcome outcome,
        string? errorCode)
    {
        _ = ReminderDeliveryAttemptId.From(attemptId.Value);
        _ = ReminderInstanceId.From(instanceId.Value);

        if (!Enum.IsDefined(channel))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.delivery.channel.invalid",
                "Delivery channel is not supported.",
                nameof(channel)));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.delivery.outcome.invalid",
                "Delivery outcome is not supported.",
                nameof(outcome)));
        }

        if (errorCode is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Any(char.IsControl))
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.delivery.error_code.invalid",
                "Delivery error code must be non-empty and contain no control characters.",
                nameof(errorCode)));
        }

        if (Encoding.UTF8.GetByteCount(errorCode.Trim()) > MaxErrorCodeBytes)
        {
            throw new DomainValidationException(new DomainValidationError(
                "reminder.delivery.error_code.too_long",
                "Delivery error code exceeds the persistence limit.",
                nameof(errorCode)));
        }
    }
}

/// <summary>独立的 UUID v7 子记录身份，避免把 channel retry 当作 Instance。</summary>
public readonly record struct ReminderDeliveryAttemptId
{
    private ReminderDeliveryAttemptId(Guid value)
    {
        Value = ReminderIdentityValidation.Validate(
            value,
            "reminder.delivery_attempt_id.empty",
            "reminder.delivery_attempt_id.not_uuid_v7",
            nameof(Value));
    }

    public Guid Value { get; }

    public static ReminderDeliveryAttemptId New() => new(Guid.CreateVersion7());

    public static ReminderDeliveryAttemptId From(Guid value) => new(value);

    public static ReminderDeliveryAttemptId Parse(string value)
    {
        if (!Guid.TryParse(value, out var guid))
        {
            throw ReminderIdentityValidation.InvalidFormat(nameof(value));
        }

        return From(guid);
    }

    public static bool TryParse(string? value, out ReminderDeliveryAttemptId id)
    {
        if (Guid.TryParse(value, out var guid) && ReminderIdentityValidation.IsUuidV7(guid))
        {
            id = new ReminderDeliveryAttemptId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}
