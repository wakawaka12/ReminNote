using ReminNote.Core;
using ReminNote.Core.Tasks;
using ReminNote.Core.Tasks.Parsing;

namespace ReminNote.Tests;

public sealed class TaskParserTests
{
    private static readonly LocalDate LogicalToday = new(2026, 8, 28);

    [Theory]
    [InlineData("整理房间")]
    [InlineData("今天 整理房间")]
    public void ParsesAnytimeWithLogicalToday(string input)
    {
        var result = TaskParser.Parse(input, LogicalToday);

        var value = AssertSuccess(result);
        var timeSpec = Assert.IsType<AnytimeSpec>(value.TimeSpec);

        Assert.Equal("整理房间", value.Title);
        Assert.Equal(LogicalToday, timeSpec.LocalDate);
    }

    [Theory]
    [InlineData("明天", 1)]
    [InlineData("后天", 2)]
    public void ParsesRelativeDatesFromCallerSuppliedLogicalToday(string dateToken, int dayOffset)
    {
        var result = TaskParser.Parse($"{dateToken} 复习", LogicalToday);

        var value = AssertSuccess(result);
        var timeSpec = Assert.IsType<AnytimeSpec>(value.TimeSpec);

        Assert.Equal("复习", value.Title);
        Assert.Equal(LogicalToday.PlusDays(dayOffset), timeSpec.LocalDate);
    }

    [Fact]
    public void ParsesIsoDateAsCalendarDate()
    {
        var result = TaskParser.Parse("2026-09-01 记录日报", LogicalToday);

        var value = AssertSuccess(result);
        var timeSpec = Assert.IsType<AnytimeSpec>(value.TimeSpec);

        Assert.Equal(new LocalDate(2026, 9, 1), timeSpec.LocalDate);
    }

    [Fact]
    public void ParsesTimeOnLogicalToday()
    {
        var result = TaskParser.Parse("00:00 起床", LogicalToday);

        var value = AssertSuccess(result);
        var timeSpec = Assert.IsType<TimePointSpec>(value.TimeSpec);

        Assert.Equal(LogicalToday, timeSpec.LocalDate);
        Assert.Equal(new LocalTime(0, 0), timeSpec.TimePoint);
    }

    [Fact]
    public void ParsesDateAndTimeTogether()
    {
        var result = TaskParser.Parse("明天 23:59 发送报告", LogicalToday);

        var value = AssertSuccess(result);
        var timeSpec = Assert.IsType<TimePointSpec>(value.TimeSpec);

        Assert.Equal(LogicalToday.PlusDays(1), timeSpec.LocalDate);
        Assert.Equal(new LocalTime(23, 59), timeSpec.TimePoint);
    }

    [Fact]
    public void TrimsOnlyOuterWhitespaceFromTitle()
    {
        var result = TaskParser.Parse("  明天   整理  房间  ", LogicalToday);

        var value = AssertSuccess(result);

        Assert.Equal("整理  房间", value.Title);
    }

    [Theory]
    [InlineData("14:00-17:30")]
    [InlineData("14:00–17:30")]
    public void ParsesSameDayRangeWithAsciiOrEnDash(string rangeToken)
    {
        var result = TaskParser.Parse($"{rangeToken} 写代码", LogicalToday);

        var value = AssertSuccess(result);
        var timeSpec = Assert.IsType<TimeRangeSpec>(value.TimeSpec);

        Assert.Equal(LogicalToday, timeSpec.LocalDate);
        Assert.Equal(new LocalTime(14, 0), timeSpec.RangeStart);
        Assert.Equal(new LocalTime(17, 30), timeSpec.RangeEnd);
        Assert.False(timeSpec.IsCrossMidnight);
    }

    [Fact]
    public void ParsesCrossMidnightRangeAndKeepsStartDateOwnership()
    {
        var result = TaskParser.Parse("明天 23:00-01:00 跨午夜任务", LogicalToday);

        var value = AssertSuccess(result);
        var timeSpec = Assert.IsType<TimeRangeSpec>(value.TimeSpec);

        Assert.Equal(LogicalToday.PlusDays(1), timeSpec.LocalDate);
        Assert.Equal(LogicalToday.PlusDays(2), timeSpec.EndLocalDate);
        Assert.True(timeSpec.IsCrossMidnight);
        Assert.Equal(new LocalTime(23, 0), timeSpec.RangeStart);
        Assert.Equal(new LocalTime(1, 0), timeSpec.RangeEnd);
    }

