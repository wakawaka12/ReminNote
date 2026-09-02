using Microsoft.EntityFrameworkCore;

namespace ReminNote.Infrastructure.Persistence.Reminders;

/// <summary>
/// Explicit opt-in for P3 Reminder tables. It is intentionally not called by
/// ReminNoteDbContext until the owning migration/startup window is ready.
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
