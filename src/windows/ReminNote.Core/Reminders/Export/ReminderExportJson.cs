using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Encodings.Web;
using NodaTime;
using ReminNote.Core.Protocol;

namespace ReminNote.Core.Reminders.Export;

/// <summary>
/// Canonical JSON encoder/decoder for the P3-08 envelope. The encoder sorts
/// entity arrays by identity before passing the document through RN-CJ-1's
/// strict UTF-8/object canonicalizer. RN-CJ-1 is used only as a JSON
/// canonicalization primitive; this is not a P2.5 write operation.
/// </summary>
public static class ReminderExportJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        Indented = false,
        SkipValidation = false,
    };

    public static byte[] Serialize(ReminderExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = document with { Checksum = null };
        ReminderExportValidator.RequireValid(normalized, requireChecksum: false);

        var body = Canonicalize(WriteDocument(normalized, includeChecksum: false));
        var checksum = ComputeChecksum(body);
        var completed = normalized with { Checksum = checksum };
        return Canonicalize(WriteDocument(completed, includeChecksum: true));
    }

    public static ReminderExportDocument Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = StrictJson.Parse(utf8Json);
        }
        catch (ProtocolContractException exception)
        {
            throw new ReminderExportContractException(
                ReminderExportErrorCodes.InvalidJson,
                "导出内容不是严格的 UTF-8 RFC 8259 JSON。",
                innerException: exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Error(ReminderExportErrorCodes.InvalidJson, "导出根值必须是 JSON object。");
            }

            var root = document.RootElement;
            EnsureKnownProperties(root, "导出 envelope", "schema", "schemaVersion", "rules", "schedules", "instances", "checksum");
            var schema = RequiredString(root, "schema");
            var schemaVersion = RequiredInt32(root, "schemaVersion");
            var rules = ParseRules(RequiredArray(root, "rules"));
            var schedules = ParseSchedules(RequiredArray(root, "schedules"));
            var instances = ParseInstances(RequiredArray(root, "instances"));
            var checksum = RequiredString(root, "checksum");

            var parsed = new ReminderExportDocument(schema, schemaVersion, rules, schedules, instances, checksum);
            ReminderExportValidator.RequireValid(parsed, requireChecksum: true);

            var expected = ComputeChecksum(Canonicalize(WriteDocument(parsed with { Checksum = null }, includeChecksum: false)));
            if (!string.Equals(expected, checksum, StringComparison.Ordinal))
            {
                throw Error(
                    ReminderExportErrorCodes.ChecksumMismatch,
                    "导出 checksum 与 canonical envelope 不匹配。",
                    "checksum");
            }

            return parsed;
        }
    }

    public static string ComputeChecksum(ReminderExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = document with { Checksum = null };
        ReminderExportValidator.RequireValid(normalized, requireChecksum: false);
        return ComputeChecksum(Canonicalize(WriteDocument(normalized, includeChecksum: false)));
    }

    public static string ComputeChecksum(ReadOnlySpan<byte> canonicalBody)
    {
        var digest = SHA256.HashData(canonicalBody);
        return ReminderExportContract.ChecksumPrefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static byte[] WriteDocument(ReminderExportDocument document, bool includeChecksum)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", document.Schema);
            writer.WriteNumber("schemaVersion", document.SchemaVersion);
            writer.WritePropertyName("rules");
            writer.WriteStartArray();
            foreach (var rule in document.Rules.OrderBy(static value => value.Id, StringComparer.Ordinal))
            {
                WriteRule(writer, rule);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("schedules");
            writer.WriteStartArray();
            foreach (var schedule in document.Schedules.OrderBy(static value => value.Id, StringComparer.Ordinal))
            {
                WriteSchedule(writer, schedule);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("instances");
            writer.WriteStartArray();
            foreach (var instance in document.Instances.OrderBy(static value => value.Id, StringComparer.Ordinal))
            {
                WriteInstance(writer, instance);
            }

            writer.WriteEndArray();
            if (includeChecksum && document.Checksum is not null)
            {
                writer.WriteString("checksum", document.Checksum);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteRule(Utf8JsonWriter writer, ReminderRuleExport rule)
    {
        writer.WriteStartObject();
        writer.WriteString("id", rule.Id);
        writer.WriteString("targetKind", rule.TargetKind);
        writer.WriteString("targetId", rule.TargetId);
        writer.WriteString("occurrenceId", rule.OccurrenceId);
        writer.WriteString("purpose", rule.Purpose);
        writer.WritePropertyName("timing");
        WriteTiming(writer, rule.Timing);
        writer.WriteString("priority", rule.Priority);
        writer.WriteBoolean("pinned", rule.Pinned);
        writer.WritePropertyName("repeatPolicy");
        WriteRepeatPolicy(writer, rule.RepeatPolicy);
        writer.WriteString("wakePolicy", rule.WakePolicy);
        writer.WriteBoolean("enabled", rule.Enabled);
        writer.WriteNumber("ruleRevision", rule.RuleRevision);
        writer.WriteString("createdAtUtc", rule.CreatedAtUtc);
        writer.WriteString("updatedAtUtc", rule.UpdatedAtUtc);
        writer.WriteEndObject();
    }

    private static void WriteTiming(Utf8JsonWriter writer, ReminderTimingExport? timing)
    {
        writer.WriteStartObject();
        if (timing is not null)
        {
            writer.WriteString("kind", timing.Kind);
            if (timing.Anchor is not null)
            {
                writer.WriteString("anchor", timing.Anchor);
            }

            if (timing.OffsetSeconds is long offsetSeconds)
            {
                writer.WriteNumber("offsetSeconds", offsetSeconds);
            }

            if (timing.AtUtc is not null)
            {
                writer.WriteString("atUtc", timing.AtUtc);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteRepeatPolicy(Utf8JsonWriter writer, ReminderRepeatPolicyExport? repeatPolicy)
    {
        writer.WriteStartObject();
        if (repeatPolicy is not null)
        {
            writer.WriteBoolean("enabled", repeatPolicy.Enabled);
            if (repeatPolicy.IntervalSeconds is long intervalSeconds)
            {
                writer.WriteNumber("intervalSeconds", intervalSeconds);
            }

            if (repeatPolicy.MaxCount is int maxCount)
            {
                writer.WriteNumber("maxCount", maxCount);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteSchedule(Utf8JsonWriter writer, ReminderScheduleExport schedule)
    {
        writer.WriteStartObject();
        writer.WriteString("id", schedule.Id);
        writer.WriteString("ruleId", schedule.RuleId);
        writer.WriteString("occurrenceId", schedule.OccurrenceId);
        writer.WriteString("logicalReminderId", schedule.LogicalReminderId);
        WriteOptionalString(writer, "originScheduleId", schedule.OriginScheduleId);
        writer.WriteString("cause", schedule.Cause);
        writer.WriteNumber("ruleRevision", schedule.RuleRevision);
        writer.WriteNumber("scheduleRevision", schedule.ScheduleRevision);
        writer.WriteString("triggerAtUtc", schedule.TriggerAtUtc);
        WriteOptionalString(writer, "timeZoneId", schedule.TimeZoneId);
        writer.WriteString("state", schedule.State);
        WriteOptionalString(writer, "terminalReason", schedule.TerminalReason);
        WriteOptionalString(writer, "replacementScheduleId", schedule.ReplacementScheduleId);
        writer.WriteString("createdAtUtc", schedule.CreatedAtUtc);
        WriteOptionalString(writer, "terminalAtUtc", schedule.TerminalAtUtc);
        writer.WriteEndObject();
    }

    private static void WriteInstance(Utf8JsonWriter writer, ReminderInstanceExport instance)
    {
        writer.WriteStartObject();
        writer.WriteString("id", instance.Id);
        writer.WriteString("scheduleId", instance.ScheduleId);
        writer.WriteString("ruleId", instance.RuleId);
        writer.WriteString("occurrenceId", instance.OccurrenceId);
        writer.WriteString("logicalReminderId", instance.LogicalReminderId);
        writer.WriteNumber("attemptOrdinal", instance.AttemptOrdinal);
        writer.WriteString("purpose", instance.Purpose);
        writer.WriteString("priority", instance.Priority);
        writer.WriteBoolean("pinned", instance.Pinned);
        writer.WriteString("triggeredAtUtc", instance.TriggeredAtUtc);
        writer.WriteString("lifecycle", instance.Lifecycle);
        WriteOptionalString(writer, "readAtUtc", instance.ReadAtUtc);
        WriteOptionalString(writer, "resolvedAtUtc", instance.ResolvedAtUtc);
        WriteOptionalString(writer, "resolutionAction", instance.ResolutionAction);
        writer.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static byte[] Canonicalize(ReadOnlySpan<byte> json) => RnCj1Canonicalizer.Canonicalize(json);

    private static List<ReminderRuleExport> ParseRules(JsonElement value)
    {
        var result = new List<ReminderRuleExport>();
        foreach (var item in value.EnumerateArray())
        {
            RequireObject(item, "rules element");
            EnsureKnownProperties(item, "rule", "id", "targetKind", "targetId", "occurrenceId", "purpose", "timing", "priority", "pinned", "repeatPolicy", "wakePolicy", "enabled", "ruleRevision", "createdAtUtc", "updatedAtUtc");
            result.Add(new ReminderRuleExport(
                RequiredString(item, "id"),
                RequiredString(item, "targetKind"),
                RequiredString(item, "targetId"),
                RequiredString(item, "occurrenceId"),
                RequiredString(item, "purpose"),
                ParseTiming(RequiredObject(item, "timing")),
                RequiredString(item, "priority"),
                RequiredBoolean(item, "pinned"),
                ParseRepeatPolicy(RequiredObject(item, "repeatPolicy")),
                RequiredString(item, "wakePolicy"),
                RequiredBoolean(item, "enabled"),
                RequiredInt64(item, "ruleRevision"),
                RequiredString(item, "createdAtUtc"),
                RequiredString(item, "updatedAtUtc")));
        }

        return result;
    }

    private static List<ReminderScheduleExport> ParseSchedules(JsonElement value)
    {
        var result = new List<ReminderScheduleExport>();
        foreach (var item in value.EnumerateArray())
        {
            RequireObject(item, "schedules element");
            EnsureKnownProperties(item, "schedule", "id", "ruleId", "occurrenceId", "logicalReminderId", "originScheduleId", "cause", "ruleRevision", "scheduleRevision", "triggerAtUtc", "timeZoneId", "state", "terminalReason", "replacementScheduleId", "createdAtUtc", "terminalAtUtc");
            result.Add(new ReminderScheduleExport(
                RequiredString(item, "id"),
                RequiredString(item, "ruleId"),
                RequiredString(item, "occurrenceId"),
                RequiredString(item, "logicalReminderId"),
                OptionalString(item, "originScheduleId"),
                RequiredString(item, "cause"),
                RequiredInt64(item, "ruleRevision"),
                RequiredInt64(item, "scheduleRevision"),
                RequiredString(item, "triggerAtUtc"),
                OptionalString(item, "timeZoneId"),
                RequiredString(item, "state"),
                OptionalString(item, "terminalReason"),
                OptionalString(item, "replacementScheduleId"),
                RequiredString(item, "createdAtUtc"),
                OptionalString(item, "terminalAtUtc")));
        }

        return result;
    }

    private static List<ReminderInstanceExport> ParseInstances(JsonElement value)
    {
        var result = new List<ReminderInstanceExport>();
        foreach (var item in value.EnumerateArray())
        {
            RequireObject(item, "instances element");
            EnsureKnownProperties(item, "instance", "id", "scheduleId", "ruleId", "occurrenceId", "logicalReminderId", "attemptOrdinal", "purpose", "priority", "pinned", "triggeredAtUtc", "lifecycle", "readAtUtc", "resolvedAtUtc", "resolutionAction");
            result.Add(new ReminderInstanceExport(
                RequiredString(item, "id"),
                RequiredString(item, "scheduleId"),
                RequiredString(item, "ruleId"),
                RequiredString(item, "occurrenceId"),
                RequiredString(item, "logicalReminderId"),
                RequiredInt32(item, "attemptOrdinal"),
                RequiredString(item, "purpose"),
                RequiredString(item, "priority"),
                RequiredBoolean(item, "pinned"),
                RequiredString(item, "triggeredAtUtc"),
                RequiredString(item, "lifecycle"),
                OptionalString(item, "readAtUtc"),
                OptionalString(item, "resolvedAtUtc"),
                OptionalString(item, "resolutionAction")));
        }

        return result;
    }

    private static ReminderTimingExport ParseTiming(JsonElement value)
    {
        EnsureKnownProperties(value, "timing", "kind", "anchor", "offsetSeconds", "atUtc");
        return new ReminderTimingExport(
            RequiredString(value, "kind"),
            OptionalString(value, "anchor"),
            OptionalInt64(value, "offsetSeconds"),
            OptionalString(value, "atUtc"));
    }

    private static ReminderRepeatPolicyExport ParseRepeatPolicy(JsonElement value)
    {
        EnsureKnownProperties(value, "repeatPolicy", "enabled", "intervalSeconds", "maxCount");
        return new ReminderRepeatPolicyExport(
            RequiredBoolean(value, "enabled"),
            OptionalInt64(value, "intervalSeconds"),
            OptionalInt32(value, "maxCount"));
    }

    private static JsonElement RequiredArray(JsonElement objectValue, string name)
    {
        var value = RequiredProperty(objectValue, name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Error(ReminderExportErrorCodes.FieldType, $"{name} 必须是 JSON array。", name);
        }

        return value;
    }

    private static JsonElement RequiredObject(JsonElement objectValue, string name)
    {
        var value = RequiredProperty(objectValue, name);
        RequireObject(value, name);
        return value;
    }

    private static void RequireObject(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Error(ReminderExportErrorCodes.FieldType, $"{field} 必须是 JSON object。", field);
        }
    }

    private static string RequiredString(JsonElement objectValue, string name)
    {
        var value = RequiredProperty(objectValue, name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is null)
        {
            throw Error(ReminderExportErrorCodes.FieldType, $"{name} 必须是非 null JSON string。", name);
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement objectValue, string name)
    {
        if (!objectValue.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is null)
        {
            throw Error(ReminderExportErrorCodes.FieldType, $"{name} 必须是 JSON string 或 null。", name);
        }

        return value.GetString();
    }

    private static bool RequiredBoolean(JsonElement objectValue, string name)
    {
        var value = RequiredProperty(objectValue, name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Error(ReminderExportErrorCodes.FieldType, $"{name} 必须是 JSON boolean。", name);
        }

        return value.GetBoolean();
    }

    private static int RequiredInt32(JsonElement objectValue, string name)
    {
        var value = RequiredProperty(objectValue, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw Error(ReminderExportErrorCodes.FieldType, $"{name} 必须是 JSON int32。", name);
        }

        return result;
    }

    private static long RequiredInt64(JsonElement objectValue, string name)
    {
        var value = RequiredProperty(objectValue, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw Error(ReminderExportErrorCodes.FieldType, $"{name} 必须是 JSON int64。", name);
        }

        return result;
    }

    private static int? OptionalInt32(JsonElement objectValue, string name)
    {
        if (!objectValue.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw Error(ReminderExportErrorCodes.FieldType, $"{name} 必须是 JSON int32 或 null。", name);
        }

        return result;
    }

    private static long? OptionalInt64(JsonElement objectValue, string name)
    {
        if (!objectValue.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw Error(ReminderExportErrorCodes.FieldType, $"{name} 必须是 JSON int64 或 null。", name);
        }

        return result;
    }

    private static JsonElement RequiredProperty(JsonElement objectValue, string name)
    {
        if (!objectValue.TryGetProperty(name, out var value))
        {
            throw Error(ReminderExportErrorCodes.MissingField, $"缺少必填字段 {name}。", name);
        }

        return value;
    }

    private static void EnsureKnownProperties(JsonElement value, string context, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (allowedSet.Contains(property.Name))
            {
                continue;
            }

            var code = ReminderExportValidator.IsSensitiveFieldName(property.Name)
                ? ReminderExportErrorCodes.SensitiveField
                : ReminderExportErrorCodes.UnknownField;
            throw Error(code, $"{context} 含有不允许的字段 {property.Name}。", property.Name);
        }
    }

    private static ReminderExportContractException Error(string code, string message, string? field = null) =>
        new(code, message, field);
}
