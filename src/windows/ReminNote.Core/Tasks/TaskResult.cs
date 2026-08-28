using NodaTime;
using ReminNote.Core;

namespace ReminNote.Core.Tasks;

/// <summary>
/// A final user-recorded result. A missing value means that no result has
/// been recorded yet; it is not an implicit MISSED result.
/// </summary>
public enum TaskResult
{
    COMPLETED,
    MISSED,
    PARTIAL
}

/// <summary>
/// The factual result recording event for a task.
/// </summary>
public sealed record TaskResultRecord
{
    private TaskResultRecord(TaskResult result, Instant recordedAt, string? note)
    {
        ValidateResult(result);
        Result = result;
        RecordedAt = recordedAt;
        Note = NormalizeNote(note);
    }

    public TaskResult Result { get; }

    public Instant RecordedAt { get; }

    public string? Note { get; }

    public static TaskResultRecord Create(TaskResult result, Instant recordedAt, string? note = null)
    {
        return new TaskResultRecord(result, recordedAt, note);
    }

    private static void ValidateResult(TaskResult result)
    {
        if (result is not (TaskResult.COMPLETED or TaskResult.MISSED or TaskResult.PARTIAL))
        {
            throw new DomainValidationException(new DomainValidationError(
                "task.result.invalid",
                "Task result is not supported.",
                nameof(result)));
        }
    }

    private static string? NormalizeNote(string? note)
    {
        return string.IsNullOrWhiteSpace(note) ? null : note;
    }
}
