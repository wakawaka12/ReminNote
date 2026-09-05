using System.Globalization;
using System.Text.Json;
using NodaTime;
using ReminNote.Core.Application;
using ReminNote.Core.Reminders.Domain;

namespace ReminNote.Core.Protocol;

/// <summary>
/// Versioned wire projection of a task reminder intent. Keeping this shape in
/// Core makes validation, canonical hashing and the Agent writer use the same
/// contract; it is deliberately independent of EF entities.
/// </summary>
public sealed record ProtocolReminderRulePayload(
    string Purpose,
    ProtocolReminderTimingPayload Timing,
    string Priority = "NORMAL",
    bool Pinned = false,
    ProtocolReminderRepeatPolicyPayload? RepeatPolicy = null,
    string WakePolicy = "DEFAULT",
    bool Enabled = true)
{
    public ReminderRuleOptions ToOptions()
    {
        Validate();
        return new ReminderRuleOptions(
            ParsePurpose(Purpose),
            Timing.ToDomain(),
            ParsePriority(Priority),
            Pinned,
            (RepeatPolicy ?? new ProtocolReminderRepeatPolicyPayload(false, null, null)).ToDomain(),
            ParseWakePolicy(WakePolicy),
            Enabled);
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Purpose) || !Enum.TryParse<ReminderPurpose>(Purpose, false, out var purpose) ||
            purpose is ReminderPurpose.ANIME_PRE_AIRING or ReminderPurpose.ANIME_AIRING or ReminderPurpose.ANIME_CUSTOM)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "reminder.purpose must be an executable TASK_* purpose.",
                nameof(Purpose));
        }

        ArgumentNullException.ThrowIfNull(Timing);
        Timing.Validate();
        if (!Enum.TryParse<ReminderPriority>(Priority, false, out _))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "reminder.priority must be LOW, NORMAL or HIGH.",
                nameof(Priority));
        }

        if (!Enum.TryParse<WakePolicy>(WakePolicy, false, out _))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "reminder.wakePolicy must be DEFAULT, YES or NO.",
                nameof(WakePolicy));
        }

        RepeatPolicy?.Validate();
        // Let the domain own the purpose/timing compatibility rules as well as
        // the signed-offset bounds. This rejects a shape before any Task row
        // can be committed.
        _ = new ReminderRuleOptions(
            purpose,
            Timing.ToDomain(),
            ParsePriority(Priority),
            Pinned,
            (RepeatPolicy ?? new ProtocolReminderRepeatPolicyPayload(false, null, null)).ToDomain(),
            ParseWakePolicy(WakePolicy),
            Enabled);
    }

    private static ReminderPurpose ParsePurpose(string value) =>
        Enum.Parse<ReminderPurpose>(value, false);

    private static ReminderPriority ParsePriority(string value) =>
        Enum.Parse<ReminderPriority>(value, false);

    private static WakePolicy ParseWakePolicy(string value) =>
        Enum.Parse<WakePolicy>(value, false);
}

