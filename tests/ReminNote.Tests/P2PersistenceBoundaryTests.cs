using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;
using ReminNote.Core;
using ReminNote.Core.Application;
using ReminNote.Core.Tasks;
using ReminNote.Core.Time;
using ReminNote.Infrastructure.Application;
using ReminNote.Infrastructure.Persistence;
using SystemTask = System.Threading.Tasks.Task;
using TaskAggregate = ReminNote.Core.Tasks.Task;

namespace ReminNote.Tests;

public sealed class P2PersistenceBoundaryTests
{
    private static readonly LocalDate PlanDate = new(2026, 8, 28);

    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'");

    [Fact]
    public async SystemTask P1DataUpgradesPreservingLegacyTaskAndBackfilledResultHistory()
    {
        using var database = new SqliteTestDatabase();
        database.MigrateToInitial();

        var taskId = TestValues.TaskId();
        var legacyTitle = new string('旧', 501);
        InsertP1Row(database.Connection, taskId, legacyTitle, withPartialResult: true);

        database.Migrate();

        using var context = database.CreateContext();
        var task = await new TaskRepository(context).FindAsync(
            taskId,
            TestContext.Current.CancellationToken);
        var history = await ReadAllAsync(new TaskHistoryQueryService(context).ListAsync(
            taskId,
            TestContext.Current.CancellationToken));
        var settings = await new AppSettingsRepository(
                context,
                new TestClock(TestValues.ChangedAt))
            .GetAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(task);
        Assert.Equal(legacyTitle, task!.Title);
        Assert.Equal(taskId, task.Id);
        Assert.Equal(TaskResult.PARTIAL, task.Result);
        Assert.Equal(0, task.SortOrder);
        Assert.Null(task.ContinuedFromTaskId);
        Assert.Single(history);
        Assert.Equal(TaskHistoryKind.ImportedResult, history[0].Kind);
        Assert.Equal(taskId, history[0].TaskId);
        Assert.Equal(task.ToSnapshot(), history[0].Snapshot);
        Assert.Equal(WorkdaySettings.Default, settings);
    }

    [Fact]
    public async SystemTask LegacyLongTitleCanBeRescheduledWithoutTruncation()
    {
        using var database = new SqliteTestDatabase();
        database.MigrateToInitial();
        var taskId = TestValues.TaskId();
        var legacyTitle = new string('旧', 501);
        InsertP1Row(database.Connection, taskId, legacyTitle, withPartialResult: false);
        database.Migrate();

        var clock = new TestClock(TestValues.ChangedAt);
        using (var context = database.CreateContext())
        {
            var updated = await CreateApplication(context, clock).UpdateAsync(
                new UpdateTaskCommand(
                    taskId,
                    $"  {legacyTitle}  ",
                    TimeSpec.At(PlanDate, new LocalTime(10, 0))),
                TestContext.Current.CancellationToken);

            Assert.NotNull(updated);
            Assert.Equal(legacyTitle, updated!.Title);
            Assert.Equal(TaskTimeType.TIME, updated.TimeType);
        }

        using (var context = database.CreateContext())
        {
            var history = await ReadAllAsync(new TaskHistoryQueryService(context).ListAsync(
                taskId,
                TestContext.Current.CancellationToken));
            Assert.Single(history);
            Assert.Equal(TaskHistoryKind.PlanChanged, history[0].Kind);
            Assert.Equal(legacyTitle, history[0].Snapshot.Title);
        }
    }

    [Fact]
    public void FailedUpgradeRollsBackTheP1TableRewriteAndLeavesNoHalfSchema()
    {
        using var database = new SqliteTestDatabase();
        database.MigrateToInitial();
        InsertP1Row(database.Connection, TestValues.TaskId(), "升级失败前的任务", withPartialResult: false);

        ExecuteNonQuery(
            database.Connection,
            "CREATE TABLE app_settings (id INTEGER PRIMARY KEY)");

        Assert.Throws<SqliteException>(() => database.Migrate());

        Assert.Equal(1, ReadTaskCount(database.Connection));
        Assert.DoesNotContain("sort_order", ReadColumnNames(database.Connection, "tasks"));
        Assert.DoesNotContain("continued_from_task_id", ReadColumnNames(database.Connection, "tasks"));
        Assert.DoesNotContain("task_history", ReadTables(database.Connection));
        Assert.Equal(1, ReadMigrationCount(database.Connection));
        Assert.Contains("app_settings", ReadTables(database.Connection));
    }

