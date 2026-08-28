using ReminNote.Core.Tasks;

namespace ReminNote.Tests;

public sealed class TaskIdTests
{
    [Fact]
    public void NewCreatesUuidV7Identity()
    {
        var taskId = TaskId.New();

        TestValues.AssertUuidV7(taskId.Value);
    }

    [Fact]
    public void FromAndParsePreserveUuidV7Identity()
    {
        var guid = Guid.CreateVersion7();

        var from = TaskId.From(guid);
        var parsed = TaskId.Parse(guid.ToString("D"));

        Assert.Equal(guid, from.Value);
        Assert.Equal(from, parsed);
    }

    [Fact]
    public void TryParseReturnsFalseForNullEmptyAndNonUuidV7Values()
    {
        Assert.False(TaskId.TryParse(null, out _));
        Assert.False(TaskId.TryParse(string.Empty, out _));
        Assert.False(TaskId.TryParse(Guid.Empty.ToString("D"), out _));
        Assert.False(TaskId.TryParse(Guid.NewGuid().ToString("D"), out _));
    }

    [Fact]
    public void FromRejectsEmptyAndNonUuidV7ValuesWithStableCodes()
    {
        TestValues.AssertValidationCode(
            () => TaskId.From(Guid.Empty),
            "task.id.empty");

        TestValues.AssertValidationCode(
            () => TaskId.From(Guid.NewGuid()),
            "task.id.not_uuid_v7");
    }

    [Fact]
    public void ParseDistinguishesFormatAndUuidVersionFailures()
    {
        TestValues.AssertValidationCode(
            () => TaskId.Parse("not-a-guid"),
            "task.id.invalid_format");

        TestValues.AssertValidationCode(
            () => TaskId.Parse(Guid.NewGuid().ToString("D")),
            "task.id.not_uuid_v7");
    }

    [Fact]
    public void TaskIdEqualityIsValueBased()
    {
        var first = TestValues.TaskId();
        var same = TaskId.Parse(first.ToString());
        var different = TestValues.AnotherTaskId();

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, different);
    }
}
