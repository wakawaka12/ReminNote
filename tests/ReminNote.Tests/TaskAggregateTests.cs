using ReminNote.Core.Tasks;

namespace ReminNote.Tests;

public sealed class TaskAggregateTests
{
    [Fact]
    public void CreateAssignsUuidV7AndStartsWithoutAResult()
    {
        var task = ReminNote.Core.Tasks.Task.Create(
            "  整理测试夹具  ",
            TimeSpec.Anytime(TestValues.PlanDate),
            TestValues.CreatedAt);

        TestValues.AssertUuidV7(task.Id.Value);
        Assert.Equal("整理测试夹具", task.Title);
        Assert.Equal(TestValues.CreatedAt, task.CreatedAt);
        Assert.Equal(TestValues.CreatedAt, task.UpdatedAt);
        Assert.Null(task.ResultRecord);
        Assert.Null(task.Result);
    }

    [Fact]
    public void CreateAndRehydratePreserveStableIdentityAndSnapshotValues()
    {
        var id = TestValues.TaskId();
        var timeSpec = TimeSpec.At(TestValues.PlanDate, TestValues.Afternoon);
        var result = TaskResultRecord.Create(TaskResult.COMPLETED, TestValues.ChangedAt);

        var task = ReminNote.Core.Tasks.Task.Rehydrate(
            id,
            "读取记录",
            timeSpec,
            TestValues.CreatedAt,
            TestValues.ChangedAt,
            result);
        var snapshot = task.ToSnapshot();

        Assert.Equal(id, task.Id);
        Assert.Equal(id, snapshot.Id);
        Assert.Equal("读取记录", snapshot.Title);
        Assert.Equal(timeSpec, snapshot.TimeSpec);
        Assert.Equal(result, snapshot.ResultRecord);
        Assert.Equal((TaskResult?)TaskResult.COMPLETED, snapshot.Result);
        Assert.Equal(TestValues.ChangedAt, snapshot.UpdatedAt);
    }

    [Fact]
    public void RenameTrimsTitleAndUpdatesChangedTimestamp()
    {
        var task = CreateTask();

        task.Rename("  新标题  ", TestValues.ChangedAt);

        Assert.Equal("新标题", task.Title);
        Assert.Equal(TestValues.ChangedAt, task.UpdatedAt);
    }

    [Fact]
    public void ChangeTimeReplacesThePlannedShapeAndUpdatesChangedTimestamp()
    {
        var task = CreateTask();
        var replacement = TimeSpec.Range(TestValues.PlanDate, new LocalTime(23, 0), new LocalTime(1, 0));

        task.ChangeTime(replacement, TestValues.ChangedAt);

        Assert.Equal(replacement, task.TimeSpec);
        Assert.Equal(TestValues.ChangedAt, task.UpdatedAt);
    }

    [Fact]
    public void RecordResultPersistsResultNoteAndRecordedAt()
    {
        var task = CreateTask(TimeSpec.Range(TestValues.PlanDate, new LocalTime(23, 0), new LocalTime(1, 0)));

        task.RecordResult(TaskResult.PARTIAL, TestValues.ChangedAt, "做到一半");

        Assert.Equal((TaskResult?)TaskResult.PARTIAL, task.Result);
        Assert.Equal("做到一半", task.ResultNote);
        Assert.NotNull(task.ResultRecord);
        Assert.Equal(TestValues.ChangedAt, task.ResultRecord!.RecordedAt);
        Assert.Equal(TestValues.ChangedAt, task.UpdatedAt);
    }

    [Fact]
    public void RecordResultRejectsUnknownResultValuesBeforeMutatingTheTask()
    {
        var task = CreateTask();

        TestValues.AssertValidationCode(
            () => task.RecordResult((TaskResult)99, TestValues.ChangedAt),
            "task.result.invalid");

        Assert.Null(task.ResultRecord);
        Assert.Equal(TestValues.CreatedAt, task.UpdatedAt);
    }

    [Theory]
    [InlineData(TaskResult.COMPLETED)]
    [InlineData(TaskResult.MISSED)]
    public void AnyTimeAndTimeTasksAcceptCompletedOrMissedResults(TaskResult result)
    {
        var anytime = CreateTask(TimeSpec.Anytime(TestValues.PlanDate));
        var time = CreateTask(TimeSpec.At(TestValues.PlanDate, TestValues.Afternoon));

        anytime.RecordResult(result, TestValues.ChangedAt);
        time.RecordResult(result, TestValues.ChangedAt);

        Assert.Equal((TaskResult?)result, anytime.Result);
        Assert.Equal((TaskResult?)result, time.Result);
    }

    [Fact]
    public void PartialResultIsRejectedForNonRangeTasks()
    {
        var anytime = CreateTask(TimeSpec.Anytime(TestValues.PlanDate));

        TestValues.AssertValidationCode(
            () => anytime.RecordResult(TaskResult.PARTIAL, TestValues.ChangedAt),
            "task.result.partial_requires_range");

        Assert.Null(anytime.ResultRecord);
    }