    [Fact]
    public void DowngradeRestoresTheP1TaskShapeAndRemovesP2OnlyTables()
    {
        using var database = new SqliteTestDatabase();
        database.MigrateToInitial();
        InsertP1Row(database.Connection, TestValues.TaskId(), "可回滚的旧任务", withPartialResult: true);
        database.Migrate();

        database.MigrateToInitial();

        var tables = ReadTables(database.Connection);
        Assert.Contains("tasks", tables);
        Assert.DoesNotContain("app_settings", tables);
        Assert.DoesNotContain("task_history", tables);
        Assert.DoesNotContain("sort_order", ReadColumnNames(database.Connection, "tasks"));
        Assert.DoesNotContain("continued_from_task_id", ReadColumnNames(database.Connection, "tasks"));
        Assert.Equal(1, ReadTaskCount(database.Connection));
        Assert.Equal(1, ReadMigrationCount(database.Connection));
    }

    [Fact]
    public async SystemTask ApplicationPersistsMeaningfulHistoryForPlanResultAndReorder()
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();
        var clock = new TestClock(TestValues.CreatedAt);
        var cancellationToken = TestContext.Current.CancellationToken;
        TaskSnapshot created;

        using (var context = database.CreateContext())
        {
            created = await CreateApplication(context, clock).CreateAsync(
                new CreateTaskCommand(
                    "历史边界任务",
                    TimeSpec.Anytime(PlanDate)),
                cancellationToken);
        }

        clock.Current = TestValues.ChangedAt;
        TaskSnapshot updated;
        using (var context = database.CreateContext())
        {
            updated = (await CreateApplication(context, clock).UpdateAsync(
                new UpdateTaskCommand(
                    created.Id,
                    "历史边界任务改期",
                    TimeSpec.At(PlanDate, new LocalTime(9, 0))),
                cancellationToken))!;
        }

        Assert.Equal("历史边界任务改期", updated.Title);
        Assert.Equal(TaskTimeType.TIME, updated.TimeType);
        clock.Current = TestValues.ChangedAt.Plus(Duration.FromMinutes(5));
        TaskSnapshot recorded;
        using (var context = database.CreateContext())
        {
            recorded = (await CreateApplication(context, clock).RecordResultAsync(
                new RecordTaskResultCommand(created.Id, TaskResult.COMPLETED, "已完成"),
                cancellationToken))!;
        }

        clock.Current = TestValues.ChangedAt.Plus(Duration.FromMinutes(10));
        TaskSnapshot reordered;
        using (var context = database.CreateContext())
        {
            reordered = (await CreateApplication(context, clock).ReorderAsync(
                new ReorderTaskCommand(created.Id, 7),
                cancellationToken))!;
        }

        using var readContext = database.CreateContext();
        var history = await ReadAllAsync(new TaskHistoryQueryService(readContext).ListAsync(
            created.Id,
            cancellationToken));