public sealed record ProtocolReminderTimingPayload(
    string Kind,
    string? Anchor = null,
    long? OffsetSeconds = null,
    string? AtUtc = null)
{
    public ReminderTiming ToDomain()
    {
        Validate();
        return Kind switch
        {
            "RELATIVE" => ReminderTiming.Relative(
                Enum.Parse<ReminderAnchor>(Anchor!, false),
                OffsetSeconds!.Value),
            "ABSOLUTE_UTC" => ReminderTiming.AbsoluteUtc(ParseInstant(AtUtc!)),
            _ => throw new InvalidOperationException("Validate must run before converting reminder timing.")
        };
    }

    public void Validate()
    {
        switch (Kind)
        {
            case "RELATIVE":
                if (Anchor is null || !Enum.TryParse<ReminderAnchor>(Anchor, false, out _) ||
                    OffsetSeconds is null || AtUtc is not null)
                {
                    throw ProtocolContractException.Invalid(
                        ProtocolErrorCodes.InvalidRequest,
                        "Relative reminder timing requires anchor and signed offsetSeconds only.",
                        nameof(Anchor));
                }

                break;
            case "ABSOLUTE_UTC":
                if (Anchor is not null || OffsetSeconds is not null || AtUtc is null)
                {
                    throw ProtocolContractException.Invalid(
                        ProtocolErrorCodes.InvalidRequest,
                        "Absolute reminder timing requires atUtc only.",
                        nameof(AtUtc));
                }

                _ = ParseInstant(AtUtc);
                break;
            default:
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    "reminder.timing.kind must be RELATIVE or ABSOLUTE_UTC.",
                    nameof(Kind));
        }
    }

    public static ProtocolReminderTimingPayload FromDomain(ReminderTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        var result = timing switch
        {
            RelativeReminderTiming relative => new ProtocolReminderTimingPayload(
                "RELATIVE",
                relative.Anchor.ToString(),
                relative.OffsetSeconds),
            AbsoluteReminderTiming absolute => new ProtocolReminderTimingPayload(
                "ABSOLUTE_UTC",
                AtUtc: FormatInstant(absolute.AtUtc)),
            _ => throw new ArgumentOutOfRangeException(nameof(timing))
        };
        result.Validate();
        return result;
    }

    internal static Instant ParseInstant(string value)
    {
        var patterns = new[]
        {
            "uuuu-MM-dd'T'HH:mm:ss'Z'",
            "uuuu-MM-dd'T'HH:mm:ss.FFFFFFFFF'Z'"
        };
        foreach (var pattern in patterns)
        {
            var parse = NodaTime.Text.InstantPattern.CreateWithInvariantCulture(pattern).Parse(value);
            if (parse.Success &&
                (string.Equals(FormatInstant(parse.Value), value, StringComparison.Ordinal) ||
                 string.Equals(
                     NodaTime.Text.InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd'T'HH:mm:ss'Z'").Format(parse.Value),
                     value,
                     StringComparison.Ordinal)))
            {
                return parse.Value;
            }
        }

        throw ProtocolContractException.Invalid(
            ProtocolErrorCodes.InvalidRequest,
            "reminder.timing.atUtc must be a canonical UTC instant.",
            nameof(AtUtc));
    }

    internal static string FormatInstant(Instant value) =>
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture(
            "uuuu-MM-dd'T'HH:mm:ss.FFFFFFFFF'Z'").Format(value);
}

public sealed record ProtocolReminderRepeatPolicyPayload(
    bool Enabled,
    long? IntervalSeconds,
    int? MaxCount)
{
    public RepeatPolicy ToDomain()
    {
        Validate();
        return new RepeatPolicy(Enabled, IntervalSeconds, MaxCount);
    }

    public void Validate()
    {
        if (!Enabled && IntervalSeconds is not null)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Disabled repeatPolicy cannot carry intervalSeconds.",
                nameof(IntervalSeconds));
        }

        if (IntervalSeconds is <= 0 || MaxCount is <= 0)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidNumber,
                "repeatPolicy intervalSeconds and maxCount must be positive.",
                nameof(IntervalSeconds));
        }

        if (Enabled && IntervalSeconds is null)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidNumber,
                "Enabled repeatPolicy requires intervalSeconds.",
                nameof(IntervalSeconds));
        }
    }

    public static ProtocolReminderRepeatPolicyPayload FromDomain(RepeatPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new(policy.Enabled, policy.IntervalSeconds, policy.MaxCount);
    }
}