    [Theory]
    [InlineData("今天18:00", "task.parser.time.invalid")]
    [InlineData("明天18:00", "task.parser.time.invalid")]
    [InlineData("后天08:05", "task.parser.time.invalid")]
    [InlineData("今天18:00 标题", "task.parser.time.invalid")]
    [InlineData("明天18:00 标题", "task.parser.time.invalid")]
    [InlineData("后天08:05 标题", "task.parser.time.invalid")]
    [InlineData("明天18:00-19:00 粘连范围", "task.parser.range.invalid")]
    [InlineData("后天08:05–09:00 粘连范围和标题", "task.parser.range.invalid")]
    public void RejectsGluedRelativeDateTimeBeforeTreatingInputAsTitle(
        string input,
        string expectedCode)
    {
        var result = TaskParser.Parse(input, LogicalToday);

        AssertFailure(result, expectedCode);
    }

    [Theory]
    [InlineData(null, "task.parser.empty")]
    [InlineData("", "task.parser.empty")]
    [InlineData("   ", "task.parser.empty")]
    [InlineData("今天", "task.parser.title.required")]
    [InlineData("2026-02-30 无效日期", "task.parser.date.invalid")]
    [InlineData("2026/08/28 非 ISO 日期", "task.parser.date.invalid")]
    [InlineData("昨天 历史日期未支持", "task.parser.date.invalid")]
    [InlineData("9:00 非 HH:mm 时间", "task.parser.time.invalid")]
    [InlineData("24:00 无效时间", "task.parser.time.invalid")]
    [InlineData("23:60 无效时间", "task.parser.time.invalid")]
    [InlineData("14:00-14:00 零时长范围", "task.time_range.zero_duration")]
    [InlineData("14:00-25:00 无效范围", "task.parser.range.invalid")]
    [InlineData("14:00--17:00 无效范围", "task.parser.range.invalid")]
    [InlineData("明天 #生活 标签不支持", "task.parser.reserved_syntax")]
    [InlineData("明天 !高 优先级不支持", "task.parser.reserved_syntax")]
    public void RejectsInvalidInputWithStableErrorCode(string? input, string expectedCode)
    {
        var result = TaskParser.Parse(input, LogicalToday);

        AssertFailure(result, expectedCode);
    }

    [Fact]
    public void RejectsSeparatedRangeSyntaxInsteadOfTreatingItAsATitle()
    {
        var result = TaskParser.Parse("14:00 - 17:00 分隔范围", LogicalToday);

        AssertFailure(result, "task.parser.range.invalid");
    }

    [Theory]
    [InlineData("00:00")]
    [InlineData("23:59")]
    public void AcceptsInclusiveClockBoundaries(string timeToken)
    {
        var result = TaskParser.Parse($"{timeToken} 边界时间", LogicalToday);

        var value = AssertSuccess(result);
        Assert.IsType<TimePointSpec>(value.TimeSpec);
    }

    [Fact]
    public void AcceptsTitleAtExactlyFiveHundredCharacters()
    {
        var title = new string('字', 500);
        var result = TaskParser.Parse(title, LogicalToday);

        var value = AssertSuccess(result);

        Assert.Equal(title, value.Title);
    }

    [Fact]
    public void RejectsTitleLongerThanFiveHundredCharactersWithoutTruncatingIt()
    {
        var title = new string('字', 501);
        var result = TaskParser.Parse(title, LogicalToday);

        AssertFailure(result, "task.title.too_long");
        Assert.Null(result.Value);
    }

    [Fact]
    public void ParsingIsDeterministicForTheSameInputAndLogicalToday()
    {
        const string input = "后天 08:05 确定性任务";

        var first = TaskParser.Parse(input, LogicalToday);
        var second = TaskParser.Parse(input, LogicalToday);

        Assert.Equal(first.IsSuccess, second.IsSuccess);
        Assert.Equal(first.Value?.Title, second.Value?.Title);
        Assert.Equal(first.Value?.TimeSpec, second.Value?.TimeSpec);
        Assert.Equal(
            first.Errors.Select(error => error.Code),
            second.Errors.Select(error => error.Code));
    }

    private static ParsedTaskInput AssertSuccess(TaskParserResult result)
    {
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Value);
        return result.Value!;
    }

    private static void AssertFailure(TaskParserResult result, string expectedCode)
    {
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Single(result.Errors);
        Assert.Equal(expectedCode, result.Errors[0].Code);
    }
}