        Assert.Equal(7, reordered.SortOrder);
        Assert.Equal(
            new[]
            {
                TaskHistoryKind.PlanChanged,
                TaskHistoryKind.ResultRecorded,
                TaskHistoryKind.SortOrderChanged
            },
            history.Select(entry => entry.Kind));
        Assert.Equal(created.TimeSpec, history[0].Snapshot.TimeSpec);
        Assert.Equal("历史边界任务", history[0].Snapshot.Title);
        Assert.Equal(created.UpdatedAt, history[0].Snapshot.UpdatedAt);
        Assert.Null(history[0].Snapshot.Result);
        Assert.Equal(recorded.ResultRecord, history[1].Snapshot.ResultRecord);
        Assert.Equal(7, history[2].Snapshot.SortOrder);
    }

    [Fact]
    public async SystemTask ContinuationIsOnlyCreatedFromAPartialRangeAndIsWrittenWithHistory()
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();
        var clock = new TestClock(TestValues.CreatedAt);
        var cancellationToken = TestContext.Current.CancellationToken;
        TaskSnapshot source;

        using (var context = database.CreateContext())
        {
            source = await CreateApplication(context, clock).CreateAsync(
                new CreateTaskCommand(
                    "可继续的范围任务",
                    TimeSpec.Range(PlanDate, new LocalTime(23, 0), new LocalTime(1, 0))),
                cancellationToken);
        }

        clock.Current = TestValues.ChangedAt;
        using (var context = database.CreateContext())
        {
            source = (await CreateApplication(context, clock).RecordResultAsync(
                new RecordTaskResultCommand(source.Id, TaskResult.PARTIAL, "明天继续"),
                cancellationToken))!;
        }

        clock.Current = TestValues.ChangedAt.Plus(Duration.FromMinutes(10));
        TaskSnapshot continued;
        using (var context = database.CreateContext())
        {
            continued = (await CreateApplication(context, clock).ContinueAsync(
                new ContinueTaskCommand(
                    source.Id,
                    "继续处理",
                    TimeSpec.Range(PlanDate.PlusDays(1), new LocalTime(9, 0), new LocalTime(10, 0))),
                cancellationToken))!;
        }

        Assert.Equal(source.Id, continued.ContinuedFromTaskId);
        using (var context = database.CreateContext())
        {
            var history = await ReadAllAsync(new TaskHistoryQueryService(context).ListAsync(
                continued.Id,
                cancellationToken));

            Assert.Single(history);
            Assert.Equal(TaskHistoryKind.Continued, history[0].Kind);
            Assert.Equal(source.Id, history[0].RelatedTaskId);
            Assert.Equal(source.Id, history[0].Snapshot.ContinuedFromTaskId);
        }

        TaskSnapshot completed;
        clock.Current = TestValues.ChangedAt.Plus(Duration.FromMinutes(20));
        using (var context = database.CreateContext())
        {
            completed = await CreateApplication(context, clock).CreateAsync(
                new CreateTaskCommand("不可继续的任意任务", TimeSpec.Anytime(PlanDate)),
                cancellationToken);
            clock.Current = TestValues.ChangedAt.Plus(Duration.FromMinutes(25));
            completed = (await CreateApplication(context, clock).RecordResultAsync(
                new RecordTaskResultCommand(completed.Id, TaskResult.COMPLETED),
                cancellationToken))!;
        }

        var taskCountBeforeRejectedContinuation = ReadTaskCount(database.Connection);
        clock.Current = TestValues.ChangedAt.Plus(Duration.FromMinutes(30));
        var exception = await Assert.ThrowsAsync<DomainValidationException>(async () =>
        {
            using var context = database.CreateContext();
            await CreateApplication(context, clock).ContinueAsync(
                new ContinueTaskCommand(
                    completed.Id,
                    "不应出现的继续任务",
                    TimeSpec.Anytime(PlanDate)),
                cancellationToken);
        });

        Assert.Contains(exception.Errors, error => error.Code == "task.continuation.requires_partial");
        Assert.Equal(taskCountBeforeRejectedContinuation, ReadTaskCount(database.Connection));
    }

    [Fact]
    public async SystemTask RepositoryRollsBackTaskAndHistoryTogetherWhenForeignKeyValidationFails()
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();
        var cancellationToken = TestContext.Current.CancellationToken;
        var original = TaskAggregate.Create(
            TestValues.TaskId(),
            "原始任务",
            TimeSpec.Anytime(PlanDate),
            TestValues.CreatedAt);

        using (var context = database.CreateContext())
        {
            await new TaskRepository(context).AddAsync(original, cancellationToken);
        }

        var missingSource = TestValues.TaskId("0191f6a4-3b25-7c12-8d34-56789abcde99");
        var candidate = TaskAggregate.Rehydrate(
            original.Id,
            "不应写入的标题",
            original.TimeSpec,
            original.CreatedAt,
            TestValues.ChangedAt,
            resultRecord: null,
            sortOrder: 0,
            continuedFromTaskId: missingSource);
        var invalidHistory = new TaskHistoryRecord(
            original.Id,
            TaskHistoryKind.Continued,
            TestValues.ChangedAt,
            candidate.ToSnapshot(),
            missingSource);

        using (var context = database.CreateContext())
        {
            var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await new TaskRepository(context).UpdateAsync(
                    candidate,
                    invalidHistory,
                    cancellationToken));

            Assert.True(
                exception is DbUpdateException or SqliteException,
                $"Expected a SQLite/EF persistence failure, got {exception.GetType().FullName}.");
            Assert.Empty(context.ChangeTracker.Entries());
        }

        using (var context = database.CreateContext())
        {
            var persisted = await new TaskRepository(context).FindAsync(
                original.Id,
                cancellationToken);
            var historyCount = await context.TaskHistory.CountAsync(cancellationToken);

            Assert.NotNull(persisted);
            Assert.Equal(original.ToSnapshot(), persisted!.ToSnapshot());
            Assert.Equal(0, historyCount);
        }
    }

    [Fact]
    public async SystemTask ReorderRejectsNegativeValuesAndKeepsStableNonNegativeOrdering()
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = TaskAggregate.Create(
            TestValues.TaskId(),
            "同序一",
            TimeSpec.At(PlanDate, new LocalTime(14, 0)),
            TestValues.CreatedAt,
            sortOrder: 3);
        var second = TaskAggregate.Create(
            TestValues.AnotherTaskId(),
            "同序二",
            TimeSpec.At(PlanDate, new LocalTime(14, 0)),
            TestValues.CreatedAt,
            sortOrder: 3);

        using (var context = database.CreateContext())
        {
            var repository = new TaskRepository(context);
            await repository.AddAsync(first, cancellationToken);
            await repository.AddAsync(second, cancellationToken);
        }

        using (var context = database.CreateContext())
        {
            var query = new TaskQueryService(context);
            var ordered = await ReadAllAsync(query.ListAsync(new TaskQuery(PlanDate), cancellationToken));
            Assert.Equal(new[] { first.Id, second.Id }, ordered.Select(task => task.Id));
        }

        var exception = await Assert.ThrowsAsync<DomainValidationException>(async () =>
        {
            using var context = database.CreateContext();
            await CreateApplication(context, new TestClock(TestValues.ChangedAt)).ReorderAsync(
                new ReorderTaskCommand(first.Id, -1),
                cancellationToken);
        });
        Assert.Contains(exception.Errors, error => error.Code == "task.sort_order.invalid");

        using (var context = database.CreateContext())
        {
            var persisted = await new TaskRepository(context).FindAsync(first.Id, cancellationToken);
            Assert.NotNull(persisted);
            Assert.Equal(3, persisted!.SortOrder);
        }
    }

    [Fact]
    public async SystemTask DatabaseRejectsRawContinuationUnlessTheSourceIsPartialRange()
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();

        var nonPartialSource = TaskAggregate.Create(
            TestValues.TaskId(),
            "不可作为继续源",
            TimeSpec.Anytime(PlanDate),
            TestValues.CreatedAt);
        using (var context = database.CreateContext())
        {
            await new TaskRepository(context).AddAsync(
                nonPartialSource,
                TestContext.Current.CancellationToken);
        }

        var rejectedChildId = TestValues.TaskId("0191f6a4-3b25-7c12-8d34-56789abcde03");
        var rejection = Assert.Throws<SqliteException>(() => InsertRawContinuation(
            database.Connection,
            rejectedChildId,
            nonPartialSource.Id));
        Assert.Contains("PARTIAL RANGE", rejection.Message, StringComparison.Ordinal);

        var partialSource = TaskAggregate.Create(
            TestValues.TaskId("0191f6a4-3b25-7c12-8d34-56789abcde04"),
            "可作为继续源",
            TimeSpec.Range(PlanDate, new LocalTime(23, 0), new LocalTime(1, 0)),
            TestValues.CreatedAt);
        partialSource.RecordResult(TaskResult.PARTIAL, TestValues.ChangedAt, "继续");
        using (var context = database.CreateContext())
        {
            await new TaskRepository(context).AddAsync(
                partialSource,
                TestContext.Current.CancellationToken);
        }

        var acceptedChildId = TestValues.TaskId("0191f6a4-3b25-7c12-8d34-56789abcde05");
        InsertRawContinuation(database.Connection, acceptedChildId, partialSource.Id);

        using (var context = database.CreateContext())
        {
            var child = await new TaskRepository(context).FindAsync(
                acceptedChildId,
                TestContext.Current.CancellationToken);
            Assert.NotNull(child);
            Assert.Equal(partialSource.Id, child!.ContinuedFromTaskId);
        }
    }

    [Fact]
    public async SystemTask AppSettingsPersistOneValidatedWorkdayBoundaryRow()
    {
        using var database = new SqliteTestDatabase();
        database.Migrate();
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestClock(TestValues.ChangedAt);

        using (var context = database.CreateContext())
        {
            var repository = new AppSettingsRepository(context, clock);
            Assert.Equal(WorkdaySettings.Default, await repository.GetAsync(cancellationToken));
            await repository.SetAsync(new WorkdaySettings(23 * 60 + 59), cancellationToken);
        }

        using (var context = database.CreateContext())
        {
            var repository = new AppSettingsRepository(context, clock);
            var settings = await repository.GetAsync(cancellationToken);
            Assert.Equal(23 * 60 + 59, settings.BoundaryMinutes);
        }

        Assert.Equal(1, ReadScalarInt(database.Connection, "SELECT COUNT(*) FROM app_settings"));
        TestValues.AssertValidationCode(
            () => _ = new WorkdaySettings(1_440),
            "workday.boundary.out_of_range");
        TestValues.AssertValidationCode(
            () => _ = new WorkdaySettings(new LocalTime(12, 0, 1)),
            "workday.boundary.minute_precision");
    }

    [Fact]
    public async SystemTask WorkspaceRequiresSuccessfulInitializationAndUsesTheExplicitDevelopmentPath()
    {
        var repositoryRoot = Path.Combine(
            Path.GetTempPath(),
            $"ReminNote-P2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
        File.WriteAllText(Path.Combine(repositoryRoot, "ReminNote.sln"), string.Empty);

        var workspace = new TaskWorkspace(
            repositoryRoot,
            new TestClock(TestValues.CreatedAt),
            new SystemUserTimeZoneProvider(DateTimeZone.Utc),
            new NoopTaskWriteGate());
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await workspace.FindAsync(TestValues.TaskId(), TestContext.Current.CancellationToken));

            workspace.Initialize();
            workspace.Initialize();
            var created = await workspace.CreateAsync(
                new CreateTaskCommand("工作区任务", TimeSpec.Anytime(PlanDate)),
                TestContext.Current.CancellationToken);
            var found = await workspace.FindAsync(created.Id, TestContext.Current.CancellationToken);

            Assert.Equal(created, found);
            await workspace.SetAsync(
                new WorkdaySettings(60),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                60,
                (await workspace.GetAsync(TestContext.Current.CancellationToken)).BoundaryMinutes);
            Assert.Equal(
                ReminNoteDatabase.GetDevelopmentDatabasePath(repositoryRoot),
                Path.Combine(repositoryRoot, ".devdata", "reminnote.sqlite"));
        }
        finally
        {
            workspace.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(repositoryRoot))
            {
                Directory.Delete(repositoryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async SystemTask CrossProcessWriteGateReportsBusyAndHonorsCancellation()
    {
        var name = $"Local\\ReminNote.Tests.{Guid.NewGuid():N}";
        using var first = new CrossProcessTaskWriteGate(name, timeoutMilliseconds: 100);
        using var second = new CrossProcessTaskWriteGate(name, timeoutMilliseconds: 100);
        using var lease = first.Enter(TestContext.Current.CancellationToken);

        var busyAttempt = SystemTask.Run(() => second.Enter(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TaskWriteGateBusyException>(async () => await busyAttempt);

        using var cancellation = new CancellationTokenSource();
        var waiting = SystemTask.Run(() => second.Enter(cancellation.Token));
        cancellation.CancelAfter(50);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting);
    }

    [Fact]
    public void HistoryContractRejectsMismatchedAndArbitraryContinuationMetadata()
    {
        var task = TaskAggregate.Create(
            TestValues.TaskId(),
            "历史契约任务",
            TimeSpec.Anytime(PlanDate),
            TestValues.CreatedAt);
        var other = TaskAggregate.Create(
            TestValues.AnotherTaskId(),
            "另一任务",
            TimeSpec.Anytime(PlanDate),
            TestValues.CreatedAt);

        TestValues.AssertValidationCode(
            () => _ = new TaskHistoryRecord(
                task.Id,
                TaskHistoryKind.ResultRecorded,
                TestValues.ChangedAt,
                other.ToSnapshot()),
            "task.history.task_id_mismatch");

        TestValues.AssertValidationCode(
            () => _ = new TaskHistoryRecord(
                task.Id,
                TaskHistoryKind.ResultRecorded,
                TestValues.ChangedAt,
                task.ToSnapshot(),
                other.Id),
            "task.history.related_task.unexpected");
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

    private static void InsertP1Row(
        SqliteConnection connection,
        TaskId id,
        string title,
        bool withPartialResult)
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

        var recordedAt = FormatInstant(TestValues.ChangedAt);
        AddParameter(command, "$id", id.Value.ToString("D"));
        AddParameter(command, "$title", title);
        AddParameter(command, "$time_type", withPartialResult ? 2 : 0);
        AddParameter(command, "$local_date", PlanDate.ToString("uuuu-MM-dd", null));
        AddParameter(command, "$time_point", null);
        AddParameter(command, "$range_start", withPartialResult ? 23L * 60 * 60 * 1_000_000_000 : null);
        AddParameter(command, "$range_end", withPartialResult ? 1L * 60 * 60 * 1_000_000_000 : null);
        AddParameter(command, "$result", withPartialResult ? 2 : null);
        AddParameter(command, "$result_recorded_at", withPartialResult ? recordedAt : null);
        AddParameter(command, "$result_note", withPartialResult ? "旧结果" : null);
        AddParameter(command, "$created_at", FormatInstant(TestValues.CreatedAt));
        AddParameter(command, "$updated_at", recordedAt);
        command.ExecuteNonQuery();
    }

    private static void InsertRawContinuation(
        SqliteConnection connection,
        TaskId childId,
        TaskId sourceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks
                (id, title, time_type, local_date, time_point, range_start, range_end,
                 result, result_recorded_at, result_note, created_at, updated_at,
                 sort_order, continued_from_task_id)
            VALUES
                ($id, $title, 0, $local_date, NULL, NULL, NULL,
                 NULL, NULL, NULL, $created_at, $updated_at, 0, $source_id)
            """;
        AddParameter(command, "$id", childId.Value.ToString("D"));
        AddParameter(command, "$title", "原始继续任务");
        AddParameter(command, "$local_date", PlanDate.ToString("uuuu-MM-dd", null));
        AddParameter(command, "$created_at", FormatInstant(TestValues.ChangedAt));
        AddParameter(command, "$updated_at", FormatInstant(TestValues.ChangedAt));
        AddParameter(command, "$source_id", sourceId.Value.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static string FormatInstant(Instant instant) => InstantPattern.Format(instant);

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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

    private static List<string> ReadColumnNames(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info([{tableName}])";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static int ReadTaskCount(SqliteConnection connection) =>
        ReadScalarInt(connection, "SELECT COUNT(*) FROM tasks");

    private static int ReadMigrationCount(SqliteConnection connection) =>
        ReadScalarInt(connection, "SELECT COUNT(*) FROM __EFMigrationsHistory");

    private static int ReadScalarInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class TestClock(Instant current) : IClock
    {
        public Instant Current { get; set; } = current;

        public Instant GetCurrentInstant() => Current;
    }
}
