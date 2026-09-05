using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;
using ReminNote.Core.Application;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Export;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Reminders.Notifications;
using ReminNote.Core.Tasks;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence.Reminders;

namespace ReminNote.Infrastructure.Persistence.Export;

/// <summary>
/// Versioned envelope for a complete user-data export. It contains user
/// content and durable history, but deliberately omits profile paths, SIDs,
/// machine identifiers, secrets and transport receipts. The nested Reminder
/// export keeps its own P3-08 checksum; the outer checksum covers the complete
/// envelope.
/// </summary>
public static class UserDataExportContract
{
    public const string Schema = "reminnote.user-data.export";
    public const int SchemaVersion = 1;
    public const string InstantPattern = "uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'";
    public const string ChecksumPrefix = "sha256:";
    public const int MaxArtifactBytes = 64 * 1024 * 1024;
}

public sealed record UserTaskExport(
    string Id,
    string Title,
    string TimeType,
    string LocalDate,
    string? TimePoint,
    string? RangeStart,
    string? RangeEnd,
    string? Result,
    string? ResultRecordedAtUtc,
    string? ResultNote,
    string CreatedAtUtc,
    string UpdatedAtUtc,
    int SortOrder,
    string? ContinuedFromTaskId);

public sealed record UserTaskHistoryExport(
    long Id,
    string TaskId,
    string Kind,
    string OccurredAtUtc,
    string Title,
    string TimeType,
    string LocalDate,
    string? TimePoint,
    string? RangeStart,
    string? RangeEnd,
    string? Result,
    string? ResultRecordedAtUtc,
    string? ResultNote,
    string CreatedAtUtc,
    string UpdatedAtUtc,
    int SortOrder,
    string? ContinuedFromTaskId,
    string? RelatedTaskId);

public sealed record UserAppSettingsExport(
    int WorkdayBoundaryMinutes,
    string UpdatedAtUtc);

public sealed record UserDeliveryAttemptExport(
    string AttemptId,
    string InstanceId,
    string Channel,
    string AttemptedAtUtc,
    string Outcome,
    string? ErrorCode);

/// <summary>
/// Full P3-04 append-only delivery event. Profile scope is deliberately not
/// exported; Candidate import binds the event to the newly resolved profile.
/// Keeping event ordinal, retry state and the request identities preserves the
/// durable delivery/recovery history instead of exporting only the legacy
/// summary row.
/// </summary>
public sealed record UserDeliveryAttemptEventExport(
    string AttemptId,
    long EventOrdinal,
    string InstanceId,
    string ScheduleId,
    string RuleId,
    string OccurrenceId,
    string LogicalReminderId,
    string Channel,
    string CorrelationId,
    string RequestId,
    string IdempotencyKey,
    string PurposeSnapshot,
    string PrioritySnapshot,
    bool PinnedSnapshot,
    int TriggerAttemptOrdinal,
    string TriggeredAtUtc,
    string? ScheduledTriggerAtUtc,
    int AttemptNumber,
    string State,
    string? Outcome,
    string CreatedAtUtc,
    string UpdatedAtUtc,
    string? NextAttemptAtUtc,
    string? ErrorCode,
    bool Retryable,
    long Revision,
    string JournalBatchId,
    string ReceiptId);

public sealed record UserDataExportDocument(
    string Schema,
    int SchemaVersion,
    long GlobalRevision,
    IReadOnlyList<UserTaskExport> Tasks,
    IReadOnlyList<UserTaskHistoryExport> TaskHistory,
    UserAppSettingsExport? AppSettings,
    ReminderExportDocument Reminders,
    IReadOnlyList<UserDeliveryAttemptExport> DeliveryAttempts,
    string? NotificationPolicyJson,
    string? Checksum,
    IReadOnlyList<UserDeliveryAttemptEventExport>? DeliveryAttemptEvents = null);

public sealed record UserDataExportArtifact(
    UserDataExportDocument Document,
    byte[] Content,
    string Checksum,
    string? Path = null);

public enum UserDataRestoreStatus
{
    DryRun,
    RequiresConfirmation,
    Conflict,
    Blocked,
    Staged,
    Imported
}

public sealed record UserDataRestoreRequest(
    string ArtifactPath,
    string CandidateRoot,
    string ActiveDatabasePath,
    string ProfileScope,
    bool DryRun = true,
    bool Confirmed = false,
    bool ImportCandidate = false);

public sealed record UserDataRestoreResult(
    UserDataRestoreStatus Status,
    string SourceChecksum,
    string? StagedArtifactPath,
    string? CandidateDatabasePath,
    ReminderRestorePlan ReminderPlan,
    IReadOnlyList<string> Issues);

public sealed class UserDataExportContractException : InvalidOperationException
{
    public UserDataExportContractException(string code, string message, string? field = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Field = field;
    }

    public string Code { get; }

    public string? Field { get; }
}

