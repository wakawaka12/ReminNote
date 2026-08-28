using ReminNote.Core.Tasks;

namespace ReminNote.Tests;

public sealed class TaskAggregateTests
{
    [Fact]
    public void Create_assigns_uuid_v7_and_starts_without_a_result()
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
    public void Create_and_rehydrate_preserve_stable_identity_and_snapshot_values()
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
    public void Rename_trims_title_and_updates_changed_timestamp()
    {
        var task = CreateTask();

        task.Rename("  新标题  ", TestValues.ChangedAt);

        Assert.Equal("新标题", task.Title);
        Assert.Equal(TestValues.ChangedAt, task.UpdatedAt);
    }

    [Fact]
    public void Change_time_replaces_the_planned_shape_and_updates_changed_timestamp()
    {
        var task = CreateTask();
        var replacement = TimeSpec.Range(TestValues.PlanDate, new LocalTime(23, 0), new LocalTime(1, 0));

        task.ChangeTime(replacement, TestValues.ChangedAt);

        Assert.Equal(replacement, task.TimeSpec);
        Assert.Equal(TestValues.ChangedAt, task.UpdatedAt);
    }

    [Fact]
    public void Record_result_persists_result_note_and_recorded_at()
    {
        var task = CreateTask(TimeSpec.Range(TestValues.PlanDate, new LocalTime(23, 0), new LocalTime(1, 0)));

        task.RecordResult(TaskResult.PARTIAL, TestValues.ChangedAt, "做到一半");

        Assert.Equal(TaskResult.PARTIAL, task.Result);
        Assert.Equal("做到一半", task.ResultNote);
        Assert.NotNull(task.ResultRecord);
        Assert.Equal(TestValues.ChangedAt, task.ResultRecord!.RecordedAt);
        Assert.Equal(TestValues.ChangedAt, task.UpdatedAt);
    }

    [Fact]
    public void Record_result_rejects_unknown_result_values_before_mutating_the_task()
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
    public void Any_time_and_time_tasks_accept_completed_or_missed_results(TaskResult result)
    {
        var anytime = CreateTask(TimeSpec.Anytime(TestValues.PlanDate));
        var time = CreateTask(TimeSpec.At(TestValues.PlanDate, TestValues.Afternoon));

        anytime.RecordResult(result, TestValues.ChangedAt);
        time.RecordResult(result, TestValues.ChangedAt);

        Assert.Equal((TaskResult?)result, anytime.Result);
        Assert.Equal((TaskResult?)result, time.Result);
    }

    [Fact]
    public void Partial_result_is_rejected_for_non_range_tasks()
    {
        var anytime = CreateTask(TimeSpec.Anytime(TestValues.PlanDate));

        TestValues.AssertValidationCode(
            () => anytime.RecordResult(TaskResult.PARTIAL, TestValues.ChangedAt),
            "task.result.partial_requires_range");

        Assert.Null(anytime.ResultRecord);
    }

    [Fact]
    public void Rehydrate_and_change_time_reject_partial_results_for_non_range_tasks()
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
    public void Rename_before_creation_is_rejected_without_mutating_any_task_state()
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
    public void Invalid_titles_are_rejected_without_mutating_the_task()
    {
        var task = CreateTask();

        TestValues.AssertValidationCode(
            () => task.Rename(" \t ", TestValues.ChangedAt),
            "task.title.required");

        Assert.Equal("测试任务", task.Title);
        Assert.Equal(TestValues.CreatedAt, task.UpdatedAt);
    }

    [Fact]
    public void Record_result_before_creation_is_rejected_without_mutating_any_task_state()
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
    public void Change_time_before_creation_is_rejected_without_mutating_the_task()
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
    public void Rehydrate_rejects_updated_at_before_created_at()
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
