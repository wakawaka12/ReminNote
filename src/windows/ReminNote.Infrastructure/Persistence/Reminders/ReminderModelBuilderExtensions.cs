using Microsoft.EntityFrameworkCore;

namespace ReminNote.Infrastructure.Persistence.Reminders;

/// <summary>
/// Explicit application point for the formal P3 Reminder model. Legacy P2
/// fixture options remain isolated because ReminNoteDbContext calls this only
/// for its formal options marker.
/// </summary>
public static class ReminderModelBuilderExtensions
{
    public static ModelBuilder ApplyReminderConfigurations(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ReminderRuleEntityConfiguration.Configure(modelBuilder.Entity<ReminderRuleEntity>());
        ReminderScheduleEntityConfiguration.Configure(modelBuilder.Entity<ReminderScheduleEntity>());
        ReminderInstanceEntityConfiguration.Configure(modelBuilder.Entity<ReminderInstanceEntity>());
        ReminderDeliveryAttemptEntityConfiguration.Configure(modelBuilder.Entity<ReminderDeliveryAttemptEntity>());
        return modelBuilder;
    }
}