/// <summary>
/// Canonical serializer/parser for the complete export envelope. Parsing is
/// strict (including duplicate and unknown fields) and verifies the checksum
/// before returning a document to a restore caller.
/// </summary>
public static class UserDataExportJson
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        Indented = false,
        SkipValidation = false
    };
    private static readonly JsonSerializerOptions ParserOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 64
    };
    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture(UserDataExportContract.InstantPattern);
    private static readonly LocalDatePattern LocalDatePattern =
        NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("uuuu-MM-dd");
    private static readonly LocalTimePattern LocalTimePattern =
        NodaTime.Text.LocalTimePattern.CreateWithInvariantCulture("HH:mm:ss.FFFFFFFFF");

    public static byte[] Serialize(UserDataExportDocument document)
    {
        ValidateDocument(document, requireChecksum: false);
        var body = Canonicalize(WriteDocument(document with { Checksum = null }, includeChecksum: false));
        var checksum = ComputeChecksum(body);
        return Canonicalize(WriteDocument(document with { Checksum = checksum }, includeChecksum: true));
    }

    public static UserDataExportDocument Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > UserDataExportContract.MaxArtifactBytes)
        {
            throw Error("export.invalid_size", "导出 artifact 大小不在允许范围内。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                utf8Json.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            EnsureNoDuplicateProperties(document.RootElement);
        }
        catch (UserDataExportContractException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            throw Error("export.invalid_json", "导出内容不是严格的 UTF-8 JSON。", innerException: exception);
        }

        using (document)
        {
            try
            {
                RequireProperties(
                    document.RootElement,
                    "schema",
                    "schemaVersion",
                    "globalRevision",
                    "tasks",
                    "taskHistory",
                    "appSettings",
                    "reminders",
                    "deliveryAttempts",
                    "deliveryAttemptEvents",
                    "notificationPolicy",
                    "checksum");
                var wire = JsonSerializer.Deserialize<WireDocument>(
                    utf8Json,
                    ParserOptions) ?? throw Error("export.invalid_json", "导出根对象为空。");
                var remindersElement = wire.Reminders is { } reminders
                    ? reminders
                    : throw Error("export.field.missing", "导出缺少 reminders。", "reminders");
                var remindersBytes = Utf8.GetBytes(remindersElement.GetRawText());
                var remindersDocument = ReminderExportJson.Parse(remindersBytes);
                var parsed = new UserDataExportDocument(
                    wire.Schema ?? throw Error("export.field.missing", "导出缺少 schema。", "schema"),
                    wire.SchemaVersion,
                    wire.GlobalRevision,
                    wire.Tasks?.Select(ToTask).ToArray()
                        ?? throw Error("export.field.missing", "导出缺少 tasks。", "tasks"),
                    wire.TaskHistory?.Select(ToHistory).ToArray()
                        ?? throw Error("export.field.missing", "导出缺少 taskHistory。", "taskHistory"),
                    wire.AppSettings is null ? null : ToSettings(wire.AppSettings),
                    remindersDocument,
                    wire.DeliveryAttempts?.Select(ToAttempt).ToArray()
                        ?? throw Error("export.field.missing", "导出缺少 deliveryAttempts。", "deliveryAttempts"),
                    wire.NotificationPolicy is { } policy && policy.ValueKind != JsonValueKind.Null
                        ? policy.GetRawText()
                        : null,
                    wire.Checksum ?? throw Error("export.checksum.missing", "导出缺少 checksum。", "checksum"),
                    wire.DeliveryAttemptEvents?.Select(ToAttemptEvent).ToArray()
                        ?? throw Error("export.field.missing", "导出缺少 deliveryAttemptEvents。", "deliveryAttemptEvents"));
                ValidateDocument(parsed, requireChecksum: true);
                var expected = ComputeChecksum(
                    Canonicalize(WriteDocument(parsed with { Checksum = null }, includeChecksum: false)));
                if (!string.Equals(expected, parsed.Checksum, StringComparison.Ordinal))
                {
                    throw Error("export.checksum.mismatch", "导出 checksum 与 canonical envelope 不匹配。", "checksum");
                }

                return parsed;
            }
            catch (UserDataExportContractException)
            {
                throw;
            }
            catch (ReminderExportContractException exception)
            {
                throw Error("export.reminders.invalid", "嵌套 Reminder export 无法验证。", "reminders", exception);
            }
            catch (JsonException exception)
            {
                throw Error("export.invalid_json", "导出字段类型或结构无效。", innerException: exception);
            }
        }
    }

    public static string ComputeChecksum(UserDataExportDocument document)
    {
        ValidateDocument(document, requireChecksum: false);
        return ComputeChecksum(Canonicalize(WriteDocument(document with { Checksum = null }, includeChecksum: false)));
    }

    public static string ComputeChecksum(ReadOnlySpan<byte> canonicalBody) =>
        UserDataExportContract.ChecksumPrefix +
        Convert.ToHexString(SHA256.HashData(canonicalBody)).ToLowerInvariant();

    private static byte[] WriteDocument(UserDataExportDocument document, bool includeChecksum)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", document.Schema);
            writer.WriteNumber("schemaVersion", document.SchemaVersion);
            writer.WriteNumber("globalRevision", document.GlobalRevision);
            writer.WritePropertyName("tasks");
            writer.WriteStartArray();
            foreach (var task in document.Tasks.OrderBy(value => value.Id, StringComparer.Ordinal))
            {
                WriteTask(writer, task);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("taskHistory");
            writer.WriteStartArray();
            foreach (var history in document.TaskHistory.OrderBy(value => value.Id))
            {
                WriteHistory(writer, history);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("appSettings");
            if (document.AppSettings is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                WriteSettings(writer, document.AppSettings);
            }

            writer.WritePropertyName("reminders");
            using (var reminders = JsonDocument.Parse(ReminderExportJson.Serialize(document.Reminders)))
            {
                reminders.RootElement.WriteTo(writer);
            }

            writer.WritePropertyName("deliveryAttempts");
            writer.WriteStartArray();
            foreach (var attempt in document.DeliveryAttempts.OrderBy(value => value.AttemptId, StringComparer.Ordinal))
            {
                WriteAttempt(writer, attempt);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("deliveryAttemptEvents");
            writer.WriteStartArray();
            foreach (var attempt in (document.DeliveryAttemptEvents ?? Array.Empty<UserDeliveryAttemptEventExport>())
                .OrderBy(value => value.AttemptId, StringComparer.Ordinal)
                .ThenBy(value => value.EventOrdinal))
            {
                WriteAttemptEvent(writer, attempt);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("notificationPolicy");
            if (document.NotificationPolicyJson is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                using var policy = JsonDocument.Parse(document.NotificationPolicyJson);
                policy.RootElement.WriteTo(writer);
            }

            if (includeChecksum && document.Checksum is not null)
            {
                writer.WriteString("checksum", document.Checksum);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteTask(Utf8JsonWriter writer, UserTaskExport value)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("title", value.Title);
        writer.WriteString("timeType", value.TimeType);
        writer.WriteString("localDate", value.LocalDate);
        WriteOptional(writer, "timePoint", value.TimePoint);
        WriteOptional(writer, "rangeStart", value.RangeStart);
        WriteOptional(writer, "rangeEnd", value.RangeEnd);
        WriteOptional(writer, "result", value.Result);
        WriteOptional(writer, "resultRecordedAtUtc", value.ResultRecordedAtUtc);
        WriteOptional(writer, "resultNote", value.ResultNote);
        writer.WriteString("createdAtUtc", value.CreatedAtUtc);
        writer.WriteString("updatedAtUtc", value.UpdatedAtUtc);
        writer.WriteNumber("sortOrder", value.SortOrder);
        WriteOptional(writer, "continuedFromTaskId", value.ContinuedFromTaskId);
        writer.WriteEndObject();
    }

    private static void WriteHistory(Utf8JsonWriter writer, UserTaskHistoryExport value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", value.Id);
        writer.WriteString("taskId", value.TaskId);
        writer.WriteString("kind", value.Kind);
        writer.WriteString("occurredAtUtc", value.OccurredAtUtc);
        writer.WriteString("title", value.Title);
        writer.WriteString("timeType", value.TimeType);
        writer.WriteString("localDate", value.LocalDate);
        WriteOptional(writer, "timePoint", value.TimePoint);
        WriteOptional(writer, "rangeStart", value.RangeStart);
        WriteOptional(writer, "rangeEnd", value.RangeEnd);
        WriteOptional(writer, "result", value.Result);
        WriteOptional(writer, "resultRecordedAtUtc", value.ResultRecordedAtUtc);
        WriteOptional(writer, "resultNote", value.ResultNote);
        writer.WriteString("createdAtUtc", value.CreatedAtUtc);
        writer.WriteString("updatedAtUtc", value.UpdatedAtUtc);
        writer.WriteNumber("sortOrder", value.SortOrder);
        WriteOptional(writer, "continuedFromTaskId", value.ContinuedFromTaskId);
        WriteOptional(writer, "relatedTaskId", value.RelatedTaskId);
        writer.WriteEndObject();
    }

    private static void WriteSettings(Utf8JsonWriter writer, UserAppSettingsExport value)
    {
        writer.WriteStartObject();
        writer.WriteNumber("workdayBoundaryMinutes", value.WorkdayBoundaryMinutes);
        writer.WriteString("updatedAtUtc", value.UpdatedAtUtc);
        writer.WriteEndObject();
    }

    private static void WriteAttempt(Utf8JsonWriter writer, UserDeliveryAttemptExport value)
    {
        writer.WriteStartObject();
        writer.WriteString("attemptId", value.AttemptId);
        writer.WriteString("instanceId", value.InstanceId);
        writer.WriteString("channel", value.Channel);
        writer.WriteString("attemptedAtUtc", value.AttemptedAtUtc);
        writer.WriteString("outcome", value.Outcome);
        WriteOptional(writer, "errorCode", value.ErrorCode);
        writer.WriteEndObject();
    }

    private static void WriteAttemptEvent(Utf8JsonWriter writer, UserDeliveryAttemptEventExport value)
    {
        writer.WriteStartObject();
        writer.WriteString("attemptId", value.AttemptId);
        writer.WriteNumber("eventOrdinal", value.EventOrdinal);
        writer.WriteString("instanceId", value.InstanceId);
        writer.WriteString("scheduleId", value.ScheduleId);
        writer.WriteString("ruleId", value.RuleId);
        writer.WriteString("occurrenceId", value.OccurrenceId);
        writer.WriteString("logicalReminderId", value.LogicalReminderId);
        writer.WriteString("channel", value.Channel);
        writer.WriteString("correlationId", value.CorrelationId);
        writer.WriteString("requestId", value.RequestId);
        writer.WriteString("idempotencyKey", value.IdempotencyKey);
        writer.WriteString("purposeSnapshot", value.PurposeSnapshot);
        writer.WriteString("prioritySnapshot", value.PrioritySnapshot);
        writer.WriteBoolean("pinnedSnapshot", value.PinnedSnapshot);
        writer.WriteNumber("triggerAttemptOrdinal", value.TriggerAttemptOrdinal);
        writer.WriteString("triggeredAtUtc", value.TriggeredAtUtc);
        WriteOptional(writer, "scheduledTriggerAtUtc", value.ScheduledTriggerAtUtc);
        writer.WriteNumber("attemptNumber", value.AttemptNumber);
        writer.WriteString("state", value.State);
        WriteOptional(writer, "outcome", value.Outcome);
        writer.WriteString("createdAtUtc", value.CreatedAtUtc);
        writer.WriteString("updatedAtUtc", value.UpdatedAtUtc);
        WriteOptional(writer, "nextAttemptAtUtc", value.NextAttemptAtUtc);
        WriteOptional(writer, "errorCode", value.ErrorCode);
        writer.WriteBoolean("retryable", value.Retryable);
        writer.WriteNumber("revision", value.Revision);
        writer.WriteString("journalBatchId", value.JournalBatchId);
        writer.WriteString("receiptId", value.ReceiptId);
        writer.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static byte[] Canonicalize(ReadOnlySpan<byte> json) => ProtocolCanonicalization.Canonicalize(json);

    private static void ValidateDocument(UserDataExportDocument document, bool requireChecksum)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(document.Schema, UserDataExportContract.Schema, StringComparison.Ordinal) ||
            document.SchemaVersion != UserDataExportContract.SchemaVersion)
        {
            throw Error("export.schema.unsupported", "不支持的用户数据 export schema/version。", "schema");
        }

        if (document.GlobalRevision < 0)
        {
            throw Error("export.revision.invalid", "globalRevision 不能为负数。", "globalRevision");
        }

        if (document.Tasks is null || document.TaskHistory is null || document.Reminders is null || document.DeliveryAttempts is null)
        {
            throw Error("export.field.missing", "完整导出中的集合不能为空。");
        }

        RequireUnique(document.Tasks.Select(value => value.Id), "tasks");
        RequireUnique(document.TaskHistory.Select(value => value.Id.ToString(CultureInfo.InvariantCulture)), "taskHistory");
        RequireUnique(document.DeliveryAttempts.Select(value => value.AttemptId), "deliveryAttempts");
        RequireUnique(
            (document.DeliveryAttemptEvents ?? Array.Empty<UserDeliveryAttemptEventExport>())
                .Select(value => $"{value.AttemptId}:{value.EventOrdinal}"),
            "deliveryAttemptEvents");
        foreach (var task in document.Tasks)
        {
            ValidateUuid(task.Id, "tasks.id");
            if (string.IsNullOrWhiteSpace(task.Title))
            {
                throw Error("export.task.invalid", "Task title 不能为空。", "tasks.title");
            }

            ParseInstant(task.CreatedAtUtc, "tasks.createdAtUtc");
            ParseInstant(task.UpdatedAtUtc, "tasks.updatedAtUtc");
            if (task.ResultRecordedAtUtc is not null)
            {
                ParseInstant(task.ResultRecordedAtUtc, "tasks.resultRecordedAtUtc");
            }
        }

        if (document.NotificationPolicyJson is not null)
        {
            using var policy = ParseObject(document.NotificationPolicyJson, "notificationPolicy");
            EnsureSafePolicy(policy.RootElement);
        }

        foreach (var attempt in document.DeliveryAttemptEvents ?? Array.Empty<UserDeliveryAttemptEventExport>())
        {
            ValidateAttemptEvent(attempt);
        }

        _ = ReminderExportJson.Serialize(document.Reminders);
        if (requireChecksum && string.IsNullOrWhiteSpace(document.Checksum))
        {
            throw Error("export.checksum.missing", "完整导出必须带 checksum。", "checksum");
        }

        if (document.Checksum is not null &&
            (!document.Checksum.StartsWith(UserDataExportContract.ChecksumPrefix, StringComparison.Ordinal) ||
             document.Checksum.Length != UserDataExportContract.ChecksumPrefix.Length + 64 ||
             document.Checksum.Any(char.IsUpper)))
        {
            throw Error("export.checksum.invalid", "checksum 必须是 sha256: 加 64 位小写十六进制。", "checksum");
        }
    }

    private static void RequireUnique(IEnumerable<string> values, string field)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw Error("export.id.duplicate", $"{field} 中存在重复标识。", field);
            }
        }
    }

    private static void ValidateStableErrorCode(string? value, string field)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length == 0 || value.Length > 96 ||
            value.Any(character => character is not (>= 'a' and <= 'z') &&
                character is not (>= '0' and <= '9') &&
                character is not ('.' or '_' or '-')))
        {
            throw Error(
                "export.delivery_event.invalid",
                "delivery event errorCode 不是稳定错误码。",
                field);
        }
    }

    private static void ValidateUuid(string value, string field)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal) ||
            parsed.Version != 7)
        {
            throw Error("export.id.invalid", "导出标识必须是小写 UUID v7 D 格式。", field);
        }
    }

    private static void ValidateAttemptEvent(UserDeliveryAttemptEventExport value)
    {
        ValidateUuid(value.AttemptId, "deliveryAttemptEvents.attemptId");
        ValidateUuid(value.InstanceId, "deliveryAttemptEvents.instanceId");
        ValidateUuid(value.ScheduleId, "deliveryAttemptEvents.scheduleId");
        ValidateUuid(value.RuleId, "deliveryAttemptEvents.ruleId");
        ValidateUuid(value.OccurrenceId, "deliveryAttemptEvents.occurrenceId");
        ValidateUuid(value.LogicalReminderId, "deliveryAttemptEvents.logicalReminderId");
        ValidateUuid(value.CorrelationId, "deliveryAttemptEvents.correlationId");
        ValidateUuid(value.RequestId, "deliveryAttemptEvents.requestId");
        ValidateUuid(value.IdempotencyKey, "deliveryAttemptEvents.idempotencyKey");
        ValidateUuid(value.JournalBatchId, "deliveryAttemptEvents.journalBatchId");
        ValidateUuid(value.ReceiptId, "deliveryAttemptEvents.receiptId");
        if (value.EventOrdinal < 0 || value.AttemptNumber < 1 || value.TriggerAttemptOrdinal < 1 || value.Revision < 1)
        {
            throw Error("export.delivery_event.invalid", "deliveryAttemptEvents 的 ordinal、attempt 或 revision 无效。", "deliveryAttemptEvents");
        }

        // NotificationChannelId is a value object rather than an enum; its
        // parser plus the frozen channel allow-list rejects reserved values.
        try
        {
            NotificationChannels.RequireKnown(NotificationChannelId.Parse(value.Channel));
        }
        catch (Exception exception) when (exception is ArgumentException or NotificationContractException)
        {
            throw Error("export.delivery_event.invalid", "deliveryAttempts channel 无效。", "deliveryAttemptEvents.channel", exception);
        }

        try
        {
            _ = NotificationPurposeSnapshot.Parse(value.PurposeSnapshot);
            if (!Enum.TryParse<NotificationPriority>(value.PrioritySnapshot, false, out var priority) ||
                !Enum.IsDefined(priority))
            {
                throw new FormatException("priority snapshot is invalid");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or NotificationContractException)
        {
            throw Error("export.delivery_event.invalid", "deliveryAttemptEvents 的 purpose/priority snapshot 无效。", "deliveryAttemptEvents", exception);
        }

        _ = ParseInstant(value.TriggeredAtUtc, "deliveryAttemptEvents.triggeredAtUtc");
        if (value.ScheduledTriggerAtUtc is not null)
        {
            _ = ParseInstant(value.ScheduledTriggerAtUtc, "deliveryAttemptEvents.scheduledTriggerAtUtc");
        }

        _ = ParseInstant(value.CreatedAtUtc, "deliveryAttemptEvents.createdAtUtc");
        _ = ParseInstant(value.UpdatedAtUtc, "deliveryAttemptEvents.updatedAtUtc");
        if (value.NextAttemptAtUtc is not null)
        {
            _ = ParseInstant(value.NextAttemptAtUtc, "deliveryAttemptEvents.nextAttemptAtUtc");
        }

        if (!Enum.TryParse<NotificationDeliveryAttemptState>(value.State, false, out var state) ||
            !Enum.IsDefined(state))
        {
            throw Error("export.delivery_event.invalid", "deliveryAttemptEvents state 无效。", "deliveryAttemptEvents.state");
        }

        if (value.Outcome is not null &&
            (!Enum.TryParse<NotificationDeliveryOutcome>(value.Outcome, false, out var outcome) ||
             !Enum.IsDefined(outcome)))
        {
            throw Error("export.delivery_event.invalid", "deliveryAttemptEvents outcome 无效。", "deliveryAttemptEvents.outcome");
        }

        var pending = state == NotificationDeliveryAttemptState.PENDING;
        if (pending != (value.Outcome is null) ||
            (pending && (value.ErrorCode is not null || value.Retryable || value.NextAttemptAtUtc is not null)) ||
            (!pending && value.Outcome is null))
        {
            throw Error("export.delivery_event.invalid", "deliveryAttemptEvents 的 state/outcome 形状不一致。", "deliveryAttemptEvents");
        }

        if (!pending)
        {
            NotificationDeliveryOutcome? expectedOutcome = state switch
            {
                NotificationDeliveryAttemptState.DELIVERED => NotificationDeliveryOutcome.DELIVERED,
                NotificationDeliveryAttemptState.BLOCKED => NotificationDeliveryOutcome.BLOCKED,
                NotificationDeliveryAttemptState.UNAVAILABLE => NotificationDeliveryOutcome.UNAVAILABLE,
                NotificationDeliveryAttemptState.FAILED => NotificationDeliveryOutcome.FAILED,
                NotificationDeliveryAttemptState.SUPPRESSED_QUIET_HOURS => NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS,
                NotificationDeliveryAttemptState.NOT_ATTEMPTED => NotificationDeliveryOutcome.NOT_ATTEMPTED,
                _ => null
            };
            if (expectedOutcome is null ||
                !Enum.TryParse<NotificationDeliveryOutcome>(value.Outcome, false, out var actualOutcome) ||
                actualOutcome != expectedOutcome ||
                (actualOutcome == NotificationDeliveryOutcome.DELIVERED
                    ? value.ErrorCode is not null
                    : value.ErrorCode is null))
            {
                throw Error("export.delivery_event.invalid", "deliveryAttemptEvents 的终态、outcome 和 errorCode 不一致。", "deliveryAttemptEvents");
            }

            ValidateStableErrorCode(value.ErrorCode, "deliveryAttemptEvents.errorCode");
            if (value.Retryable != (value.NextAttemptAtUtc is not null))
            {
                throw Error("export.delivery_event.invalid", "可重试 delivery event 必须带 nextAttemptAtUtc，终态事件不可反之。", "deliveryAttemptEvents");
            }
        }

        var createdAtUtc = ParseInstant(value.CreatedAtUtc, "deliveryAttemptEvents.createdAtUtc");
        var updatedAtUtc = ParseInstant(value.UpdatedAtUtc, "deliveryAttemptEvents.updatedAtUtc");
        if (updatedAtUtc < createdAtUtc ||
            value.NextAttemptAtUtc is { } nextAttempt &&
            ParseInstant(nextAttempt, "deliveryAttemptEvents.nextAttemptAtUtc") < updatedAtUtc)
        {
            throw Error("export.delivery_event.invalid", "delivery event 的时间顺序无效。", "deliveryAttemptEvents");
        }
    }

    private static Instant ParseInstant(string value, string field)
    {
        var parsed = InstantPattern.Parse(value);
        if (!parsed.Success || !string.Equals(InstantPattern.Format(parsed.Value), value, StringComparison.Ordinal))
        {
            throw Error("export.time.invalid", "时间必须使用 canonical UTC Instant 格式。", field);
        }

        return parsed.Value;
    }

    private static JsonDocument ParseObject(string json, string field)
    {
        try
        {
            var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            EnsureNoDuplicateProperties(parsed.RootElement);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                parsed.Dispose();
                throw Error("export.field.type", $"{field} 必须是 object。", field);
            }

            return parsed;
        }
        catch (UserDataExportContractException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Error("export.field.type", $"{field} 不是有效 JSON object。", field, exception);
        }
    }

    private static void EnsureSafePolicy(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("machine", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("sid", StringComparison.OrdinalIgnoreCase))
            {
                throw Error("export.field.sensitive", "notification policy 不得包含 secret、machine 或用户标识。", property.Name);
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                EnsureSafePolicy(property.Value);
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in property.Value.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        EnsureSafePolicy(value);
                    }
                }
            }
        }
    }

    private static void EnsureNoDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    throw Error("export.field.duplicate", "JSON object 不得包含重复字段。", property.Name);
                }

                EnsureNoDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                EnsureNoDuplicateProperties(child);
            }
        }
    }

    private static void RequireProperties(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out _))
            {
                throw Error("export.field.missing", $"导出缺少 {name}。", name);
            }
        }
    }

    private static UserTaskExport ToTask(WireTask value) => new(
        value.Id ?? throw Error("export.field.missing", "Task 缺少 id。", "tasks.id"),
        value.Title ?? throw Error("export.field.missing", "Task 缺少 title。", "tasks.title"),
        value.TimeType ?? throw Error("export.field.missing", "Task 缺少 timeType。", "tasks.timeType"),
        value.LocalDate ?? throw Error("export.field.missing", "Task 缺少 localDate。", "tasks.localDate"),
        value.TimePoint,
        value.RangeStart,
        value.RangeEnd,
        value.Result,
        value.ResultRecordedAtUtc,
        value.ResultNote,
        value.CreatedAtUtc ?? throw Error("export.field.missing", "Task 缺少 createdAtUtc。", "tasks.createdAtUtc"),
        value.UpdatedAtUtc ?? throw Error("export.field.missing", "Task 缺少 updatedAtUtc。", "tasks.updatedAtUtc"),
        value.SortOrder,
        value.ContinuedFromTaskId);

    private static UserTaskHistoryExport ToHistory(WireHistory value) => new(
        value.Id,
        value.TaskId ?? throw Error("export.field.missing", "Task history 缺少 taskId。", "taskHistory.taskId"),
        value.Kind ?? throw Error("export.field.missing", "Task history 缺少 kind。", "taskHistory.kind"),
        value.OccurredAtUtc ?? throw Error("export.field.missing", "Task history 缺少 occurredAtUtc。", "taskHistory.occurredAtUtc"),
        value.Title ?? throw Error("export.field.missing", "Task history 缺少 title。", "taskHistory.title"),
        value.TimeType ?? throw Error("export.field.missing", "Task history 缺少 timeType。", "taskHistory.timeType"),
        value.LocalDate ?? throw Error("export.field.missing", "Task history 缺少 localDate。", "taskHistory.localDate"),
        value.TimePoint,
        value.RangeStart,
        value.RangeEnd,
        value.Result,
        value.ResultRecordedAtUtc,
        value.ResultNote,
        value.CreatedAtUtc ?? throw Error("export.field.missing", "Task history 缺少 createdAtUtc。", "taskHistory.createdAtUtc"),
        value.UpdatedAtUtc ?? throw Error("export.field.missing", "Task history 缺少 updatedAtUtc。", "taskHistory.updatedAtUtc"),
        value.SortOrder,
        value.ContinuedFromTaskId,
        value.RelatedTaskId);

    private static UserAppSettingsExport ToSettings(WireSettings value) => new(
        value.WorkdayBoundaryMinutes,
        value.UpdatedAtUtc ?? throw Error("export.field.missing", "appSettings 缺少 updatedAtUtc。", "appSettings.updatedAtUtc"));

    private static UserDeliveryAttemptExport ToAttempt(WireAttempt value) => new(
        value.AttemptId ?? throw Error("export.field.missing", "delivery attempt 缺少 attemptId。", "deliveryAttempts.attemptId"),
        value.InstanceId ?? throw Error("export.field.missing", "delivery attempt 缺少 instanceId。", "deliveryAttempts.instanceId"),
        value.Channel ?? throw Error("export.field.missing", "delivery attempt 缺少 channel。", "deliveryAttempts.channel"),
        value.AttemptedAtUtc ?? throw Error("export.field.missing", "delivery attempt 缺少 attemptedAtUtc。", "deliveryAttempts.attemptedAtUtc"),
        value.Outcome ?? throw Error("export.field.missing", "delivery attempt 缺少 outcome。", "deliveryAttempts.outcome"),
        value.ErrorCode);

    private static UserDeliveryAttemptEventExport ToAttemptEvent(WireAttemptEvent value) => new(
        value.AttemptId ?? throw Error("export.field.missing", "delivery event 缺少 attemptId。", "deliveryAttemptEvents.attemptId"),
        value.EventOrdinal,
        value.InstanceId ?? throw Error("export.field.missing", "delivery event 缺少 instanceId。", "deliveryAttemptEvents.instanceId"),
        value.ScheduleId ?? throw Error("export.field.missing", "delivery event 缺少 scheduleId。", "deliveryAttemptEvents.scheduleId"),
        value.RuleId ?? throw Error("export.field.missing", "delivery event 缺少 ruleId。", "deliveryAttemptEvents.ruleId"),
        value.OccurrenceId ?? throw Error("export.field.missing", "delivery event 缺少 occurrenceId。", "deliveryAttemptEvents.occurrenceId"),
        value.LogicalReminderId ?? throw Error("export.field.missing", "delivery event 缺少 logicalReminderId。", "deliveryAttemptEvents.logicalReminderId"),
        value.Channel ?? throw Error("export.field.missing", "delivery event 缺少 channel。", "deliveryAttemptEvents.channel"),
        value.CorrelationId ?? throw Error("export.field.missing", "delivery event 缺少 correlationId。", "deliveryAttemptEvents.correlationId"),
        value.RequestId ?? throw Error("export.field.missing", "delivery event 缺少 requestId。", "deliveryAttemptEvents.requestId"),
        value.IdempotencyKey ?? throw Error("export.field.missing", "delivery event 缺少 idempotencyKey。", "deliveryAttemptEvents.idempotencyKey"),
        value.PurposeSnapshot ?? throw Error("export.field.missing", "delivery event 缺少 purposeSnapshot。", "deliveryAttemptEvents.purposeSnapshot"),
        value.PrioritySnapshot ?? throw Error("export.field.missing", "delivery event 缺少 prioritySnapshot。", "deliveryAttemptEvents.prioritySnapshot"),
        value.PinnedSnapshot,
        value.TriggerAttemptOrdinal,
        value.TriggeredAtUtc ?? throw Error("export.field.missing", "delivery event 缺少 triggeredAtUtc。", "deliveryAttemptEvents.triggeredAtUtc"),
        value.ScheduledTriggerAtUtc,
        value.AttemptNumber,
        value.State ?? throw Error("export.field.missing", "delivery event 缺少 state。", "deliveryAttemptEvents.state"),
        value.Outcome,
        value.CreatedAtUtc ?? throw Error("export.field.missing", "delivery event 缺少 createdAtUtc。", "deliveryAttemptEvents.createdAtUtc"),
        value.UpdatedAtUtc ?? throw Error("export.field.missing", "delivery event 缺少 updatedAtUtc。", "deliveryAttemptEvents.updatedAtUtc"),
        value.NextAttemptAtUtc,
        value.ErrorCode,
        value.Retryable,
        value.Revision,
        value.JournalBatchId ?? throw Error("export.field.missing", "delivery event 缺少 journalBatchId。", "deliveryAttemptEvents.journalBatchId"),
        value.ReceiptId ?? throw Error("export.field.missing", "delivery event 缺少 receiptId。", "deliveryAttemptEvents.receiptId"));

    private static UserDataExportContractException Error(
        string code,
        string message,
        string? field = null,
        Exception? innerException = null) =>
        new(code, message, field, innerException);

    private sealed class WireDocument
    {
        [JsonPropertyName("schema")] public string? Schema { get; set; }
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("globalRevision")] public long GlobalRevision { get; set; }
        [JsonPropertyName("tasks")] public List<WireTask>? Tasks { get; set; }
        [JsonPropertyName("taskHistory")] public List<WireHistory>? TaskHistory { get; set; }
        [JsonPropertyName("appSettings")] public WireSettings? AppSettings { get; set; }
        [JsonPropertyName("reminders")] public JsonElement? Reminders { get; set; }
        [JsonPropertyName("deliveryAttempts")] public List<WireAttempt>? DeliveryAttempts { get; set; }
        [JsonPropertyName("deliveryAttemptEvents")] public List<WireAttemptEvent>? DeliveryAttemptEvents { get; set; }
        [JsonPropertyName("notificationPolicy")] public JsonElement? NotificationPolicy { get; set; }
        [JsonPropertyName("checksum")] public string? Checksum { get; set; }
    }

    private sealed class WireTask
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("timeType")] public string? TimeType { get; set; }
        [JsonPropertyName("localDate")] public string? LocalDate { get; set; }
        [JsonPropertyName("timePoint")] public string? TimePoint { get; set; }
        [JsonPropertyName("rangeStart")] public string? RangeStart { get; set; }
        [JsonPropertyName("rangeEnd")] public string? RangeEnd { get; set; }
        [JsonPropertyName("result")] public string? Result { get; set; }
        [JsonPropertyName("resultRecordedAtUtc")] public string? ResultRecordedAtUtc { get; set; }
        [JsonPropertyName("resultNote")] public string? ResultNote { get; set; }
        [JsonPropertyName("createdAtUtc")] public string? CreatedAtUtc { get; set; }
        [JsonPropertyName("updatedAtUtc")] public string? UpdatedAtUtc { get; set; }
        [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }
        [JsonPropertyName("continuedFromTaskId")] public string? ContinuedFromTaskId { get; set; }
    }

    private sealed class WireHistory
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("taskId")] public string? TaskId { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("occurredAtUtc")] public string? OccurredAtUtc { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("timeType")] public string? TimeType { get; set; }
        [JsonPropertyName("localDate")] public string? LocalDate { get; set; }
        [JsonPropertyName("timePoint")] public string? TimePoint { get; set; }
        [JsonPropertyName("rangeStart")] public string? RangeStart { get; set; }
        [JsonPropertyName("rangeEnd")] public string? RangeEnd { get; set; }
        [JsonPropertyName("result")] public string? Result { get; set; }
        [JsonPropertyName("resultRecordedAtUtc")] public string? ResultRecordedAtUtc { get; set; }
        [JsonPropertyName("resultNote")] public string? ResultNote { get; set; }
        [JsonPropertyName("createdAtUtc")] public string? CreatedAtUtc { get; set; }
        [JsonPropertyName("updatedAtUtc")] public string? UpdatedAtUtc { get; set; }
        [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }
        [JsonPropertyName("continuedFromTaskId")] public string? ContinuedFromTaskId { get; set; }
        [JsonPropertyName("relatedTaskId")] public string? RelatedTaskId { get; set; }
    }

    private sealed class WireSettings
    {
        [JsonPropertyName("workdayBoundaryMinutes")] public int WorkdayBoundaryMinutes { get; set; }
        [JsonPropertyName("updatedAtUtc")] public string? UpdatedAtUtc { get; set; }
    }

    private sealed class WireAttempt
    {
        [JsonPropertyName("attemptId")] public string? AttemptId { get; set; }
        [JsonPropertyName("instanceId")] public string? InstanceId { get; set; }
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("attemptedAtUtc")] public string? AttemptedAtUtc { get; set; }
        [JsonPropertyName("outcome")] public string? Outcome { get; set; }
        [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
    }

    private sealed class WireAttemptEvent
    {
        [JsonPropertyName("attemptId")] public string? AttemptId { get; set; }
        [JsonPropertyName("eventOrdinal")] public long EventOrdinal { get; set; }
        [JsonPropertyName("instanceId")] public string? InstanceId { get; set; }
        [JsonPropertyName("scheduleId")] public string? ScheduleId { get; set; }
        [JsonPropertyName("ruleId")] public string? RuleId { get; set; }
        [JsonPropertyName("occurrenceId")] public string? OccurrenceId { get; set; }
        [JsonPropertyName("logicalReminderId")] public string? LogicalReminderId { get; set; }
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("correlationId")] public string? CorrelationId { get; set; }
        [JsonPropertyName("requestId")] public string? RequestId { get; set; }
        [JsonPropertyName("idempotencyKey")] public string? IdempotencyKey { get; set; }
        [JsonPropertyName("purposeSnapshot")] public string? PurposeSnapshot { get; set; }
        [JsonPropertyName("prioritySnapshot")] public string? PrioritySnapshot { get; set; }
        [JsonPropertyName("pinnedSnapshot")] public bool PinnedSnapshot { get; set; }
        [JsonPropertyName("triggerAttemptOrdinal")] public int TriggerAttemptOrdinal { get; set; }
        [JsonPropertyName("triggeredAtUtc")] public string? TriggeredAtUtc { get; set; }
        [JsonPropertyName("scheduledTriggerAtUtc")] public string? ScheduledTriggerAtUtc { get; set; }
        [JsonPropertyName("attemptNumber")] public int AttemptNumber { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("outcome")] public string? Outcome { get; set; }
        [JsonPropertyName("createdAtUtc")] public string? CreatedAtUtc { get; set; }
        [JsonPropertyName("updatedAtUtc")] public string? UpdatedAtUtc { get; set; }
        [JsonPropertyName("nextAttemptAtUtc")] public string? NextAttemptAtUtc { get; set; }
        [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
        [JsonPropertyName("retryable")] public bool Retryable { get; set; }
        [JsonPropertyName("revision")] public long Revision { get; set; }
        [JsonPropertyName("journalBatchId")] public string? JournalBatchId { get; set; }
        [JsonPropertyName("receiptId")] public string? ReceiptId { get; set; }
    }
}

