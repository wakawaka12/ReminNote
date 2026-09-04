using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ReminNote.Agent.Scheduling;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence.Reminders;
using System.Security.Cryptography;
using System.Text;
using SystemTask = System.Threading.Tasks.Task;

#pragma warning disable CA1707 // Slice directory and test names mirror the frozen plan.
namespace ReminNote.Tests.P3_03;

public sealed class AgentReminderSchedulerStoreTests
{
    private static readonly Instant CreatedAt = Instant.FromUtc(2026, 9, 2, 0, 0);
    private static readonly Instant EvaluatedAt = Instant.FromUtc(2026, 9, 2, 1, 0);
    private const string UserSid = "S-1-5-21-100-200-300-400";

    [Fact]
    public async SystemTask DueCommitUsesAgentWriterAndSurvivesSchedulerRestart()
    {
        using var database = new IsolatedFileDatabase();
        var fixture = await database.SeedAsync(
            triggerAtUtc: Instant.FromUtc(2026, 9, 2, 0, 30));

        await using var writerConnection = database.OpenConnection();
        await using var writer = await P25StorageStore.OpenReadyAsync(
                writerConnection,
                fixture.ProfileScope,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var taskId = fixture.TaskId;
        var scheduleId = fixture.ScheduleId;
        await using (var firstAgent = new AgentReminderSchedulerStore(
                         writer,
                         database.DatabasePath,
                         UserSid,
                         Id(40)))
        await using (var scheduler = new ReminderScheduler(
                         firstAgent,
                         new FixedClock(EvaluatedAt),
                         identityGenerator: new FixedIdentityGenerator(41, 42)))
        {
            var run = await scheduler.RunDueCycleAsync(
                cancellationToken: TestContext.Current.CancellationToken);

            var result = Assert.Single(run.DueResults);
            Assert.Equal(ReminderDueResultKind.TRIGGERED, result.Kind);
            Assert.True(result.ShouldDispatch);
            Assert.NotNull(result.Instance);
            Assert.Equal(ScheduleState.CONSUMED, result.FinalScheduleState);
            Assert.Null(run.NextWakeupUtc);
        }

        await using var restartedAgent = new AgentReminderSchedulerStore(
            writer,
            database.DatabasePath,
            UserSid,
            Id(43));
        await using var restartedScheduler = new ReminderScheduler(
            restartedAgent,
            new FixedClock(EvaluatedAt),
            identityGenerator: new FixedIdentityGenerator(44, 45));
        var recovery = await restartedScheduler.RecoverAsync(
            TestContext.Current.CancellationToken);

        Assert.True(recovery.IsRecovery);
        Assert.Empty(recovery.DueResults);
        Assert.Equal(1L, (await writer.ReadRevisionStateAsync(
            TestContext.Current.CancellationToken)).CurrentRevision);

        await using var verifyConnection = database.OpenConnection();
        await using var verifyContext = ReminNoteDatabase.CreateContext(verifyConnection);
        Assert.Equal(
            1L,
            await verifyContext.ReminderSchedules.CountAsync(
                schedule =>
                    schedule.Id == scheduleId &&
                    schedule.State == ScheduleState.CONSUMED,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            1L,
            await verifyContext.ReminderInstances.CountAsync(
                instance => instance.ScheduleId == scheduleId,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            1L,
            await verifyContext.ChangeJournal.CountAsync(
                entry =>
                    entry.ProfileScope == fixture.ProfileScope &&
                    entry.EntityType == "reminder_instance" &&
                    entry.ChangeKind == "triggered",
                TestContext.Current.CancellationToken));
        var receipt = await verifyContext.CommandReceipts.SingleAsync(
            entry =>
                entry.ActualUserSid == UserSid &&
                entry.ProfileScope == fixture.ProfileScope &&
                entry.Operation == "agent.reminder.due",
            TestContext.Current.CancellationToken);
        Assert.Equal("COMMITTED", receipt.Status);
        Assert.True(receipt.Changed);
        Assert.Equal(1L, receipt.CommittedRevision);
    }

    [Fact]
    public async SystemTask FormalReminderMigrationRollsBackAndReappliesInAnIsolatedFile()
    {
        using var database = new IsolatedFileDatabase();
        await using (var connection = database.OpenConnection())
        {
            await using var context = ReminNoteDatabase.CreateContext(connection);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            Assert.Contains(
                "reminder_schedules",
                await ReadTablesAsync(connection, TestContext.Current.CancellationToken));

            await context.Database.MigrateAsync(
                    "20260831090000_P25StorageConsistency",
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var rolledBackTables = await ReadTablesAsync(
                    connection,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.DoesNotContain("reminder_rules", rolledBackTables);
            Assert.DoesNotContain("reminder_schedules", rolledBackTables);
            Assert.DoesNotContain("reminder_instances", rolledBackTables);

            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var reappliedTables = await ReadTablesAsync(
                    connection,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Contains("reminder_rules", reappliedTables);
            Assert.Contains("reminder_schedules", reappliedTables);
            Assert.Contains("reminder_instances", reappliedTables);
        }
    }

    [Fact]
    public async SystemTask TaskResultCancellationUsesTheAgentWriterTransaction()
    {
        using var database = new IsolatedFileDatabase();
        var fixture = await database.SeedAsync(
            triggerAtUtc: EvaluatedAt.Plus(Duration.FromHours(1)));

        await using var connection = database.OpenConnection();
        await using var writer = await P25StorageStore.OpenReadyAsync(
                connection,
                fixture.ProfileScope,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var request = new P25CommandRequest(
            UserSid,
            fixture.ProfileScope,
            Id(50).ToString("D"),
            "command.task.record_result",
            P25StorageLimits.CanonicalHashVersion,
            SHA256.HashData(Encoding.UTF8.GetBytes("task-result")),
            ExpectedRevision: 0,
            Id(51).ToString("D"),
            Id(52).ToString("D"));
        var write = await writer.Writer.ExecuteMutationAsync(
                request,
                async (mutation, cancellationToken) =>
                {
                    await using var context = ReminNoteDatabase.CreateContext(mutation.Connection);
                    context.Database.UseTransaction(mutation.Transaction);
                    var cancelled = await ReminderPersistenceCommands
                        .CancelPendingForOccurrenceAsync(
                            context,
                            fixture.TaskId,
                            EvaluatedAt,
                            ScheduleStateReason.TASK_RESULT_RECORDED,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return cancelled.Count == 0
                        ? P25MutationDecision.NoOp()
                        : P25MutationDecision.Changed(
                            cancelled.Select(scheduleId => new P25JournalChange(
                                "reminder_schedule",
                                scheduleId.ToString("D"),
                                "cancelled")));
                },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(P25MutationOutcome.Changed, write.Outcome);
        Assert.Equal(1L, write.CommittedRevision);

        await using var verifyConnection = database.OpenConnection();
        await using var verifyContext = ReminNoteDatabase.CreateContext(verifyConnection);
        var cancelledSchedule = await verifyContext.ReminderSchedules.SingleAsync(
            schedule => schedule.Id == fixture.ScheduleId,
            TestContext.Current.CancellationToken);
        Assert.Equal(ScheduleState.CANCELLED, cancelledSchedule.State);
        Assert.Equal(
            ScheduleStateReason.TASK_RESULT_RECORDED,
            cancelledSchedule.TerminalReason);
        Assert.Equal(
            1L,
            await verifyContext.ChangeJournal.CountAsync(
                entry =>
                    entry.Revision == 1 &&
                    entry.EntityType == "reminder_schedule" &&
                    entry.EntityId == fixture.ScheduleId.ToString("D") &&
                    entry.ChangeKind == "cancelled",
                TestContext.Current.CancellationToken));
    }

    private static async Task<HashSet<string>> ReadTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static Guid Id(int suffix) =>
        Guid.Parse($"0191f6a4-3b25-7c12-8d34-56789abcde{suffix:X2}");

    private sealed class FixedClock(Instant current) : IClock
    {
        public Instant GetCurrentInstant() => current;
    }

    private sealed class FixedIdentityGenerator(
        int instanceSuffix,
        int scheduleSuffix) : IReminderIdentityGenerator
    {
        private int nextInstanceSuffix = instanceSuffix;
        private int nextScheduleSuffix = scheduleSuffix;

        public ReminderInstanceId NewInstanceId() =>
            ReminderInstanceId.From(Id(nextInstanceSuffix++));

        public ReminderScheduleId NewScheduleId() =>
            ReminderScheduleId.From(Id(nextScheduleSuffix++));
    }

    private sealed class IsolatedFileDatabase : IDisposable
    {
        private readonly string root;

        public IsolatedFileDatabase()
        {
            root = Path.Combine(
                Path.GetTempPath(),
                "reminnote-p3-03-agent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            DatabasePath = Path.Combine(root, "reminnote.sqlite");
            ProfileScope = ProtocolProfileScope.Derive(UserSid, DatabasePath);
        }

        public string DatabasePath { get; }

        public string ProfileScope { get; }

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true
            }.ToString());
            connection.Open();
            return connection;
        }

        public async System.Threading.Tasks.Task<SeededReminder> SeedAsync(Instant triggerAtUtc)
        {
            await using var connection = OpenConnection();
            await using (var context = ReminNoteDatabase.CreateContext(connection))
            {
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            }

            await P25StorageSchema.EnsureProfileAsync(
                    connection,
                    ProfileScope,
                    cancellationToken: TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            var taskId = Id(10);
            var task = ReminNote.Core.Tasks.Task.Create(
                TaskId.From(taskId),
                "持久化提醒测试",
                TimeSpec.At(new LocalDate(2026, 9, 2), new LocalTime(8, 30)),
                CreatedAt);
            var rule = ReminderRule.CreateForTask(
                taskId,
                ReminderPurpose.TASK_START,
                ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0),
                ReminderPriority.NORMAL,
                pinned: false,
                RepeatPolicy.Disabled,
                WakePolicy.DEFAULT,
                enabled: true,
                CreatedAt);
            var schedule = ReminderSchedule.CreateFromRule(
                rule,
                LogicalReminderId.From(Id(11)),
                scheduleRevision: 1,
                triggerAtUtc,
                CreatedAt,
                timeZoneId: "Asia/Shanghai",
                id: ReminderScheduleId.From(Id(12)));

            await using var seedContext = ReminNoteDatabase.CreateContext(connection);
            seedContext.Tasks.Add(TaskEntity.FromDomain(task));
            seedContext.ReminderRules.Add(ReminderRuleEntity.FromDomain(rule));
            seedContext.ReminderSchedules.Add(ReminderScheduleEntity.FromDomain(schedule));
            await seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            return new SeededReminder(taskId, schedule.Id.Value, ProfileScope);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed record SeededReminder(
        Guid TaskId,
        Guid ScheduleId,
        string ProfileScope);
}
