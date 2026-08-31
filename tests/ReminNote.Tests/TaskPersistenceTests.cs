using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime.Text;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Persistence;
using SystemTask = System.Threading.Tasks.Task;

namespace ReminNote.Tests;

public sealed class TaskPersistenceTests
{
    private static readonly string[] ExpectedP2AndP25Tables =
    [
        "__EFMigrationsHistory",
        "__EFMigrationsLock",
        "app_settings",
        "change_journal",
        "command_receipt",
        "revision_state",
        "task_history",
        "tasks"
    ];

    private static readonly LocalDatePattern LocalDatePattern =
        NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("uuuu-MM-dd");

    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'");

    public static TheoryData<string, string> InvalidRawRows =>
        new()
        {
            { "mixed-shape", "ck_tasks_time_shape" },
            { "blank-title", "ck_tasks_title_not_blank" },
            { "time-partial", "ck_tasks_partial_requires_range" },
            { "zero-range", "ck_tasks_time_shape" },
            { "non-uuid-v7", "ck_tasks_id_uuid_v7" },
            { "updated-before-created", "ck_tasks_timestamp_order" },
            { "recorded-before-created", "ck_tasks_result_timestamp_order" },
            { "recorded-after-updated", "ck_tasks_result_timestamp_order" }
        };