/// <summary>Strict parser for the nested reminder object in Task commands.</summary>
public static class ProtocolReminderRulePayloadParser
{
    public static ProtocolReminderRulePayload Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "reminder must be a JSON object.",
                "reminder");
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var name = ProtocolValidation.NormalizeUnicode(property.Name, "reminder property name");
            if (!fields.TryAdd(name, property.Value))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.DuplicateKey,
                    "reminder property names must be unique.",
                    property.Name);
            }

            if (name is not ("purpose" or "timing" or "priority" or "pinned" or "repeatPolicy" or "wakePolicy" or "enabled"))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.UnknownField,
                    $"reminder field '{property.Name}' is not allowed.",
                    property.Name);
            }
        }

        var result = new ProtocolReminderRulePayload(
            RequireString(fields, "purpose"),
            ParseTiming(Require(fields, "timing")),
            fields.TryGetValue("priority", out var priority) ? RequireString(priority, "priority") : "NORMAL",
            fields.TryGetValue("pinned", out var pinned) ? RequireBoolean(pinned, "pinned") : false,
            fields.TryGetValue("repeatPolicy", out var repeat) ? ParseRepeat(repeat) : null,
            fields.TryGetValue("wakePolicy", out var wake) ? RequireString(wake, "wakePolicy") : "DEFAULT",
            fields.TryGetValue("enabled", out var enabled) ? RequireBoolean(enabled, "enabled") : true);
        result.Validate();
        return result;
    }

    private static ProtocolReminderTimingPayload ParseTiming(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw ProtocolContractException.Invalid(ProtocolErrorCodes.InvalidRequest, "reminder.timing must be an object.", "timing");
        }

        var fields = ReadFields(value, "timing", "kind", "anchor", "offsetSeconds", "atUtc");
        return new ProtocolReminderTimingPayload(
            RequireString(fields, "kind"),
            OptionalString(fields, "anchor"),
            OptionalSignedInt64(fields, "offsetSeconds"),
            OptionalString(fields, "atUtc"));
    }

    private static ProtocolReminderRepeatPolicyPayload ParseRepeat(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw ProtocolContractException.Invalid(ProtocolErrorCodes.InvalidRequest, "reminder.repeatPolicy must be an object.", "repeatPolicy");
        }

        var fields = ReadFields(value, "repeatPolicy", "enabled", "intervalSeconds", "maxCount");
        return new ProtocolReminderRepeatPolicyPayload(
            fields.TryGetValue("enabled", out var enabled) && RequireBoolean(enabled, "enabled"),
            OptionalSignedInt64(fields, "intervalSeconds"),
            OptionalInt32(fields, "maxCount"));
    }

    private static Dictionary<string, JsonElement> ReadFields(
        JsonElement value,
        string objectName,
        params string[] allowed)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var name = ProtocolValidation.NormalizeUnicode(property.Name, $"{objectName} property name");
            if (!fields.TryAdd(name, property.Value))
            {
                throw ProtocolContractException.Invalid(ProtocolErrorCodes.DuplicateKey, $"{objectName} property names must be unique.", property.Name);
            }

            if (!allowed.Contains(name))
            {
                throw ProtocolContractException.Invalid(ProtocolErrorCodes.UnknownField, $"{objectName} field '{property.Name}' is not allowed.", property.Name);
            }
        }

        return fields;
    }

    private static JsonElement Require(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value))
        {
            throw ProtocolContractException.Invalid(ProtocolErrorCodes.MissingField, $"reminder field '{name}' is required.", name);
        }

        return value;
    }

    private static string RequireString(Dictionary<string, JsonElement> fields, string name) =>
        RequireString(Require(fields, name), name);

    private static string RequireString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result)
        {
            throw ProtocolContractException.Invalid(ProtocolErrorCodes.InvalidRequest, $"reminder.{name} must be a string.", name);
        }

        return ProtocolValidation.NormalizeUnicode(result, name);
    }

    private static string? OptionalString(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) ? RequireString(value, name) : null;

    private static bool RequireBoolean(JsonElement value, string name)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw ProtocolContractException.Invalid(ProtocolErrorCodes.InvalidRequest, $"reminder.{name} must be boolean.", name);
        }

        return value.GetBoolean();
    }

    private static long? OptionalSignedInt64(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !long.TryParse(value.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw ProtocolContractException.Invalid(ProtocolErrorCodes.InvalidNumber, $"reminder.{name} must be an integer.", name);
        }

        return result;
    }

    private static int? OptionalInt32(Dictionary<string, JsonElement> fields, string name)
    {
        var value = OptionalSignedInt64(fields, name);
        if (value is null)
        {
            return null;
        }

        if (value < int.MinValue || value > int.MaxValue)
        {
            throw ProtocolContractException.Invalid(ProtocolErrorCodes.InvalidNumber, $"reminder.{name} is outside Int32 range.", name);
        }

        return (int)value.Value;
    }
}