/// <summary>
/// Production export entry. The source connection is OS read-only and the
/// context is never saved. The returned bytes can be written to an explicit
/// artifact path outside Active by <see cref="WriteArtifactAsync"/>.
/// </summary>
public static class UserDataExportService
{
    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture(UserDataExportContract.InstantPattern);
    private static readonly LocalDatePattern LocalDatePattern =
        NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("uuuu-MM-dd");
    private static readonly LocalTimePattern LocalTimePattern =
        NodaTime.Text.LocalTimePattern.CreateWithInvariantCulture("HH:mm:ss.FFFFFFFFF");

    public static async ValueTask<UserDataExportArtifact> CreateAsync(
        string databasePath,
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var policyJson = ReadNotificationPolicy(dataRoot);
        await using var connection = await P25ReadOnlyConnectionFactory
            .OpenAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
        await using var context = ReminNoteDatabase.CreateContext(connection);

        var revisions = await context.RevisionStates
            .AsNoTracking()
            .OrderByDescending(value => value.CurrentRevision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (revisions.Count != 1)
        {
            throw new UserDataExportContractException(
                "export.profile_ambiguous",
                "导出要求数据库只包含一个已解析 profile。", "revision_state");
        }

        var tasks = await context.Tasks.AsNoTracking().OrderBy(value => value.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var history = await context.TaskHistory.AsNoTracking().OrderBy(value => value.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var settings = await context.AppSettings.AsNoTracking().OrderBy(value => value.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var rules = await context.ReminderRules.AsNoTracking().OrderBy(value => value.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var schedules = await context.ReminderSchedules.AsNoTracking().OrderBy(value => value.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var instances = await context.ReminderInstances.AsNoTracking().OrderBy(value => value.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var attempts = await context.ReminderDeliveryAttempts.AsNoTracking().OrderBy(value => value.AttemptId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var deliveryAttemptEvents = await ReadDeliveryAttemptEventsAsync(
                connection,
                revisions[0].ProfileScope,
                cancellationToken)
            .ConfigureAwait(false);

        var reminders = ReminderExportDocument.Create(
            rules.Select(ToReminderRule),
            schedules.Select(ToReminderSchedule),
            instances.Select(ToReminderInstance));
        var document = new UserDataExportDocument(
            UserDataExportContract.Schema,
            UserDataExportContract.SchemaVersion,
            revisions[0].CurrentRevision,
            tasks.Select(ToTask).ToArray(),
            history.Select(ToHistory).ToArray(),
            settings is null ? null : new UserAppSettingsExport(settings.WorkdayBoundaryMinutes, FormatInstant(settings.UpdatedAt)),
            reminders,
            attempts.Select(ToAttempt).ToArray(),
            policyJson,
            null,
            deliveryAttemptEvents);
        var content = UserDataExportJson.Serialize(document);
        var parsed = UserDataExportJson.Parse(content);
        return new UserDataExportArtifact(parsed, content, parsed.Checksum!);
    }

    public static async ValueTask<UserDataExportArtifact> WriteArtifactAsync(
        UserDataExportArtifact artifact,
        string artifactPath,
        string activeDatabasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeDatabasePath);
        var target = Path.GetFullPath(artifactPath);
        var active = Path.GetFullPath(activeDatabasePath);
        if (target.Equals(active, StringComparison.OrdinalIgnoreCase))
        {
            throw new UserDataExportContractException("export.active_write_forbidden", "导出 artifact 不能覆盖 Active 数据库。", "artifactPath");
        }

        if (artifact.Content.Length > UserDataExportContract.MaxArtifactBytes)
        {
            throw new UserDataExportContractException("export.invalid_size", "导出 artifact 超出大小上限。", "artifactPath");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("artifactPath 必须包含父目录。", nameof(artifactPath));
        Directory.CreateDirectory(parent);
        var partial = target + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(partial, artifact.Content, cancellationToken).ConfigureAwait(false);
            File.Move(partial, target, overwrite: false);
            return artifact with { Path = target };
        }
        catch
        {
            try
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
            }
            catch
            {
                // Preserve the original write failure; the partial name is
                // unique and can be diagnosed/cleaned by the caller.
            }

            throw;
        }
    }

    private static string? ReadNotificationPolicy(string dataRoot)
    {
        var path = Path.Combine(Path.GetFullPath(dataRoot), "notification-policy.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 256 * 1024)
        {
            throw new UserDataExportContractException("export.policy_too_large", "notification policy 超出大小上限。", "notificationPolicy");
        }

        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new UserDataExportContractException("export.policy_invalid", "notification policy 必须是 object。", "notificationPolicy");
        }

        return Encoding.UTF8.GetString(ProtocolCanonicalization.Canonicalize(document.RootElement));
    }

    private static UserTaskExport ToTask(TaskEntity value) => new(
        value.Id.ToString("D"),
        value.Title,
        value.TimeType.ToString(),
        LocalDatePattern.Format(value.LocalDate),
        value.TimePoint is { } point ? LocalTimePattern.Format(point) : null,
        value.RangeStart is { } start ? LocalTimePattern.Format(start) : null,
        value.RangeEnd is { } end ? LocalTimePattern.Format(end) : null,
        value.Result?.ToString(),
        value.ResultRecordedAt is { } recorded ? FormatInstant(recorded) : null,
        value.ResultNote,
        FormatInstant(value.CreatedAt),
        FormatInstant(value.UpdatedAt),
        value.SortOrder,
        value.ContinuedFromTaskId?.ToString("D"));

    private static async ValueTask<IReadOnlyList<UserDeliveryAttemptEventExport>> ReadDeliveryAttemptEventsAsync(
        SqliteConnection connection,
        string profileScope,
        CancellationToken cancellationToken)
    {
        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'notification_delivery_attempt_events' LIMIT 1;";
        if (await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return Array.Empty<UserDeliveryAttemptEventExport>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_id, event_ordinal, instance_id, schedule_id, rule_id,
                   occurrence_id, logical_reminder_id, channel, correlation_id,
                   request_id, idempotency_key, purpose_snapshot, priority_snapshot,
                   pinned_snapshot, trigger_attempt_ordinal, triggered_at_utc,
                   scheduled_trigger_at_utc, attempt_number, state, outcome,
                   created_at_utc, updated_at_utc, next_attempt_at_utc, error_code,
                   retryable, revision, journal_batch_id, receipt_id
            FROM notification_delivery_attempt_events
            WHERE profile_scope = $profileScope
            ORDER BY attempt_id, event_ordinal;
            """;
        command.Parameters.AddWithValue("$profileScope", profileScope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<UserDeliveryAttemptEventExport>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new UserDeliveryAttemptEventExport(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetInt64(13) != 0,
                reader.GetInt32(14),
                reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.GetInt32(17),
                reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.GetString(20),
                reader.GetString(21),
                reader.IsDBNull(22) ? null : reader.GetString(22),
                reader.IsDBNull(23) ? null : reader.GetString(23),
                reader.GetInt64(24) != 0,
                reader.GetInt64(25),
                reader.GetString(26),
                reader.GetString(27)));
        }

        return result;
    }

    private static UserTaskHistoryExport ToHistory(TaskHistoryEntity value) => new(
        value.Id,
        value.TaskId.ToString("D"),
        value.Kind.ToString(),
        FormatInstant(value.OccurredAt),
        value.Title,
        value.TimeType.ToString(),
        LocalDatePattern.Format(value.LocalDate),
        value.TimePoint is { } point ? LocalTimePattern.Format(point) : null,
        value.RangeStart is { } start ? LocalTimePattern.Format(start) : null,
        value.RangeEnd is { } end ? LocalTimePattern.Format(end) : null,
        value.Result?.ToString(),
        value.ResultRecordedAt is { } recorded ? FormatInstant(recorded) : null,
        value.ResultNote,
        FormatInstant(value.CreatedAt),
        FormatInstant(value.UpdatedAt),
        value.SortOrder,
        value.ContinuedFromTaskId?.ToString("D"),
        value.RelatedTaskId?.ToString("D"));

    private static ReminderRuleExport ToReminderRule(ReminderRuleEntity value) => new(
        value.Id.ToString("D"),
        value.TargetKind.ToString(),
        value.TargetId.ToString("D"),
        value.OccurrenceId.ToString("D"),
        value.Purpose.ToString(),
        new ReminderTimingExport(
            value.TimingKind.ToString(),
            value.TimingAnchor?.ToString(),
            value.OffsetSeconds,
            value.AbsoluteAtUtc is { } absolute ? FormatInstant(absolute) : null),
        value.Priority.ToString(),
        value.Pinned,
        new ReminderRepeatPolicyExport(value.RepeatEnabled, value.RepeatIntervalSeconds, value.RepeatMaxCount),
        value.WakePolicy.ToString(),
        value.Enabled,
        value.RuleRevision,
        FormatInstant(value.CreatedAtUtc),
        FormatInstant(value.UpdatedAtUtc));

    private static ReminderScheduleExport ToReminderSchedule(ReminderScheduleEntity value) => new(
        value.Id.ToString("D"),
        value.RuleId.ToString("D"),
        value.OccurrenceId.ToString("D"),
        value.LogicalReminderId.ToString("D"),
        value.OriginScheduleId?.ToString("D"),
        value.Cause.ToString(),
        value.RuleRevision,
        value.ScheduleRevision,
        FormatInstant(value.TriggerAtUtc),
        value.TimeZoneId,
        value.State.ToString(),
        value.TerminalReason?.ToString(),
        value.ReplacementScheduleId?.ToString("D"),
        FormatInstant(value.CreatedAtUtc),
        value.TerminalAtUtc is { } terminal ? FormatInstant(terminal) : null);

    private static ReminderInstanceExport ToReminderInstance(ReminderInstanceEntity value) => new(
        value.Id.ToString("D"),
        value.ScheduleId.ToString("D"),
        value.RuleId.ToString("D"),
        value.OccurrenceId.ToString("D"),
        value.LogicalReminderId.ToString("D"),
        value.AttemptOrdinal,
        value.PurposeSnapshot.ToString(),
        value.PrioritySnapshot.ToString(),
        value.PinnedSnapshot,
        FormatInstant(value.TriggeredAtUtc),
        value.Lifecycle.ToString(),
        value.ReadAtUtc is { } read ? FormatInstant(read) : null,
        value.ResolvedAtUtc is { } resolved ? FormatInstant(resolved) : null,
        value.ResolutionAction?.ToString());

    private static UserDeliveryAttemptExport ToAttempt(ReminderDeliveryAttemptEntity value) => new(
        value.AttemptId.ToString("D"),
        value.InstanceId.ToString("D"),
        value.Channel.ToString(),
        FormatInstant(value.AttemptedAtUtc),
        value.Outcome.ToString(),
        value.ErrorCode);

    private static string FormatInstant(Instant value) => InstantPattern.Format(value);
}

/// <summary>
/// Explicit Candidate restore entry. Validation and staging are side
/// effect free in dry-run mode. Confirmed import creates a new independent
/// SQLite Candidate and never opens the Active database for writing. Pending
/// schedules are intentionally not inserted into the Candidate scheduler
/// projection; the staged source must pass the separate Candidate verifier
/// before an operator-controlled promotion.
/// </summary>
public static class UserDataRestoreService
{
    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture(UserDataExportContract.InstantPattern);
    private static readonly LocalDatePattern LocalDatePattern =
        NodaTime.Text.LocalDatePattern.CreateWithInvariantCulture("uuuu-MM-dd");
    private static readonly LocalTimePattern LocalTimePattern =
        NodaTime.Text.LocalTimePattern.CreateWithInvariantCulture("HH:mm:ss.FFFFFFFFF");

    public static async ValueTask<UserDataRestoreResult> ValidateAndStageAsync(
        UserDataRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePaths(request);
        var bytes = await File.ReadAllBytesAsync(request.ArtifactPath, cancellationToken).ConfigureAwait(false);
        var document = UserDataExportJson.Parse(bytes);
        var plan = ReminderRestorePlanner.CreatePlan(new ReminderRestorePlanRequest(
            document.Reminders,
            ReminderRestoreDestination.Candidate,
            request.DryRun,
            request.Confirmed,
            CandidatePipelineReady: !request.DryRun,
            CurrentGlobalRevision: document.GlobalRevision,
            ExpectedGlobalRevision: document.GlobalRevision));
        if (request.DryRun)
        {
            return new(UserDataRestoreStatus.DryRun, document.Checksum!, null, null, plan, Describe(plan));
        }

        if (plan.Status == ReminderRestorePlanStatus.Conflict)
        {
            return new(UserDataRestoreStatus.Conflict, document.Checksum!, null, null, plan, Describe(plan));
        }

        if (plan.Status != ReminderRestorePlanStatus.Ready || !request.Confirmed)
        {
            return new(UserDataRestoreStatus.RequiresConfirmation, document.Checksum!, null, null, plan, Describe(plan));
        }

        var runId = Guid.NewGuid().ToString("N");
        var stageDirectory = Path.Combine(Path.GetFullPath(request.CandidateRoot), "recovery", "staging", runId);
        Directory.CreateDirectory(stageDirectory);
        var stagedPath = Path.Combine(stageDirectory, "structured-export.json");
        await File.WriteAllBytesAsync(stagedPath, bytes, cancellationToken).ConfigureAwait(false);
        if (document.NotificationPolicyJson is not null)
        {
            // Keep the policy sidecar beside the Candidate artifact. It is
            // part of the user-data export but deliberately remains outside
            // SQLite and cannot affect Active until a later controlled
            // promotion explicitly adopts the complete Candidate generation.
            await File.WriteAllTextAsync(
                    Path.Combine(stageDirectory, "notification-policy.json"),
                    document.NotificationPolicyJson,
                    new UTF8Encoding(false),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!request.ImportCandidate)
        {
            return new(UserDataRestoreStatus.Staged, document.Checksum!, stagedPath, null, plan, Describe(plan));
        }

        var candidatePath = Path.Combine(stageDirectory, "candidate.sqlite");
        await ImportCandidateAsync(document, candidatePath, request.ProfileScope, cancellationToken).ConfigureAwait(false);
        var marker = new
        {
            schema = UserDataExportContract.Schema,
            schemaVersion = UserDataExportContract.SchemaVersion,
            sourceChecksum = document.Checksum,
            candidateDatabase = Path.GetFileName(candidatePath),
            notificationPolicy = document.NotificationPolicyJson is null
                ? null
                : "notification-policy.json",
            pendingSchedulesDeferred = plan.PendingSchedulesDeferred,
            activeWrite = false
        };
        await File.WriteAllTextAsync(
                Path.Combine(stageDirectory, "candidate-restore.json"),
                JsonSerializer.Serialize(marker),
                new UTF8Encoding(false),
                cancellationToken)
            .ConfigureAwait(false);
        return new(UserDataRestoreStatus.Imported, document.Checksum!, stagedPath, candidatePath, plan, Describe(plan));
    }

    private static async ValueTask ImportCandidateAsync(
        UserDataExportDocument document,
        string candidatePath,
        string profileScope,
        CancellationToken cancellationToken)
    {
        ProtocolProfileScope.Validate(profileScope);
        if (File.Exists(candidatePath))
        {
            throw new UserDataExportContractException("restore.candidate.exists", "Candidate 数据库已存在，拒绝覆盖。", "candidatePath");
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = candidatePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        HashSet<Guid> importedInstanceIds = [];
        try
        {
            await using (var context = ReminNoteDatabase.CreateContext(connection))
            {
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                await P25StorageSchema.EnsureProfileAsync(connection, profileScope, requireWal: true, cancellationToken).ConfigureAwait(false);
                var taskEntities = document.Tasks.Select(ToTaskEntity).ToArray();
                var historyEntities = document.TaskHistory.Select(ToHistoryEntity).ToArray();
                var rules = document.Reminders.Rules.Select(ToRuleEntity).ToArray();
                var ruleById = document.Reminders.Rules.ToDictionary(value => value.Id, StringComparer.Ordinal);
                var schedules = document.Reminders.Schedules
                    .Where(value => string.Equals(value.State, ReminderExportValues.SchedulePending, StringComparison.Ordinal) == false)
                    .Select(value => ToScheduleEntity(value, ruleById))
                    .ToArray();
                var scheduleIds = schedules.Select(value => value.Id).ToHashSet();
                var instances = document.Reminders.Instances
                    .Where(value => scheduleIds.Contains(Guid.Parse(value.ScheduleId)))
                    .Select(ToInstanceEntity)
                    .ToArray();
                var attempts = document.DeliveryAttempts
                    .Where(value => Guid.TryParse(value.InstanceId, out var id) && instances.Any(instance => instance.Id == id))
                    .Select(ToAttemptEntity)
                    .ToArray();
                importedInstanceIds = instances.Select(instance => instance.Id).ToHashSet();

                context.Tasks.AddRange(taskEntities);
                context.TaskHistory.AddRange(historyEntities);
                if (document.AppSettings is not null)
                {
                    var settings = await context.AppSettings
                        .SingleOrDefaultAsync(value => value.Id == 1, cancellationToken)
                        .ConfigureAwait(false);
                    if (settings is null)
                    {
                        settings = new AppSettingsEntity { Id = 1 };
                        context.AppSettings.Add(settings);
                    }

                    settings.WorkdayBoundaryMinutes = document.AppSettings.WorkdayBoundaryMinutes;
                    settings.UpdatedAt = ParseInstant(document.AppSettings.UpdatedAtUtc);
                }

                context.ReminderRules.AddRange(rules);
                context.ReminderSchedules.AddRange(schedules);
                context.ReminderInstances.AddRange(instances);
                context.ReminderDeliveryAttempts.AddRange(attempts);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            // P3-04 is an append-only table managed by the Agent storage
            // writer rather than EF. Rebind its profile scope to the
            // Candidate and keep only events whose instances survived the
            // intentional pending-schedule deferral.
            var importedEvents = (document.DeliveryAttemptEvents ?? Array.Empty<UserDeliveryAttemptEventExport>())
                .Where(value => importedInstanceIds.Contains(ParseUuid(value.InstanceId)))
                .ToArray();
            await InsertDeliveryAttemptEventsAsync(
                    connection,
                    profileScope,
                    importedEvents,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            TryDeleteCandidate(candidatePath);
            throw;
        }
    }

    private static TaskEntity ToTaskEntity(UserTaskExport value)
    {
        var result = new TaskEntity
        {
            Id = ParseUuid(value.Id),
            Title = value.Title,
            TimeType = Enum.Parse<TaskTimeType>(value.TimeType, false),
            LocalDate = LocalDatePattern.Parse(value.LocalDate).GetValueOrThrow(),
            Result = value.Result is null ? null : Enum.Parse<TaskResult>(value.Result, false),
            ResultRecordedAt = value.ResultRecordedAtUtc is null ? null : ParseInstant(value.ResultRecordedAtUtc),
            ResultNote = value.ResultNote,
            CreatedAt = ParseInstant(value.CreatedAtUtc),
            UpdatedAt = ParseInstant(value.UpdatedAtUtc),
            SortOrder = value.SortOrder,
            ContinuedFromTaskId = value.ContinuedFromTaskId is null ? null : ParseUuid(value.ContinuedFromTaskId)
        };
        result.TimePoint = value.TimePoint is null ? null : LocalTimePattern.Parse(value.TimePoint).GetValueOrThrow();
        result.RangeStart = value.RangeStart is null ? null : LocalTimePattern.Parse(value.RangeStart).GetValueOrThrow();
        result.RangeEnd = value.RangeEnd is null ? null : LocalTimePattern.Parse(value.RangeEnd).GetValueOrThrow();
        _ = result.ToDomain();
        return result;
    }

    private static TaskHistoryEntity ToHistoryEntity(UserTaskHistoryExport value)
    {
        var result = new TaskHistoryEntity
        {
            Id = value.Id,
            TaskId = ParseUuid(value.TaskId),
            Kind = Enum.Parse<TaskHistoryKind>(value.Kind, false),
            OccurredAt = ParseInstant(value.OccurredAtUtc),
            Title = value.Title,
            TimeType = Enum.Parse<TaskTimeType>(value.TimeType, false),
            LocalDate = LocalDatePattern.Parse(value.LocalDate).GetValueOrThrow(),
            Result = value.Result is null ? null : Enum.Parse<TaskResult>(value.Result, false),
            ResultRecordedAt = value.ResultRecordedAtUtc is null ? null : ParseInstant(value.ResultRecordedAtUtc),
            ResultNote = value.ResultNote,
            CreatedAt = ParseInstant(value.CreatedAtUtc),
            UpdatedAt = ParseInstant(value.UpdatedAtUtc),
            SortOrder = value.SortOrder,
            ContinuedFromTaskId = value.ContinuedFromTaskId is null ? null : ParseUuid(value.ContinuedFromTaskId),
            RelatedTaskId = value.RelatedTaskId is null ? null : ParseUuid(value.RelatedTaskId)
        };
        result.TimePoint = value.TimePoint is null ? null : LocalTimePattern.Parse(value.TimePoint).GetValueOrThrow();
        result.RangeStart = value.RangeStart is null ? null : LocalTimePattern.Parse(value.RangeStart).GetValueOrThrow();
        result.RangeEnd = value.RangeEnd is null ? null : LocalTimePattern.Parse(value.RangeEnd).GetValueOrThrow();
        _ = result.ToRecord();
        return result;
    }

    private static ReminderRuleEntity ToRuleEntity(ReminderRuleExport value)
    {
        var entity = new ReminderRuleEntity
        {
            Id = ParseUuid(value.Id),
            TargetKind = Enum.Parse<ReminderTargetKind>(value.TargetKind, false),
            TargetId = ParseUuid(value.TargetId),
            OccurrenceId = ParseUuid(value.OccurrenceId),
            Purpose = Enum.Parse<ReminderPurpose>(value.Purpose, false),
            TimingKind = Enum.Parse<ReminderTimingKind>(value.Timing.Kind, false),
            TimingAnchor = value.Timing.Anchor is null ? null : Enum.Parse<ReminderAnchor>(value.Timing.Anchor, false),
            OffsetSeconds = value.Timing.OffsetSeconds,
            AbsoluteAtUtc = value.Timing.AtUtc is null ? null : ParseInstant(value.Timing.AtUtc),
            Priority = Enum.Parse<ReminderPriority>(value.Priority, false),
            Pinned = value.Pinned,
            RepeatEnabled = value.RepeatPolicy.Enabled,
            RepeatIntervalSeconds = value.RepeatPolicy.IntervalSeconds,
            RepeatMaxCount = value.RepeatPolicy.MaxCount,
            WakePolicy = Enum.Parse<WakePolicy>(value.WakePolicy, false),
            Enabled = value.Enabled,
            RuleRevision = value.RuleRevision,
            CreatedAtUtc = ParseInstant(value.CreatedAtUtc),
            UpdatedAtUtc = ParseInstant(value.UpdatedAtUtc)
        };
        _ = entity.ToDomain();
        return entity;
    }

    private static ReminderScheduleEntity ToScheduleEntity(
        ReminderScheduleExport value,
        Dictionary<string, ReminderRuleExport> rules)
    {
        if (!rules.TryGetValue(value.RuleId, out var rule))
        {
            throw new UserDataExportContractException("restore.relation.invalid", "Schedule 引用的 Rule 不存在。", "schedules.ruleId");
        }

        var entity = new ReminderScheduleEntity
        {
            Id = ParseUuid(value.Id),
            RuleId = ParseUuid(value.RuleId),
            OccurrenceId = ParseUuid(value.OccurrenceId),
            LogicalReminderId = ParseUuid(value.LogicalReminderId),
            OriginScheduleId = value.OriginScheduleId is null ? null : ParseUuid(value.OriginScheduleId),
            Cause = Enum.Parse<ScheduleCause>(value.Cause, false),
            RuleRevision = value.RuleRevision,
            ScheduleRevision = value.ScheduleRevision,
            TriggerAtUtc = ParseInstant(value.TriggerAtUtc),
            TimeZoneId = value.TimeZoneId,
            PurposeSnapshot = Enum.Parse<ReminderPurpose>(rule.Purpose, false),
            PrioritySnapshot = Enum.Parse<ReminderPriority>(rule.Priority, false),
            PinnedSnapshot = rule.Pinned,
            State = Enum.Parse<ScheduleState>(value.State, false),
            TerminalReason = value.TerminalReason is null ? null : Enum.Parse<ScheduleStateReason>(value.TerminalReason, false),
            ReplacementScheduleId = value.ReplacementScheduleId is null ? null : ParseUuid(value.ReplacementScheduleId),
            CreatedAtUtc = ParseInstant(value.CreatedAtUtc),
            TerminalAtUtc = value.TerminalAtUtc is null ? null : ParseInstant(value.TerminalAtUtc)
        };
        return entity;
    }

    private static ReminderInstanceEntity ToInstanceEntity(ReminderInstanceExport value)
    {
        var entity = new ReminderInstanceEntity
        {
            Id = ParseUuid(value.Id),
            ScheduleId = ParseUuid(value.ScheduleId),
            RuleId = ParseUuid(value.RuleId),
            OccurrenceId = ParseUuid(value.OccurrenceId),
            LogicalReminderId = ParseUuid(value.LogicalReminderId),
            AttemptOrdinal = value.AttemptOrdinal,
            PurposeSnapshot = Enum.Parse<ReminderPurpose>(value.Purpose, false),
            PrioritySnapshot = Enum.Parse<ReminderPriority>(value.Priority, false),
            PinnedSnapshot = value.Pinned,
            TriggeredAtUtc = ParseInstant(value.TriggeredAtUtc),
            Lifecycle = Enum.Parse<ReminderLifecycle>(value.Lifecycle, false),
            ReadAtUtc = value.ReadAtUtc is null ? null : ParseInstant(value.ReadAtUtc),
            ResolvedAtUtc = value.ResolvedAtUtc is null ? null : ParseInstant(value.ResolvedAtUtc),
            ResolutionAction = value.ResolutionAction is null ? null : Enum.Parse<ResolutionAction>(value.ResolutionAction, false)
        };
        _ = entity.ToDomain();
        return entity;
    }

    private static ReminderDeliveryAttemptEntity ToAttemptEntity(UserDeliveryAttemptExport value) => new()
    {
        AttemptId = ParseUuid(value.AttemptId),
        InstanceId = ParseUuid(value.InstanceId),
        Channel = Enum.Parse<ReminderDeliveryChannel>(value.Channel, false),
        AttemptedAtUtc = ParseInstant(value.AttemptedAtUtc),
        Outcome = Enum.Parse<ReminderDeliveryOutcome>(value.Outcome, false),
        ErrorCode = value.ErrorCode
    };

    private static async ValueTask InsertDeliveryAttemptEventsAsync(
        SqliteConnection connection,
        string profileScope,
        UserDeliveryAttemptEventExport[] events,
        CancellationToken cancellationToken)
    {
        if (events.Length == 0)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var value in events)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO notification_delivery_attempt_events (
                        profile_scope, attempt_id, event_ordinal, instance_id,
                        schedule_id, rule_id, occurrence_id, logical_reminder_id,
                        channel, correlation_id, request_id, idempotency_key,
                        purpose_snapshot, priority_snapshot, pinned_snapshot,
                        trigger_attempt_ordinal, triggered_at_utc,
                        scheduled_trigger_at_utc, attempt_number, state, outcome,
                        created_at_utc, updated_at_utc, next_attempt_at_utc,
                        error_code, retryable, revision, journal_batch_id, receipt_id)
                    VALUES (
                        $profileScope, $attemptId, $eventOrdinal, $instanceId,
                        $scheduleId, $ruleId, $occurrenceId, $logicalReminderId,
                        $channel, $correlationId, $requestId, $idempotencyKey,
                        $purposeSnapshot, $prioritySnapshot, $pinnedSnapshot,
                        $triggerAttemptOrdinal, $triggeredAtUtc,
                        $scheduledTriggerAtUtc, $attemptNumber, $state, $outcome,
                        $createdAtUtc, $updatedAtUtc, $nextAttemptAtUtc,
                        $errorCode, $retryable, $revision, $journalBatchId, $receiptId);
                    """;
                command.Parameters.AddWithValue("$profileScope", profileScope);
                AddEventParameter(command, "$attemptId", ParseUuid(value.AttemptId));
                command.Parameters.AddWithValue("$eventOrdinal", value.EventOrdinal);
                AddEventParameter(command, "$instanceId", ParseUuid(value.InstanceId));
                AddEventParameter(command, "$scheduleId", ParseUuid(value.ScheduleId));
                AddEventParameter(command, "$ruleId", ParseUuid(value.RuleId));
                AddEventParameter(command, "$occurrenceId", ParseUuid(value.OccurrenceId));
                AddEventParameter(command, "$logicalReminderId", ParseUuid(value.LogicalReminderId));
                command.Parameters.AddWithValue("$channel", value.Channel);
                AddEventParameter(command, "$correlationId", ParseUuid(value.CorrelationId));
                AddEventParameter(command, "$requestId", ParseUuid(value.RequestId));
                AddEventParameter(command, "$idempotencyKey", ParseUuid(value.IdempotencyKey));
                command.Parameters.AddWithValue("$purposeSnapshot", value.PurposeSnapshot);
                command.Parameters.AddWithValue("$prioritySnapshot", value.PrioritySnapshot);
                command.Parameters.AddWithValue("$pinnedSnapshot", value.PinnedSnapshot ? 1 : 0);
                command.Parameters.AddWithValue("$triggerAttemptOrdinal", value.TriggerAttemptOrdinal);
                command.Parameters.AddWithValue("$triggeredAtUtc", value.TriggeredAtUtc);
                command.Parameters.AddWithValue("$scheduledTriggerAtUtc", value.ScheduledTriggerAtUtc ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$attemptNumber", value.AttemptNumber);
                command.Parameters.AddWithValue("$state", value.State);
                command.Parameters.AddWithValue("$outcome", value.Outcome ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$createdAtUtc", value.CreatedAtUtc);
                command.Parameters.AddWithValue("$updatedAtUtc", value.UpdatedAtUtc);
                command.Parameters.AddWithValue("$nextAttemptAtUtc", value.NextAttemptAtUtc ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$errorCode", value.ErrorCode ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$retryable", value.Retryable ? 1 : 0);
                command.Parameters.AddWithValue("$revision", value.Revision);
                AddEventParameter(command, "$journalBatchId", ParseUuid(value.JournalBatchId));
                AddEventParameter(command, "$receiptId", ParseUuid(value.ReceiptId));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void AddEventParameter(SqliteCommand command, string name, Guid value) =>
        command.Parameters.AddWithValue(name, value.ToString("D"));

    private static Guid ParseUuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id.Version != 7 || !string.Equals(id.ToString("D"), value, StringComparison.Ordinal))
        {
            throw new UserDataExportContractException("restore.id.invalid", "Candidate import 中的标识不是 UUID v7。", "id");
        }

        return id;
    }

    private static Instant ParseInstant(string value) =>
        InstantPattern.Parse(value).GetValueOrThrow();

    private static void ValidatePaths(UserDataRestoreRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ArtifactPath) ||
            string.IsNullOrWhiteSpace(request.CandidateRoot) ||
            string.IsNullOrWhiteSpace(request.ActiveDatabasePath))
        {
            throw new UserDataExportContractException("restore.path.required", "Artifact、Candidate root 和 Active database path 都必须显式提供。");
        }

        var artifact = Path.GetFullPath(request.ArtifactPath);
        var candidate = Path.GetFullPath(request.CandidateRoot);
        var active = Path.GetFullPath(request.ActiveDatabasePath);
        var activeProfileRoot = Path.GetDirectoryName(active)
            ?? throw new UserDataExportContractException(
                "restore.path.invalid",
                "Active database path 没有合法 profile 目录。",
                "activeDatabasePath");
        if (artifact.Equals(active, StringComparison.OrdinalIgnoreCase) ||
            IsSameOrWithin(candidate, activeProfileRoot) ||
            IsSameOrWithin(activeProfileRoot, candidate))
        {
            throw new UserDataExportContractException("restore.active_write_forbidden", "Candidate root 不能与 Active 数据库或其 profile 目录重合。", "candidateRoot");
        }

        if (!Path.IsPathFullyQualified(candidate) || candidate.StartsWith("\\\\", StringComparison.Ordinal) || candidate.StartsWith("//", StringComparison.Ordinal))
        {
            throw new UserDataExportContractException("restore.path.invalid", "Candidate root 必须是绝对本地路径。", "candidateRoot");
        }
    }

    private static string[] Describe(ReminderRestorePlan plan) =>
        plan.Issues.Select(issue => issue.Code + ":" + issue.Message).ToArray();

    private static bool IsSameOrWithin(string candidate, string parent)
    {
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedCandidate.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(normalizedParent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteCandidate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
