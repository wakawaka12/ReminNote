using ReminNote.Core.Tasks;

namespace ReminNote.Tests;

public sealed class TaskResultTests
{
    [Theory]
    [InlineData(TaskResult.COMPLETED)]
    [InlineData(TaskResult.MISSED)]
    [InlineData(TaskResult.PARTIAL)]
    public void Result_record_accepts_each_frozen_result_value(TaskResult result)
    {
        var record = TaskResultRecord.Create(result, TestValues.CreatedAt, "已记录");

        Assert.Equal(result, record.Result);
        Assert.Equal(TestValues.CreatedAt, record.RecordedAt);
        Assert.Equal("已记录", record.Note);
    }

    [Fact]
    public void Result_record_normalizes_blank_note_to_null_without_changing_content()
    {
        var blank = TaskResultRecord.Create(TaskResult.COMPLETED, TestValues.CreatedAt, "  ");
        var note = TaskResultRecord.Create(TaskResult.COMPLETED, TestValues.CreatedAt, "  note  ");

        Assert.Null(blank.Note);
        Assert.Equal("  note  ", note.Note);
    }

    [Fact]
    public void Result_record_rejects_unknown_result_values()
    {
        TestValues.AssertValidationCode(
            () => TaskResultRecord.Create((TaskResult)99, TestValues.CreatedAt),
            "task.result.invalid");
    }

    [Fact]
    public void Result_record_equality_includes_result_timestamp_and_note()
    {
        var first = TaskResultRecord.Create(TaskResult.COMPLETED, TestValues.CreatedAt, "note");
        var same = TaskResultRecord.Create(TaskResult.COMPLETED, TestValues.CreatedAt, "note");
        var differentTimestamp = TaskResultRecord.Create(TaskResult.COMPLETED, TestValues.ChangedAt, "note");

        Assert.Equal(first, same);
        Assert.NotEqual(first, differentTimestamp);
    }
}
