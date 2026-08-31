using System.Text.Json;
using NodaTime;
using NodaTime.Text;
using ReminNote.Core.Tasks;

namespace ReminNote.Core.Protocol;

/// <summary>
/// Frozen RN-CJ-1 wire shape for a task's civil planning time.
/// Every value contains type and localDate. ANYTIME has no additional time
/// fields, TIME has only time, and RANGE has only start and end. Times use
/// minute precision in HH:mm form; equal RANGE endpoints are rejected and an
/// end earlier than start represents a cross-midnight range.
/// </summary>
public sealed record ProtocolTimeSpecPayload(
    string Type,
    string LocalDate,
    string? Time = null,
    string? Start = null,
    string? End = null)
{
    private static readonly LocalDatePattern DatePattern =
        LocalDatePattern.CreateWithInvariantCulture("uuuu-MM-dd");

    private static readonly LocalTimePattern TimePattern =
        LocalTimePattern.CreateWithInvariantCulture("HH:mm");

    public static ProtocolTimeSpecPayload Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "timeSpec must be a JSON object.",
                "timeSpec");
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var normalizedName = ProtocolValidation.NormalizeUnicode(property.Name, "timeSpec property name");
            if (!fields.TryAdd(normalizedName, property.Value))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.DuplicateKey,
                    "timeSpec property names must be unique after NFC normalization.",
                    property.Name);
            }

            if (normalizedName is not ("type" or "localDate" or "time" or "start" or "end"))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.UnknownField,
                    $"timeSpec field '{property.Name}' is not allowed.",
                    property.Name);
            }
        }

        var result = new ProtocolTimeSpecPayload(
            RequiredString(fields, "type"),
            RequiredString(fields, "localDate"),
            OptionalString(fields, "time"),
            OptionalString(fields, "start"),
            OptionalString(fields, "end"));
        result.Validate();
        return result;
    }

    public TimeSpec ToDomain()
    {
        Validate();
        var date = ParseDate(LocalDate);
        return Type switch
        {
            "ANYTIME" => TimeSpec.Anytime(date),
            "TIME" => TimeSpec.At(date, ParseTime(Time!, "time")),
            "RANGE" => TimeSpec.Range(date, ParseTime(Start!, "start"), ParseTime(End!, "end")),
            _ => throw new InvalidOperationException("Validate must run before converting timeSpec.")
        };
    }

    public static ProtocolTimeSpecPayload FromDomain(TimeSpec timeSpec)
    {
        ArgumentNullException.ThrowIfNull(timeSpec);
        var localDate = DatePattern.Format(timeSpec.LocalDate);
        var result = timeSpec switch
        {
            AnytimeSpec => new ProtocolTimeSpecPayload("ANYTIME", localDate),
            TimePointSpec point => new ProtocolTimeSpecPayload("TIME", localDate, Time: TimePattern.Format(point.TimePoint)),
            TimeRangeSpec range => new ProtocolTimeSpecPayload("RANGE", localDate, Start: TimePattern.Format(range.RangeStart), End: TimePattern.Format(range.RangeEnd)),
            _ => throw new ArgumentOutOfRangeException(nameof(timeSpec))
        };
        result.Validate();
        return result;
    }

    public void Validate()
    {
        ProtocolValidation.RequireUtf8ByteLength(Type, 16, nameof(Type));
        ProtocolValidation.RequireUtf8ByteLength(LocalDate, 10, nameof(LocalDate));
        ParseDate(LocalDate);

        switch (Type)
        {
            case "ANYTIME":
                RequireAbsent(Time, nameof(Time));
                RequireAbsent(Start, nameof(Start));
                RequireAbsent(End, nameof(End));
                break;
            case "TIME":
                RequireTime(Time, nameof(Time));
                RequireAbsent(Start, nameof(Start));
                RequireAbsent(End, nameof(End));
                break;
            case "RANGE":
                RequireAbsent(Time, nameof(Time));
                RequireTime(Start, nameof(Start));
                RequireTime(End, nameof(End));
                if (string.Equals(Start, End, StringComparison.Ordinal))
                {
                    throw ProtocolContractException.Invalid(
                        ProtocolErrorCodes.InvalidRequest,
                        "timeSpec RANGE start and end must be different.",
                        nameof(End));
                }
                break;
            default:
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    "timeSpec type must be ANYTIME, TIME, or RANGE.",
                    nameof(Type));
        }
    }

    private static string RequiredString(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.MissingField,
                $"timeSpec field '{name}' is required.",
                name);
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"timeSpec field '{name}' must be a string.",
                name);
        }

        return ProtocolValidation.NormalizeUnicode(result, name);
    }

    private static string? OptionalString(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"timeSpec field '{name}' must be omitted or a string.",
                name);
        }

        return ProtocolValidation.NormalizeUnicode(result, name);
    }

    private static LocalDate ParseDate(string value)
    {
        var parse = DatePattern.Parse(value);
        if (!parse.Success)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "timeSpec localDate must use the yyyy-MM-dd form.",
                nameof(LocalDate));
        }

        return parse.Value;
    }

    private static LocalTime ParseTime(string value, string fieldName)
    {
        var parse = TimePattern.Parse(value);
        if (!parse.Success)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"timeSpec {fieldName} must use the HH:mm form.",
                fieldName);
        }

        return parse.Value;
    }

    private static void RequireTime(string? value, string fieldName)
    {
        if (value is null)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.MissingField,
                $"timeSpec field '{fieldName}' is required for this type.",
                fieldName);
        }

        ProtocolValidation.RequireUtf8ByteLength(value, 5, fieldName);
        ParseTime(value, fieldName);
    }

    private static void RequireAbsent(string? value, string fieldName)
    {
        if (value is not null)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"timeSpec field '{fieldName}' is not allowed for this type.",
                fieldName);
        }
    }
}
