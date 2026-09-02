using ReminNote.Core;

namespace ReminNote.Core.Reminders.Domain;

internal static class ReminderIdentityValidation
{
    public static Guid Validate(Guid value, string emptyCode, string invalidCode, string fieldName)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException(new DomainValidationError(
                emptyCode,
                "Reminder identity cannot be empty.",
                fieldName));
        }

        if (!IsUuidV7(value))
        {
            throw new DomainValidationException(new DomainValidationError(
                invalidCode,
                "Reminder identity must use UUID version 7.",
                fieldName));
        }

        return value;
    }

    public static bool IsUuidV7(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);

        // Guid's first three fields are little-endian in TryWriteBytes. The
        // version nibble is therefore at byte 7 and the RFC variant at byte 8.
        return (bytes[7] & 0xF0) == 0x70 && (bytes[8] & 0xC0) == 0x80;
    }

    public static DomainValidationException InvalidFormat(string fieldName) =>
        new(new DomainValidationError(
            "reminder.identity.invalid_format",
            "Reminder identity must be a valid UUID.",
            fieldName));
}

public readonly record struct ReminderRuleId
{
    private ReminderRuleId(Guid value)
    {
        Value = ReminderIdentityValidation.Validate(
            value,
            "reminder.rule_id.empty",
            "reminder.rule_id.not_uuid_v7",
            nameof(Value));
    }

    public Guid Value { get; }

    public static ReminderRuleId New() => new(Guid.CreateVersion7());

    public static ReminderRuleId From(Guid value) => new(value);

    public static ReminderRuleId Parse(string value)
    {
        if (!Guid.TryParse(value, out var guid))
        {
            throw ReminderIdentityValidation.InvalidFormat(nameof(value));
        }

        return From(guid);
    }

    public static bool TryParse(string? value, out ReminderRuleId id)
    {
        if (Guid.TryParse(value, out var guid) && ReminderIdentityValidation.IsUuidV7(guid))
        {
            id = new ReminderRuleId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ReminderScheduleId
{
    private ReminderScheduleId(Guid value)
    {
        Value = ReminderIdentityValidation.Validate(
            value,
            "reminder.schedule_id.empty",
            "reminder.schedule_id.not_uuid_v7",
            nameof(Value));
    }

    public Guid Value { get; }

    public static ReminderScheduleId New() => new(Guid.CreateVersion7());

    public static ReminderScheduleId From(Guid value) => new(value);

    public static ReminderScheduleId Parse(string value)
    {
        if (!Guid.TryParse(value, out var guid))
        {
            throw ReminderIdentityValidation.InvalidFormat(nameof(value));
        }

        return From(guid);
    }

    public static bool TryParse(string? value, out ReminderScheduleId id)
    {
        if (Guid.TryParse(value, out var guid) && ReminderIdentityValidation.IsUuidV7(guid))
        {
            id = new ReminderScheduleId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ReminderInstanceId
{
    private ReminderInstanceId(Guid value)
    {
        Value = ReminderIdentityValidation.Validate(
            value,
            "reminder.instance_id.empty",
            "reminder.instance_id.not_uuid_v7",
            nameof(Value));
    }

    public Guid Value { get; }

    public static ReminderInstanceId New() => new(Guid.CreateVersion7());

    public static ReminderInstanceId From(Guid value) => new(value);

    public static ReminderInstanceId Parse(string value)
    {
        if (!Guid.TryParse(value, out var guid))
        {
            throw ReminderIdentityValidation.InvalidFormat(nameof(value));
        }

        return From(guid);
    }

    public static bool TryParse(string? value, out ReminderInstanceId id)
    {
        if (Guid.TryParse(value, out var guid) && ReminderIdentityValidation.IsUuidV7(guid))
        {
            id = new ReminderInstanceId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct LogicalReminderId
{
    private LogicalReminderId(Guid value)
    {
        Value = ReminderIdentityValidation.Validate(
            value,
            "reminder.logical_id.empty",
            "reminder.logical_id.not_uuid_v7",
            nameof(Value));
    }

    public Guid Value { get; }

    public static LogicalReminderId New() => new(Guid.CreateVersion7());

    public static LogicalReminderId From(Guid value) => new(value);

    public static LogicalReminderId Parse(string value)
    {
        if (!Guid.TryParse(value, out var guid))
        {
            throw ReminderIdentityValidation.InvalidFormat(nameof(value));
        }

        return From(guid);
    }

    public static bool TryParse(string? value, out LogicalReminderId id)
    {
        if (Guid.TryParse(value, out var guid) && ReminderIdentityValidation.IsUuidV7(guid))
        {
            id = new LogicalReminderId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

public readonly record struct OccurrenceId
{
    private OccurrenceId(Guid value)
    {
        Value = ReminderIdentityValidation.Validate(
            value,
            "reminder.occurrence_id.empty",
            "reminder.occurrence_id.not_uuid_v7",
            nameof(Value));
    }

    public Guid Value { get; }

    public static OccurrenceId New() => new(Guid.CreateVersion7());

    public static OccurrenceId From(Guid value) => new(value);

    public static OccurrenceId Parse(string value)
    {
        if (!Guid.TryParse(value, out var guid))
        {
            throw ReminderIdentityValidation.InvalidFormat(nameof(value));
        }

        return From(guid);
    }

    public static bool TryParse(string? value, out OccurrenceId id)
    {
        if (Guid.TryParse(value, out var guid) && ReminderIdentityValidation.IsUuidV7(guid))
        {
            id = new OccurrenceId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}
