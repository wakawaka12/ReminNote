using NodaTime;
using NodaTime.Text;
using ReminNote.Core;

namespace ReminNote.Core.Tasks.Parsing;

/// <summary>
/// The deterministic P2 Quick Add grammar. Parsing is pure: the caller owns
/// the logical Today date and the parser never reads a clock or a database.
/// </summary>
public static class TaskParser
{
    private const string EmptyInputCode = "task.parser.empty";
    private const string ReservedSyntaxCode = "task.parser.reserved_syntax";
    private const string InvalidDateCode = "task.parser.date.invalid";
    private const string InvalidRangeCode = "task.parser.range.invalid";
    private const string InvalidTimeCode = "task.parser.time.invalid";
    private const string TitleRequiredCode = "task.parser.title.required";
    private const string TitleTooLongCode = "task.title.too_long";

    private static readonly LocalDatePattern DatePattern =
        LocalDatePattern.CreateWithInvariantCulture("uuuu-MM-dd");

    private static readonly LocalTimePattern TimePattern =
        LocalTimePattern.CreateWithInvariantCulture("HH:mm");

    private static readonly string[] RelativeDateTokens = ["今天", "明天", "后天"];

    public static TaskParserResult Parse(string? text, LocalDate logicalToday)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TaskParserResult.Failure(Error(
                EmptyInputCode,
                "Quick Add text is empty."));
        }

        var input = text.Trim();
        var tokens = input
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Any(HasReservedSyntax))
        {
            return TaskParserResult.Failure(Error(
                ReservedSyntaxCode,
                "P2 does not support tag or priority syntax."));
        }

        var index = 0;
        var planDate = logicalToday;

        if (index < tokens.Length &&
            TryGetGluedRelativeDateError(tokens[index], out var gluedDateError))
        {
            return TaskParserResult.Failure(gluedDateError!.Value);
        }

        if (index < tokens.Length && IsDateToken(tokens[index]))
        {
            if (!TryResolveDate(tokens[index], logicalToday, out planDate, out var dateError))
            {
                return TaskParserResult.Failure(dateError!.Value);
            }

            index++;
        }
        else if (index < tokens.Length && LooksLikeDateSyntax(tokens[index]))
        {
            return TaskParserResult.Failure(Error(
                InvalidDateCode,
                $"Date is not valid: {tokens[index]}."));
        }

        TimeSpec? timeSpec = null;
        if (index < tokens.Length &&
            TryGetGluedRelativeDateError(tokens[index], out var gluedTimeError))
        {
            return TaskParserResult.Failure(gluedTimeError!.Value);
        }

        if (index < tokens.Length && LooksLikeTimeShape(tokens[index]))
        {
            if (!TryResolveTimeShape(tokens[index], planDate, out timeSpec, out var timeError))
            {
                return TaskParserResult.Failure(timeError!.Value);
            }

            index++;
        }
        else if (index < tokens.Length && LooksLikeMalformedTimeShape(tokens[index]))
        {
            var errorCode = LooksLikeMalformedRangeShape(tokens[index])
                ? InvalidRangeCode
                : InvalidTimeCode;
            return TaskParserResult.Failure(Error(
                errorCode,
                "Time must use HH:mm or HH:mm-HH:mm."));
        }

        if (timeSpec is not null && HasSeparatedRangeSyntax(tokens, index))
        {
            return TaskParserResult.Failure(Error(
                InvalidRangeCode,
                "Range times must use HH:mm-HH:mm without spaces."));
        }

        if (index >= tokens.Length)
        {
            return TaskParserResult.Failure(Error(
                TitleRequiredCode,
                "Task title is required."));
        }

        var title = ExtractTitle(input, tokens, index);
        if (title.Length == 0)
        {
            return TaskParserResult.Failure(Error(
                TitleRequiredCode,
                "Task title is required."));
        }

        if (title.Length > 500)
        {
            return TaskParserResult.Failure(Error(
                TitleTooLongCode,
                "Task title cannot exceed 500 characters."));
        }

        try
        {
            timeSpec ??= TimeSpec.Anytime(planDate);
            return TaskParserResult.Success(new ParsedTaskInput(title, timeSpec));
        }
        catch (DomainValidationException exception)
        {
            return TaskParserResult.Failure(exception.Errors);
        }
    }

    private static bool TryResolveDate(
        string token,
        LocalDate logicalToday,
        out LocalDate date,
        out DomainValidationError? error)
    {
        if (token.Equals("今天", StringComparison.Ordinal))
        {
            date = logicalToday;
            error = null;
            return true;
        }

        if (token.Equals("明天", StringComparison.Ordinal))
        {
            date = logicalToday.PlusDays(1);
            error = null;
            return true;
        }

        if (token.Equals("后天", StringComparison.Ordinal))
        {
            date = logicalToday.PlusDays(2);
            error = null;
            return true;
        }

        var parsed = DatePattern.Parse(token);
        if (parsed.Success)
        {
            date = parsed.Value;
            error = null;
            return true;
        }

        date = default;
        error = Error(InvalidDateCode, $"Date is not valid: {token}.");
        return false;
    }

    private static bool TryResolveTimeShape(
        string token,
        LocalDate planDate,
        out TimeSpec? timeSpec,
        out DomainValidationError? error)
    {
        if (IsRangeToken(token))
        {
            var startToken = token[..5];
            var endToken = token[6..];
            if (!TryParseTime(startToken, out var start) || !TryParseTime(endToken, out var end))
            {
                timeSpec = null;
                error = Error(
                    InvalidRangeCode,
                    "Range times must use valid HH:mm values.");
                return false;
            }

            try
            {
                timeSpec = TimeSpec.Range(planDate, start, end);
                error = null;
                return true;
            }
            catch (DomainValidationException exception)
            {
                timeSpec = null;
                error = exception.Errors[0];
                return false;
            }
        }

        if (!TryParseTime(token, out var point))
        {
            timeSpec = null;
            error = Error(
                InvalidTimeCode,
                "Time must use a valid HH:mm value.");
            return false;
        }

        timeSpec = TimeSpec.At(planDate, point);
        error = null;
        return true;
    }

    private static bool TryParseTime(string token, out LocalTime time)
    {
        if (token.Length != 5 || token[2] != ':' ||
            !token[..2].All(char.IsAsciiDigit) ||
            !token[3..].All(char.IsAsciiDigit))
        {
            time = default;
            return false;
        }

        var parsed = TimePattern.Parse(token);
        if (!parsed.Success)
        {
            time = default;
            return false;
        }

        time = parsed.Value;
        return true;
    }

    private static bool IsDateToken(string token) =>
        token is "今天" or "明天" or "后天" ||
        (token.Length == 10 && token[4] == '-' && token[7] == '-' &&
         token[..4].All(char.IsAsciiDigit) &&
         token[5..7].All(char.IsAsciiDigit) &&
         token[8..].All(char.IsAsciiDigit));

    private static bool TryGetGluedRelativeDateError(
        string token,
        out DomainValidationError? error)
    {
        foreach (var relativeDateToken in RelativeDateTokens)
        {
            if (!token.StartsWith(relativeDateToken, StringComparison.Ordinal) ||
                token.Length == relativeDateToken.Length)
            {
                continue;
            }

            var suffix = token[relativeDateToken.Length..];
            if (suffix[0] is not (>= '0' and <= '9') and not ':' and not '-' and not '–')
            {
                continue;
            }

            var errorCode = suffix.Contains('-') || suffix.Contains('–')
                ? InvalidRangeCode
                : InvalidTimeCode;
            error = Error(
                errorCode,
                $"Relative date and time must be separated by a space: {token}.");
            return true;
        }

        error = null;
        return false;
    }

    private static bool LooksLikeDateSyntax(string token) =>
        token is "昨天" or "前天" or "大后天" or "大前天" or "上周" or "本周" or "下周" ||
        (token.Length >= 5 &&
         token[..4].All(char.IsAsciiDigit) &&
         (token[4] is '-' or '/' or '.'));

    private static bool LooksLikeTimeShape(string token) =>
        (token.Length == 5 && token[2] == ':') || IsRangeToken(token);

    private static bool LooksLikeMalformedTimeShape(string token) =>
        (StartsWithAsciiDigit(token) && token.Contains(':')) ||
        HasNumericRangeSeparator(token);

    private static bool LooksLikeMalformedRangeShape(string token) =>
        StartsWithAsciiDigit(token) &&
        (token.Contains('-') || token.Contains('–') || token.Contains('—') || token.Contains('~'));

    private static bool StartsWithAsciiDigit(string token) =>
        token.Length > 0 && token[0] is >= '0' and <= '9';

    private static bool HasNumericRangeSeparator(string token)
    {
        var separatorIndex = token.IndexOfAny(['-', '–']);
        return separatorIndex > 0 &&
               separatorIndex < token.Length - 1 &&
               token[..separatorIndex].All(char.IsAsciiDigit) &&
               token[(separatorIndex + 1)..].All(char.IsAsciiDigit);
    }

    private static bool HasSeparatedRangeSyntax(string[] tokens, int index) =>
        index + 1 < tokens.Length &&
        (tokens[index] is "-" or "–" or "—" or "~") &&
        (LooksLikeTimeShape(tokens[index + 1]) ||
         LooksLikeMalformedTimeShape(tokens[index + 1]));

    private static string ExtractTitle(string input, string[] tokens, int titleTokenIndex)
    {
        if (titleTokenIndex == 0)
        {
            return input;
        }

        var searchStart = 0;
        for (var index = 0; index < titleTokenIndex; index++)
        {
            var tokenStart = input.IndexOf(tokens[index], searchStart, StringComparison.Ordinal);
            if (tokenStart < 0)
            {
                return string.Join(" ", tokens[titleTokenIndex..]).Trim();
            }

            searchStart = tokenStart + tokens[index].Length;
        }

        return input[searchStart..].Trim();
    }

    private static bool IsRangeToken(string token) =>
        token.Length == 11 && (token[5] == '-' || token[5] == '–');

    private static bool HasReservedSyntax(string token) =>
        token.StartsWith('#') || token.StartsWith('!');

    private static DomainValidationError Error(string code, string message) =>
        new(code, message);
}

public sealed record ParsedTaskInput(string Title, TimeSpec TimeSpec);

public sealed record TaskParserResult(
    ParsedTaskInput? Value,
    IReadOnlyList<DomainValidationError> Errors)
{
    public bool IsSuccess => Value is not null && Errors.Count == 0;

    public static TaskParserResult Success(ParsedTaskInput value) =>
        new(value, []);

    public static TaskParserResult Failure(DomainValidationError error) =>
        new(null, [error]);

    public static TaskParserResult Failure(IReadOnlyList<DomainValidationError> errors) =>
        new(null, errors.ToArray());
}
