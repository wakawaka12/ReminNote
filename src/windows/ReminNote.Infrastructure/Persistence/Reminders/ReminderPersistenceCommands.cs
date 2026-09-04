using Microsoft.EntityFrameworkCore;
using NodaTime;
using ReminNote.Core.Reminders.Domain;

namespace ReminNote.Infrastructure.Persistence.Reminders;

/// <summary>
/// Reminder domain mutations that must run inside the already-open Agent
/// transaction. This type deliberately owns no connection, transaction, or
/// writer gate; callers supply the transaction-bound DbContext.
/// </summary>
public static class ReminderPersistenceCommands
{
    public static async ValueTask<IReadOnlyList<Guid>> CancelPendingForOccurrenceAsync(
        ReminNoteDbContext context,
        Guid occurrenceId,
        Instant atUtc,
        ScheduleStateReason reason = ScheduleStateReason.TASK_RESULT_RECORDED,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = OccurrenceId.From(occurrenceId);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        var schedules = await context.ReminderSchedules
            .Where(schedule =>
                schedule.OccurrenceId == occurrenceId &&
                schedule.State == ScheduleState.PENDING)
            .OrderBy(schedule => schedule.ScheduleRevision)
            .ThenBy(schedule => schedule.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (schedules.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        foreach (var schedule in schedules)
        {
            var domain = schedule.ToDomain();
            domain.Cancel(reason, atUtc);
            schedule.Apply(domain);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return schedules.Select(schedule => schedule.Id).ToArray();
    }
}