    [Fact]
    public void RehydrateAndChangeTimeRejectPartialResultsForNonRangeTasks()
    {
        var partial = TaskResultRecord.Create(TaskResult.PARTIAL, TestValues.ChangedAt);

        TestValues.AssertValidationCode(
            () => ReminNote.Core.Tasks.Task.Rehydrate(
                TestValues.TaskId(),
                "非法部分结果",
                TimeSpec.Anytime(TestValues.PlanDate),
                TestValues.CreatedAt,
                TestValues.ChangedAt,
                partial),
            "task.result.partial_requires_range");

        var rangeTask = CreateTask(TimeSpec.Range(TestValues.PlanDate, new LocalTime(14, 0), new LocalTime(15, 0)));
        rangeTask.RecordResult(TaskResult.PARTIAL, TestValues.ChangedAt);

        TestValues.AssertValidationCode(
            () => rangeTask.ChangeTime(TimeSpec.Anytime(TestValues.PlanDate), TestValues.ChangedAt),
            "task.result.partial_requires_range");

        Assert.Equal(TaskTimeType.RANGE, rangeTask.TimeSpec.Type);
        Assert.Equal((TaskResult?)TaskResult.PARTIAL, rangeTask.Result);
    }

    [Fact]
    public void RenameBeforeCreationIsRejectedWithoutMutatingAnyTaskState()
    {
        var task = CreateTask();
        var originalTitle = task.Title;
        var originalTimeSpec = task.TimeSpec;
        var originalResult = task.ResultRecord;
        var earlier = TestValues.CreatedAt.Minus(Duration.FromSeconds(1));

        TestValues.AssertValidationCode(
            () => task.Rename("新标题", earlier),
            "task.changed_at.before_created_at");

        Assert.Equal(originalTitle, task.Title);
        Assert.Equal(originalTimeSpec, task.TimeSpec);
        Assert.Equal(originalResult, task.ResultRecord);
        Assert.Equal(TestValues.CreatedAt, task.UpdatedAt);
    }

    [Fact]
    public void InvalidTitlesAreRejectedWithoutMutatingTheTask()
    {
        var task = CreateTask();

        TestValues.AssertValidationCode(
            () => task.Rename(" \t ", TestValues.ChangedAt),
            "task.title.required");

        Assert.Equal("测试任务", task.Title);
        Assert.Equal(TestValues.CreatedAt, task.UpdatedAt);
    }

    [Fact]
    public void RecordResultBeforeCreationIsRejectedWithoutMutatingAnyTaskState()
    {
        var task = CreateTask();
        var originalTitle = task.Title;
        var originalTimeSpec = task.TimeSpec;
        var originalResult = task.ResultRecord;
        var earlier = TestValues.CreatedAt.Minus(Duration.FromSeconds(1));

        TestValues.AssertValidationCode(
            () => task.RecordResult(TaskResult.COMPLETED, earlier),
            "task.changed_at.before_created_at");

        Assert.Equal(originalTitle, task.Title);
        Assert.Equal(originalTimeSpec, task.TimeSpec);
        Assert.Equal(originalResult, task.ResultRecord);
        Assert.Equal(TestValues.CreatedAt, task.UpdatedAt);
    }

    [Fact]
    public void ChangeTimeBeforeCreationIsRejectedWithoutMutatingTheTask()
    {
        var task = CreateTask();
        var originalTimeSpec = task.TimeSpec;
        var originalTitle = task.Title;
        var originalResult = task.ResultRecord;
        var earlier = TestValues.CreatedAt.Minus(Duration.FromSeconds(1));

        TestValues.AssertValidationCode(
            () => task.ChangeTime(TimeSpec.At(TestValues.PlanDate, TestValues.Afternoon), earlier),
            "task.changed_at.before_created_at");

        Assert.Equal(originalTimeSpec, task.TimeSpec);
        Assert.Equal(originalTitle, task.Title);
        Assert.Equal(originalResult, task.ResultRecord);
        Assert.Equal(TestValues.CreatedAt, task.UpdatedAt);
    }

    [Fact]
    public void RehydrateRejectsUpdatedAtBeforeCreatedAt()
    {
        TestValues.AssertValidationCode(
            () => ReminNote.Core.Tasks.Task.Rehydrate(
                TestValues.TaskId(),
                "时间不合法",
                TimeSpec.Anytime(TestValues.PlanDate),
                TestValues.CreatedAt,
                TestValues.CreatedAt.Minus(Duration.FromSeconds(1)),
                null),
            "task.updated_at.before_created_at");
    }

    private static ReminNote.Core.Tasks.Task CreateTask(TimeSpec? timeSpec = null) =>
        ReminNote.Core.Tasks.Task.Create(
            TestValues.TaskId(),
            "测试任务",
            timeSpec ?? TimeSpec.Anytime(TestValues.PlanDate),
            TestValues.CreatedAt);
}
