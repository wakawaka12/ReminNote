using ReminNote.Core.Tasks;

namespace ReminNote.Tests;

public sealed class TaskResultTests
{
    [Theory]
    [InlineData(TaskResult.COMPLETED)]
    [InlineData(TaskResult.MISSED)]
    [InlineData(TaskResult.PARTIAL)]
    public void ResultRecordAcceptsEachFrozenResultValue(TaskResult result)
    {
        var record = TaskResultRecord.Create(result, TestValues.CreatedAt, "已记录");

        Assert.Equal(result, record.Result);
        Assert.Equal(TestValues.CreatedAt, record.RecordedAt);
        Assert.Equal("已记录", record.Note);
    }

    [Fact]
    public void ResultRecordNormalizesBlankNoteToNullWithoutChangingContent()
    {
        var blank = TaskResultRecord.Create(TaskResult.COMPLETED, TestValues.CreatedAt, "  ");
        var note = TaskResultRecord.Create(TaskResult.COMPLETED, TestValues.CreatedAt, "  note  ");

        Assert.Null(blank.Note);
        Assert.Equal("  note  ", note.Note);
    }

    [Fact]
    public void ResultRecordRejectsUnknownResultValues()
    {
        TestValues.AssertValidationCode(
            () => TaskResultRecord.Create((TaskResult)99, TestValues.CreatedAt),
            "task.result.invalid");
    }

    [Fact]
    public void ResultRecordEqualityIncludesResultTimestampAndNote()
    {
        var first = TaskResultRecord.Create(TaskResult.COMPLETED, TestValues.CreatedAt, "note");
        var same = TaskResultRecord.Create(TaskResult.COMPLETED, TestValues.CreatedAt, "note");
        var differentTimestamp = TaskResultRecord.Create(TaskResult.COMPLETED, TestValues.ChangedAt, "note");

        Assert.Equal(first, same);
        Assert.NotEqual(first, differentTimestamp);
    }
}
