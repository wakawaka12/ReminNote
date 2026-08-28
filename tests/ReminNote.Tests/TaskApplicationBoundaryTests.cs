using ReminNote.Core.Application;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Application;
using ReminNote.Infrastructure.Persistence;
using SystemTask = System.Threading.Tasks.Task;
using TaskAggregate = ReminNote.Core.Tasks.Task;

namespace ReminNote.Tests;

public sealed class TaskApplicationBoundaryTests
{
    [Fact]
    public async SystemTask ApplicationServiceExecutesCreateUpdateRecordAndDelete()
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestClock(TestValues.CreatedAt);
        TaskSnapshot created;

        using (var context = database.CreateContext())
        {
            created = await CreateApplication(context, clock).CreateAsync(
                new CreateTaskCommand(
                    "  生命周期任务  ",
                    TimeSpec.Anytime(TestValues.PlanDate)),
                cancellationToken);
        }

        Assert.Equal("生命周期任务", created.Title);
        Assert.Equal(TaskTimeType.ANYTIME, created.TimeType);
        Assert.Equal(TestValues.CreatedAt, created.CreatedAt);
        Assert.Equal(TestValues.CreatedAt, created.UpdatedAt);

        clock.Current = TestValues.ChangedAt;
        TaskSnapshot? updated;
        using (var context = database.CreateContext())
        {
            updated = await CreateApplication(context, clock).UpdateAsync(
                new UpdateTaskCommand(
                    created.Id,
                    "  已更新任务  ",
                    TimeSpec.Range(TestValues.PlanDate, new LocalTime(14, 0), new LocalTime(16, 0))),
                cancellationToken);
        }

        Assert.NotNull(updated);
        Assert.Equal(created.Id, updated!.Id);
        Assert.Equal("已更新任务", updated.Title);
        Assert.Equal(TaskTimeType.RANGE, updated.TimeType);
        Assert.Equal(TestValues.ChangedAt, updated.UpdatedAt);

        clock.Current = TestValues.ChangedAt.Plus(Duration.FromMinutes(5));
        TaskSnapshot? recorded;
        using (var context = database.CreateContext())
        {
            recorded = await CreateApplication(context, clock).RecordResultAsync(
                new RecordTaskResultCommand(created.Id, TaskResult.PARTIAL, "先完成一半"),
                cancellationToken);
        }

        Assert.NotNull(recorded);
        Assert.Equal((TaskResult?)TaskResult.PARTIAL, recorded!.Result);
        Assert.Equal("先完成一半", recorded.ResultNote);
        Assert.Equal(clock.Current, recorded.RecordedAt);

        using (var context = database.CreateContext())
        {
            var query = new TaskQueryService(context);
            var persisted = await query.FindAsync(created.Id, cancellationToken);

            Assert.Equal(recorded, persisted);
        }

        using (var context = database.CreateContext())
        {
            var application = CreateApplication(context, clock);

            Assert.True(await application.DeleteAsync(created.Id, cancellationToken));
            Assert.False(await application.DeleteAsync(created.Id, cancellationToken));
        }

        using (var context = database.CreateContext())
        {
            Assert.Null(await new TaskQueryService(context).FindAsync(created.Id, cancellationToken));
        }
    }

    [Fact]
    public async SystemTask ApplicationServiceReturnsEmptyResultsForMissingId()
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();
        var cancellationToken = TestContext.Current.CancellationToken;
        var missingId = TestValues.TaskId("0191f6a4-3b25-7c12-8d34-56789abcde99");

        using var context = database.CreateContext();
        var application = CreateApplication(context, new TestClock(TestValues.CreatedAt));

        Assert.Null(await application.UpdateAsync(
            new UpdateTaskCommand(
                missingId,
                "不存在的任务",
                TimeSpec.Anytime(TestValues.PlanDate)),
            cancellationToken));
        Assert.Null(await application.RecordResultAsync(
            new RecordTaskResultCommand(missingId, TaskResult.COMPLETED),
            cancellationToken));
        Assert.False(await application.DeleteAsync(missingId, cancellationToken));
        Assert.Null(await new TaskQueryService(context).FindAsync(missingId, cancellationToken));
    }

    [Fact]
    public async SystemTask QueryFiltersByPlanDateUsesStableOrderAndDoesNotTrackEntities()
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();
        var cancellationToken = TestContext.Current.CancellationToken;
        var otherDate = TestValues.PlanDate.PlusDays(1);
        var tasks = new[]
        {
            TaskAggregate.Create(
                TaskId(1),
                "任意",
                TimeSpec.Anytime(TestValues.PlanDate),
                TestValues.CreatedAt),
            TaskAggregate.Create(
                TaskId(2),
                "九点",
                TimeSpec.At(TestValues.PlanDate, new LocalTime(9, 0)),
                TestValues.CreatedAt),
            TaskAggregate.Create(
                TaskId(3),
                "十四点",
                TimeSpec.At(TestValues.PlanDate, new LocalTime(14, 0)),
                TestValues.CreatedAt),
            TaskAggregate.Create(
                TaskId(4),
                "范围",
                TimeSpec.Range(TestValues.PlanDate, new LocalTime(10, 0), new LocalTime(11, 0)),
                TestValues.CreatedAt),
            TaskAggregate.Create(
                TaskId(5),
                "其他日期",
                TimeSpec.Anytime(otherDate),
                TestValues.CreatedAt)
        };

        using (var context = database.CreateContext())
        {
            var repository = new TaskRepository(context);
            foreach (var task in tasks)
            {
                await repository.AddAsync(task, cancellationToken);
            }
        }

        using var readContext = database.CreateContext();
        var query = new TaskQueryService(readContext);
        var first = await ReadAllAsync(query.ListAsync(new TaskQuery(TestValues.PlanDate), cancellationToken));

        Assert.Equal(
            new[] { TaskId(1), TaskId(2), TaskId(3), TaskId(4) },
            first.Select(snapshot => snapshot.Id));
        Assert.All(first, snapshot => Assert.Equal(TestValues.PlanDate, snapshot.TimeSpec.LocalDate));
        Assert.All(first, snapshot => Assert.IsType<TaskSnapshot>(snapshot));
        Assert.DoesNotContain(first, snapshot => snapshot.Id == TaskId(5));
        Assert.Empty(readContext.ChangeTracker.Entries<TaskEntity>());

        Assert.NotNull(await query.FindAsync(TaskId(5), cancellationToken));
        Assert.Empty(readContext.ChangeTracker.Entries<TaskEntity>());

        var second = await ReadAllAsync(query.ListAsync(new TaskQuery(TestValues.PlanDate), cancellationToken));

        Assert.Equal(first, second);
        Assert.Empty(readContext.ChangeTracker.Entries<TaskEntity>());
    }

    private static TaskApplicationService CreateApplication(
        ReminNoteDbContext context,
        TestClock clock) =>
        new(new TaskRepository(context), clock);

    private static async System.Threading.Tasks.Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var values = new List<T>();
        await foreach (var value in source)
        {
            values.Add(value);
        }

        return values;
    }

    private static TaskId TaskId(int suffix) =>
        TestValues.TaskId($"0191f6a4-3b25-7c12-8d34-56789abcde{suffix:00}");

    private sealed class TestClock(Instant current) : IClock
    {
        public Instant Current { get; set; } = current;

        public Instant GetCurrentInstant() => Current;
    }
}
