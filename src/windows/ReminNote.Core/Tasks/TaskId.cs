using ReminNote.Core;

namespace ReminNote.Core.Tasks;

/// <summary>
/// Local Task identity backed by a UUID version 7.
/// </summary>
public readonly record struct TaskId
{
    private TaskId(Guid value)
    {
        Validate(value);
        Value = value;
    }

    public Guid Value { get; }

    public static TaskId New() => new(Guid.CreateVersion7());

    public static TaskId From(Guid value) => new(value);

    public static TaskId Parse(string value)
    {
        if (!Guid.TryParse(value, out var guid))
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.id.invalid_format",
                "Task ID must be a valid UUID.",
                nameof(value)));
        }

        return From(guid);
    }

    public static bool TryParse(string? value, out TaskId taskId)
    {
        if (Guid.TryParse(value, out var guid) && IsUuidV7(guid))
        {
            taskId = new TaskId(guid);
            return true;
        }

        taskId = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");

    private static void Validate(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.id.empty",
                "Task ID cannot be empty.",
                nameof(Value)));
        }

        if (!IsUuidV7(value))
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.id.not_uuid_v7",
                "Task ID must use UUID version 7.",
                nameof(Value)));
        }
    }

    private static bool IsUuidV7(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);

        // Guid's first three fields are stored little-endian by ToByteArray /
        // TryWriteBytes. The version nibble is therefore at byte 7, while the
        // RFC 4122 variant is at byte 8.
        return (bytes[7] & 0xF0) == 0x70 && (bytes[8] & 0xC0) == 0x80;
    }
}