    [Fact]
    public void DatabaseMigrateCreatesP2AndP25TablesAndIsIdempotent()
    {
        using var database = new SqliteTestDatabase();

        database.Migrate();
        var firstTables = ReadTables(database.Connection);
        database.Migrate();
        var secondTables = ReadTables(database.Connection);

        Assert.Equal(ExpectedP2AndP25Tables, firstTables);
        Assert.Equal(firstTables, secondTables);
        Assert.DoesNotContain("reminders", secondTables, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("anime", secondTables, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("sync", secondTables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async SystemTask CrossMidnightPartialTaskRoundTripsWithPlanDateNoteAndRecordedAt()
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();

        var task = ReminNote.Core.Tasks.Task.Create(
            TestValues.TaskId(),
            "跨午夜测试任务",
            TimeSpec.Range(TestValues.PlanDate, new LocalTime(23, 0), new LocalTime(1, 0)),
            TestValues.CreatedAt);
        task.RecordResult(TaskResult.PARTIAL, TestValues.ChangedAt, "明天继续");

        using (var writeContext = database.CreateContext())
        {
            var repository = new TaskRepository(writeContext);
            await repository.AddAsync(task, TestContext.Current.CancellationToken);
        }

        ReminNote.Core.Tasks.Task? reloaded;
        using (var readContext = database.CreateContext())
        {
            var repository = new TaskRepository(readContext);
            reloaded = await repository.FindAsync(task.Id, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(reloaded);
        var range = Assert.IsType<TimeRangeSpec>(reloaded!.TimeSpec);
        Assert.Equal(task.Id, reloaded.Id);
        Assert.Equal("跨午夜测试任务", reloaded.Title);
        Assert.Equal(TestValues.PlanDate, range.LocalDate);
        Assert.True(range.IsCrossMidnight);
        Assert.Equal(TestValues.PlanDate.PlusDays(1), range.EndLocalDate);
        Assert.Equal((TaskResult?)TaskResult.PARTIAL, reloaded.Result);
        Assert.NotNull(reloaded.ResultRecord);
        Assert.Equal(TestValues.ChangedAt, reloaded.ResultRecord!.RecordedAt);
        Assert.Equal("明天继续", reloaded.ResultNote);
        Assert.Equal(TestValues.CreatedAt, reloaded.CreatedAt);
        Assert.Equal(TestValues.ChangedAt, reloaded.UpdatedAt);
    }

    [Fact]
    public async SystemTask RepositorySupportsCreateReadUpdateAndDeleteWithSeparateContexts()
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();
        var id = TestValues.AnotherTaskId();

        var task = ReminNote.Core.Tasks.Task.Create(
            id,
            "待更新任务",
            TimeSpec.Anytime(TestValues.PlanDate),
            TestValues.CreatedAt);

        using (var createContext = database.CreateContext())
        {
            await new TaskRepository(createContext).AddAsync(
                task,
                TestContext.Current.CancellationToken);
        }

        using (var updateContext = database.CreateContext())
        {
            var repository = new TaskRepository(updateContext);
            var stored = await repository.FindAsync(id, TestContext.Current.CancellationToken);

            Assert.NotNull(stored);
            stored!.Rename("已更新任务", TestValues.ChangedAt);
            stored.ChangeTime(TimeSpec.At(TestValues.PlanDate, TestValues.Afternoon), TestValues.ChangedAt);
            await repository.UpdateAsync(stored, TestContext.Current.CancellationToken);
        }

        using (var readContext = database.CreateContext())
        {
            var stored = await new TaskRepository(readContext).FindAsync(
                id,
                TestContext.Current.CancellationToken);

            Assert.NotNull(stored);
            Assert.Equal(id, stored!.Id);
            Assert.Equal("已更新任务", stored.Title);
            Assert.Equal(TimeSpec.At(TestValues.PlanDate, TestValues.Afternoon), stored.TimeSpec);
            Assert.Equal(TestValues.ChangedAt, stored.UpdatedAt);
        }

        using (var deleteContext = database.CreateContext())
        {
            var repository = new TaskRepository(deleteContext);

            Assert.True(await repository.DeleteAsync(id, TestContext.Current.CancellationToken));
            Assert.False(await repository.DeleteAsync(id, TestContext.Current.CancellationToken));
            Assert.Null(await repository.FindAsync(id, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async SystemTask RecordedTaskKeepsOriginalTimeWhenChangeTimeFailsBeforeRepositoryUpdate()
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();
        var id = TestValues.AnotherTaskId();
        var originalTime = TimeSpec.Range(TestValues.PlanDate, new LocalTime(23, 0), new LocalTime(1, 0));

        var task = ReminNote.Core.Tasks.Task.Create(
            id,
            "持久化改期保护",
            originalTime,
            TestValues.CreatedAt);
        task.RecordResult(TaskResult.PARTIAL, TestValues.ChangedAt, "保留原计划");

        using (var writeContext = database.CreateContext())
        {
            await new TaskRepository(writeContext).AddAsync(task, TestContext.Current.CancellationToken);
        }

        using (var updateContext = database.CreateContext())
        {
            var repository = new TaskRepository(updateContext);
            var stored = await repository.FindAsync(id, TestContext.Current.CancellationToken);

            Assert.NotNull(stored);
            TestValues.AssertValidationCode(
                () => stored!.ChangeTime(
                    TimeSpec.Anytime(TestValues.PlanDate.PlusDays(1)),
                    TestValues.ChangedAt.Plus(Duration.FromMinutes(1))),
                "task.time_spec.changed_after_result");
        }

        using (var readContext = database.CreateContext())
        {
            var persisted = await new TaskRepository(readContext).FindAsync(
                id,
                TestContext.Current.CancellationToken);

            Assert.NotNull(persisted);
            Assert.Equal(originalTime, persisted!.TimeSpec);
            Assert.Equal(TaskResult.PARTIAL, persisted.Result);
            Assert.Equal("保留原计划", persisted.ResultNote);
            Assert.Equal(TestValues.ChangedAt, persisted.UpdatedAt);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidRawRows))]
    public void RawInsertIsRejectedByTheNamedSchemaConstraint(
        string violation,
        string expectedConstraint)
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();
        var row = CreateValidRawRow();

        row = violation switch
        {
            "mixed-shape" => row with
            {
                TimeType = 0,
                TimePoint = TestValues.Afternoon.NanosecondOfDay
            },
            "blank-title" => row with { Title = "   " },
            "time-partial" => row with
            {
                TimeType = 1,
                TimePoint = TestValues.Afternoon.NanosecondOfDay,
                Result = 2,
                ResultRecordedAt = FormatInstant(TestValues.ChangedAt)
            },
            "zero-range" => row with
            {
                TimeType = 2,
                RangeStart = TestValues.Afternoon.NanosecondOfDay,
                RangeEnd = TestValues.Afternoon.NanosecondOfDay
            },
            "non-uuid-v7" => row with
            {
                Id = "0191f6a4-3b25-4c12-8d34-56789abcde01"
            },
            "updated-before-created" => row with
            {
                UpdatedAt = FormatInstant(TestValues.CreatedAt.Minus(Duration.FromSeconds(1)))
            },
            "recorded-before-created" => row with
            {
                Result = 0,
                ResultRecordedAt = FormatInstant(TestValues.CreatedAt.Minus(Duration.FromSeconds(1)))
            },
            "recorded-after-updated" => row with
            {
                Result = 0,
                ResultRecordedAt = FormatInstant(TestValues.ChangedAt),
                UpdatedAt = FormatInstant(TestValues.CreatedAt)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(violation), violation, null)
        };

        var exception = Assert.Throws<SqliteException>(() => InsertRaw(database.Connection, row));

        Assert.Contains(expectedConstraint, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, ReadTaskCount(database.Connection));
    }

    private static List<string> ReadTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static int ReadTaskCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tasks";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void InsertRaw(SqliteConnection connection, RawTaskRow row)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks
                (id, title, time_type, local_date, time_point, range_start, range_end,
                 result, result_recorded_at, result_note, created_at, updated_at)
            VALUES
                ($id, $title, $time_type, $local_date, $time_point, $range_start, $range_end,
                 $result, $result_recorded_at, $result_note, $created_at, $updated_at)
            """;

        AddParameter(command, "$id", row.Id);
        AddParameter(command, "$title", row.Title);
        AddParameter(command, "$time_type", row.TimeType);
        AddParameter(command, "$local_date", row.LocalDate);
        AddParameter(command, "$time_point", row.TimePoint);
        AddParameter(command, "$range_start", row.RangeStart);
        AddParameter(command, "$range_end", row.RangeEnd);
        AddParameter(command, "$result", row.Result);
        AddParameter(command, "$result_recorded_at", row.ResultRecordedAt);
        AddParameter(command, "$result_note", row.ResultNote);
        AddParameter(command, "$created_at", row.CreatedAt);
        AddParameter(command, "$updated_at", row.UpdatedAt);
        command.ExecuteNonQuery();
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static RawTaskRow CreateValidRawRow() => new(
        Id: TestValues.TaskId().Value.ToString("D"),
        Title: "合法原始任务",
        TimeType: 0,
        LocalDate: LocalDatePattern.Format(TestValues.PlanDate),
        TimePoint: null,
        RangeStart: null,
        RangeEnd: null,
        Result: null,
        ResultRecordedAt: null,
        ResultNote: null,
        CreatedAt: FormatInstant(TestValues.CreatedAt),
        UpdatedAt: FormatInstant(TestValues.ChangedAt));

    private static string FormatInstant(NodaTime.Instant instant) => InstantPattern.Format(instant);

    private sealed record RawTaskRow(
        string Id,
        string Title,
        int TimeType,
        string LocalDate,
        long? TimePoint,
        long? RangeStart,
        long? RangeEnd,
        int? Result,
        string? ResultRecordedAt,
        string? ResultNote,
        string CreatedAt,
        string UpdatedAt);
}
