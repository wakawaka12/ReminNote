using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.Reminders;

namespace ReminNote.Tests;

public sealed class ReminderPersistenceMappingTests
{
    private static readonly Instant CreatedAt = Instant.FromUtc(2026, 9, 2, 8, 0);
    private static readonly Instant TriggerAt = Instant.FromUtc(2026, 9, 2, 8, 30);

    [Fact]
    public void DomainProjectionRoundTripsAllFourReminderRows()
    {
        var rule = ReminderRule.CreateForTask(
            Guid.CreateVersion7(),
            ReminderPurpose.TASK_CUSTOM,
            ReminderTiming.AbsoluteUtc(TriggerAt),
            ReminderPriority.HIGH,
            true,
            RepeatPolicy.Disabled,
            WakePolicy.YES,
            true,
            CreatedAt);
        var schedule = ReminderSchedule.CreateFromRule(
            rule,
            LogicalReminderId.New(),
            1,
            TriggerAt,
            CreatedAt,
            "UTC");
        var instance = ReminderInstance.CreateFromSchedule(schedule, 1, TriggerAt);
        instance.Resolve(ResolutionAction.DONE, TriggerAt.Plus(Duration.FromSeconds(1)));
        var delivery = ReminderDeliveryAttempt.Create(
            instance.Id,
            ReminderDeliveryChannel.TOAST,
            TriggerAt,
            ReminderDeliveryOutcome.DELIVERED);

        var ruleEntity = ReminderRuleEntity.FromDomain(rule);
        Assert.Equal(ReminderTimingKind.ABSOLUTE_UTC, ruleEntity.TimingKind);
        Assert.Null(ruleEntity.TimingAnchor);
        Assert.Null(ruleEntity.OffsetSeconds);
        Assert.NotNull(ruleEntity.AbsoluteAtUtc);
        Assert.Equal(rule.Timing, ruleEntity.ToDomain().Timing);

        var scheduleEntity = ReminderScheduleEntity.FromDomain(schedule);
        var instanceEntity = ReminderInstanceEntity.FromDomain(instance);
        var deliveryEntity = ReminderDeliveryAttemptEntity.FromDomain(delivery);

        var scheduleRoundTrip = scheduleEntity.ToDomain();
        var instanceRoundTrip = instanceEntity.ToDomain();
        var deliveryRoundTrip = deliveryEntity.ToDomain();
        Assert.Equal(schedule.Id, scheduleRoundTrip.Id);
        Assert.Equal(schedule.TriggerAtUtc, scheduleRoundTrip.TriggerAtUtc);
        Assert.Equal(schedule.Cause, scheduleRoundTrip.Cause);
        Assert.Equal(instance.Lifecycle, instanceRoundTrip.Lifecycle);
        Assert.Equal(instance.ResolutionAction, instanceRoundTrip.ResolutionAction);
        Assert.Equal(delivery.ErrorCode, deliveryRoundTrip.ErrorCode);

        WithDatabase((context, _) =>
        {
            context.Rules.Add(ruleEntity);
            context.Schedules.Add(scheduleEntity);
            context.Instances.Add(instanceEntity);
            context.Deliveries.Add(deliveryEntity);
            context.SaveChanges();

            Assert.NotNull(context.Rules.Single(entity => entity.Id == rule.Id.Value));
            Assert.Equal(ReminderLifecycle.RESOLVED, context.Instances.Single().Lifecycle);
            Assert.Equal(1, context.Deliveries.Count());
        });
    }

    [Fact]
    public void ExplicitReminderModelHasNamedTablesIndexesAndNoCurrentContextDiscovery()
    {
        WithDatabase((context, _) =>
        {
            var tableNames = context.Model.GetEntityTypes()
                .Select(entity => entity.GetTableName())
                .Where(name => name is not null)
                .ToArray();

            Assert.Contains("reminder_rules", tableNames);
            Assert.Contains("reminder_schedules", tableNames);
            Assert.Contains("reminder_instances", tableNames);
            Assert.Contains("reminder_delivery_attempts", tableNames);

            var scheduleType = context.Model.FindEntityType(typeof(ReminderScheduleEntity));
            Assert.NotNull(scheduleType);
            Assert.Contains(
                scheduleType!.GetIndexes(),
                index => index.IsUnique && index.GetDatabaseName() == "ux_reminder_schedules_rule_occurrence_revision");
            Assert.Contains(
                scheduleType.GetIndexes(),
                index => index.GetDatabaseName() == "ix_reminder_schedules_state_trigger_at_utc");

            var instanceType = context.Model.FindEntityType(typeof(ReminderInstanceEntity));
            Assert.NotNull(instanceType);
            Assert.Contains(
                instanceType!.GetIndexes(),
                index => index.IsUnique && index.GetDatabaseName() == "ux_reminder_instances_schedule_id");
        });

        using var legacyConnection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        legacyConnection.Open();
        var legacyOptions = new DbContextOptionsBuilder<ReminNoteDbContext>()
            .UseSqlite(legacyConnection)
            .Options;
        using var legacyContext = new ReminNoteDbContext(legacyOptions);

        Assert.Null(legacyContext.Model.FindEntityType(typeof(ReminderRuleEntity)));
        Assert.Null(legacyContext.Model.FindEntityType(typeof(ReminderScheduleEntity)));
    }

    [Fact]
    public void ScheduleRevisionUniqueIndexRejectsDuplicateRevision()
    {
        WithDatabase((context, _) =>
        {
            var rule = ReminderRule.CreateForTask(
                Guid.CreateVersion7(),
                ReminderPurpose.TASK_START,
                ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0),
                ReminderPriority.NORMAL,
                false,
                RepeatPolicy.Disabled,
                WakePolicy.DEFAULT,
                true,
                CreatedAt);
            var logical = LogicalReminderId.New();
            var first = ReminderSchedule.CreateFromRule(rule, logical, 1, TriggerAt, CreatedAt);
            context.Rules.Add(ReminderRuleEntity.FromDomain(rule));
            context.Schedules.Add(ReminderScheduleEntity.FromDomain(first));
            context.SaveChanges();

            var duplicate = ReminderSchedule.Create(
                ReminderScheduleId.New(),
                rule.Id,
                rule.OccurrenceId,
                logical,
                ScheduleCause.RULE,
                rule.RuleRevision,
                1,
                TriggerAt.Plus(Duration.FromMinutes(1)),
                null,
                CreatedAt,
                rule.Purpose,
                rule.Priority,
                rule.Pinned);
            context.Schedules.Add(ReminderScheduleEntity.FromDomain(duplicate));
            Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        });
    }

    private static void WithDatabase(Action<ReminderModelContext, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ReminNote-P3-01-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "reminders.sqlite");

        try
        {
            using (var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False"))
            {
                connection.Open();
                var options = new DbContextOptionsBuilder<ReminderModelContext>()
                    .UseSqlite(connection)
                    .Options;
                using (var context = new ReminderModelContext(options))
                {
                    context.Database.EnsureCreated();
                    action(context, root);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class ReminderModelContext(DbContextOptions<ReminderModelContext> options) : DbContext(options)
    {
        public DbSet<ReminderRuleEntity> Rules => Set<ReminderRuleEntity>();

        public DbSet<ReminderScheduleEntity> Schedules => Set<ReminderScheduleEntity>();

        public DbSet<ReminderInstanceEntity> Instances => Set<ReminderInstanceEntity>();

        public DbSet<ReminderDeliveryAttemptEntity> Deliveries => Set<ReminderDeliveryAttemptEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyReminderConfigurations();
    }
}
