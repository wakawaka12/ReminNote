using ReminNote.Core.Tasks;

namespace ReminNote.Tests;

public sealed class TaskIdTests
{
    [Fact]
    public void New_creates_uuid_v7_identity()
    {
        var taskId = TaskId.New();

        TestValues.AssertUuidV7(taskId.Value);
    }

    [Fact]
    public void From_and_parse_preserve_uuid_v7_identity()
    {
        var guid = Guid.CreateVersion7();

        var from = TaskId.From(guid);
        var parsed = TaskId.Parse(guid.ToString("D"));

        Assert.Equal(guid, from.Value);
        Assert.Equal(from, parsed);
    }

    [Fact]
    public void TryParse_returns_false_for_null_empty_and_non_uuid_v7_values()
    {
        Assert.False(TaskId.TryParse(null, out _));
        Assert.False(TaskId.TryParse(string.Empty, out _));
        Assert.False(TaskId.TryParse(Guid.Empty.ToString("D"), out _));
        Assert.False(TaskId.TryParse(Guid.NewGuid().ToString("D"), out _));
    }

    [Fact]
    public void From_rejects_empty_and_non_uuid_v7_values_with_stable_codes()
    {
        TestValues.AssertValidationCode(
            () => TaskId.From(Guid.Empty),
            "task.id.empty");

        TestValues.AssertValidationCode(
            () => TaskId.From(Guid.NewGuid()),
            "task.id.not_uuid_v7");
    }

    [Fact]
    public void Parse_distinguishes_format_and_uuid_version_failures()
    {
        TestValues.AssertValidationCode(
            () => TaskId.Parse("not-a-guid"),
            "task.id.invalid_format");

        TestValues.AssertValidationCode(
            () => TaskId.Parse(Guid.NewGuid().ToString("D")),
            "task.id.not_uuid_v7");
    }

    [Fact]
    public void Task_id_equality_is_value_based()
    {
        var first = TestValues.TaskId();
        var same = TaskId.Parse(first.ToString());
        var different = TestValues.AnotherTaskId();

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, different);
    }
}
