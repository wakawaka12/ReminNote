using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NodaTime;
using NodaTime.Text;

namespace ReminNote.Core.Reminders.Notifications;

/// <summary>
/// Bounded, deterministic JSON codec for the notification contract.  This is
/// an adapter-facing diagnostic/persistence seam, not a P2 wire DTO.  The
/// codec writes only stable codes and snapshots; it never serializes display
/// text or mutable channel state.
/// </summary>
public static class NotificationContractJson
{
    private static readonly InstantPattern InstantPattern =
        NodaTime.Text.InstantPattern.CreateWithInvariantCulture(
            "uuuu-MM-dd'T'HH:mm:ss.fffffffff'Z'");

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    public static byte[] SerializeChannelStatus(NotificationChannelStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return Finish(
            NotificationContractLimits.MaxSerializedChannelStatusBytes,
            writer =>
            {
                writer.WriteString("channelId", status.ChannelId.Value);
                writer.WritePropertyName("capabilities");
                writer.WriteStartArray();
                foreach (var capability in status.Capabilities)
                {
                    writer.WriteStringValue(ToWireCapability(capability));
                }

                writer.WriteEndArray();
                writer.WriteString("health", ToWireHealth(status.Health));
                writer.WriteString("observedAtUtc", FormatInstant(status.ObservedAtUtc));
                WriteNullableString(writer, "healthCode", status.HealthCode);
            });
    }

    public static string SerializeChannelStatusText(NotificationChannelStatus status) =>
        NotificationContractLimits.StrictUtf8.GetString(SerializeChannelStatus(status));

    public static NotificationChannelStatus DeserializeChannelStatus(ReadOnlySpan<byte> utf8Json)
    {
        var fields = ReadObject(
            utf8Json,
            NotificationContractLimits.MaxSerializedChannelStatusBytes,
            "channel status");
        RequireFields(fields, "channelId", "capabilities", "health", "observedAtUtc");

        var channelId = NotificationChannelId.Parse(ReadString(fields, "channelId"));
        var capabilities = ReadCapabilities(fields["capabilities"]);
        var health = ParseHealth(ReadString(fields, "health"));
        var observedAtUtc = ParseInstant(ReadString(fields, "observedAtUtc"), "observedAtUtc");
        var healthCode = ReadNullableString(fields, "healthCode");
        return new NotificationChannelStatus(
            channelId,
            capabilities,
            health,
            observedAtUtc,
            healthCode);
    }

    public static NotificationChannelStatus DeserializeChannelStatus(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return DeserializeChannelStatus(ToUtf8(json, "channel status"));
    }

    public static byte[] SerializeDeliveryAttempt(NotificationDeliveryAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return Finish(
            NotificationContractLimits.MaxSerializedDeliveryAttemptBytes,
            writer =>
            {
                writer.WriteString("attemptId", FormatGuid(attempt.AttemptId));
                writer.WriteString("instanceId", FormatGuid(attempt.InstanceId));
                writer.WriteString("logicalReminderId", FormatGuid(attempt.LogicalReminderId));
                // P3-00 calls this child field `channel`; the public Core
                // property remains ChannelId to make its role explicit.
                writer.WriteString("channel", attempt.ChannelId.Value);
                writer.WriteString("correlationId", FormatGuid(attempt.CorrelationId));
                writer.WriteString("idempotencyKey", FormatGuid(attempt.IdempotencyKey));
                writer.WriteString("purposeSnapshot", attempt.PurposeSnapshot.Value);
                writer.WriteString("prioritySnapshot", ToWirePriority(attempt.PrioritySnapshot));
                writer.WriteBoolean("pinnedSnapshot", attempt.PinnedSnapshot);
                writer.WriteString("attemptedAtUtc", FormatInstant(attempt.AttemptedAtUtc));
                writer.WriteString("outcome", ToWireOutcome(attempt.Outcome));
                WriteNullableString(writer, "errorCode", attempt.ErrorCode);
            });
    }

    public static string SerializeDeliveryAttemptText(NotificationDeliveryAttempt attempt) =>
        NotificationContractLimits.StrictUtf8.GetString(SerializeDeliveryAttempt(attempt));

    public static NotificationDeliveryAttempt DeserializeDeliveryAttempt(ReadOnlySpan<byte> utf8Json)
    {
        var fields = ReadObject(
            utf8Json,
            NotificationContractLimits.MaxSerializedDeliveryAttemptBytes,
            "delivery attempt");
        RequireFields(
            fields,
            "attemptId",
            "instanceId",
            "logicalReminderId",
            "channel",
            "correlationId",
            "idempotencyKey",
            "purposeSnapshot",
            "prioritySnapshot",
            "pinnedSnapshot",
            "attemptedAtUtc",
            "outcome");

        return new NotificationDeliveryAttempt(
            ParseGuid(fields, "attemptId"),
            ParseGuid(fields, "instanceId"),
            ParseGuid(fields, "logicalReminderId"),
            NotificationChannelId.Parse(ReadString(fields, "channel")),
            ParseGuid(fields, "correlationId"),
            ParseGuid(fields, "idempotencyKey"),
            NotificationPurposeSnapshot.Parse(ReadString(fields, "purposeSnapshot")),
            ParsePriority(ReadString(fields, "prioritySnapshot")),
            ReadBoolean(fields, "pinnedSnapshot"),
            ParseInstant(ReadString(fields, "attemptedAtUtc"), "attemptedAtUtc"),
            ParseOutcome(ReadString(fields, "outcome")),
            ReadNullableString(fields, "errorCode"));
    }

    public static NotificationDeliveryAttempt DeserializeDeliveryAttempt(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return DeserializeDeliveryAttempt(ToUtf8(json, "delivery attempt"));
    }

    public static byte[] SerializeLifecycleState(NotificationLifecycleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Finish(
            NotificationContractLimits.MaxSerializedDeliveryAttemptBytes,
            writer =>
            {
                writer.WriteString("lifecycle", ToWireLifecycle(state.Lifecycle));
                WriteNullableInstant(writer, "readAtUtc", state.ReadAtUtc);
                WriteNullableInstant(writer, "resolvedAtUtc", state.ResolvedAtUtc);
                if (state.ResolutionAction is { } action)
                {
                    writer.WriteString("resolutionAction", ToWireResolutionAction(action));
                }
                else
                {
                    writer.WriteNull("resolutionAction");
                }
            });
    }

    public static NotificationLifecycleState DeserializeLifecycleState(ReadOnlySpan<byte> utf8Json)
    {
        var fields = ReadObject(
            utf8Json,
            NotificationContractLimits.MaxSerializedDeliveryAttemptBytes,
            "lifecycle state");
        RequireFields(fields, "lifecycle");
        var lifecycle = ParseLifecycle(ReadString(fields, "lifecycle"));
        var readAtUtc = ReadNullableInstant(fields, "readAtUtc");
        var resolvedAtUtc = ReadNullableInstant(fields, "resolvedAtUtc");
        NotificationResolutionAction? action = ReadNullableString(fields, "resolutionAction") is { } actionText
            ? ParseResolutionAction(actionText)
            : null;
        return new NotificationLifecycleState(lifecycle, readAtUtc, resolvedAtUtc, action);
    }

    public static NotificationLifecycleState DeserializeLifecycleState(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return DeserializeLifecycleState(ToUtf8(json, "lifecycle state"));
    }

    private static byte[] Finish(int maxBytes, Action<Utf8JsonWriter> writeProperties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.Default,
                       Indented = false,
                   }))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        if (buffer.WrittenCount > maxBytes)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationTooLarge,
                "Serialized notification contract exceeds its bounded byte limit.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static Dictionary<string, JsonElement> ReadObject(
        ReadOnlySpan<byte> utf8Json,
        int maxBytes,
        string typeName)
    {
        if (utf8Json.Length == 0)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                $"Serialized {typeName} cannot be empty.");
        }

        if (utf8Json.Length > maxBytes)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationTooLarge,
                $"Serialized {typeName} exceeds its bounded byte limit.");
        }

        if (utf8Json.Length >= 3 &&
            utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                $"Serialized {typeName} must not contain a UTF-8 BOM.");
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.SerializationInvalid,
                    $"Serialized {typeName} must be a JSON object.");
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!fields.TryAdd(property.Name, property.Value.Clone()))
                {
                    throw NotificationContractException.Invalid(
                        NotificationErrorCodes.SerializationInvalid,
                        $"Serialized {typeName} contains a duplicate field.",
                        property.Name);
                }
            }

            return fields;
        }
        catch (NotificationContractException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                $"Serialized {typeName} is not valid strict JSON.",
                innerException: exception);
        }
    }

    private static void RequireFields(
        Dictionary<string, JsonElement> fields,
        params string[] required)
    {
        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        // Optional fields are part of the frozen object shapes even when they
        // are omitted by an older adapter.  Unknown fields are never ignored.
        if (required.Contains("channelId", StringComparer.Ordinal))
        {
            allowed.Add("healthCode");
        }

        if (required.Contains("attemptId", StringComparer.Ordinal))
        {
            allowed.Add("errorCode");
        }

        if (required.Contains("lifecycle", StringComparer.Ordinal))
        {
            allowed.Add("readAtUtc");
            allowed.Add("resolvedAtUtc");
            allowed.Add("resolutionAction");
        }

        foreach (var field in fields.Keys)
        {
            if (!allowed.Contains(field))
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.SerializationUnknownField,
                    $"Unknown notification contract field '{field}'.",
                    field);
            }
        }

        foreach (var field in required)
        {
            if (!fields.ContainsKey(field))
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.SerializationInvalid,
                    $"Required notification contract field '{field}' is missing.",
                    field);
            }
        }
    }

    private static string ReadString(
        Dictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } result)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                $"Field '{name}' must be a string.",
                name);
        }

        return result;
    }

    private static string? ReadNullableString(
        Dictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                $"Field '{name}' must be null or a string.",
                name);
        }

        return result;
    }

    private static bool ReadBoolean(
        Dictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) ||
            value.ValueKind != JsonValueKind.True &&
            value.ValueKind != JsonValueKind.False)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                $"Field '{name}' must be a JSON boolean.",
                name);
        }

        return value.GetBoolean();
    }

    private static Guid ParseGuid(
        Dictionary<string, JsonElement> fields,
        string name)
    {
        var value = ReadString(fields, name);
        if (!Guid.TryParseExact(value, "D", out var result) ||
            !string.Equals(value, result.ToString("D"), StringComparison.Ordinal))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                $"Field '{name}' must be a lowercase UUID v7 in D format.",
                name);
        }

        return NotificationValidation.RequireUuidV7(result, name);
    }

    private static Instant ParseInstant(string value, string fieldName)
    {
        var parsed = InstantPattern.Parse(value);
        if (!parsed.Success)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidTimestamp,
                $"Field '{fieldName}' must use the nine-fraction UTC instant form.",
                fieldName);
        }

        return parsed.Value;
    }

    private static Instant? ReadNullableInstant(
        Dictionary<string, JsonElement> fields,
        string name)
    {
        var value = ReadNullableString(fields, name);
        return value is null ? null : ParseInstant(value, name);
    }

    private static List<NotificationCapability> ReadCapabilities(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "Field 'capabilities' must be an array.",
                "capabilities");
        }

        var capabilities = new List<NotificationCapability>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text)
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.SerializationInvalid,
                    "Each capability must be a string.",
                    "capabilities");
            }

            capabilities.Add(ParseCapability(text));
        }

        return capabilities;
    }

    private static string FormatGuid(Guid value) => value.ToString("D");

    private static string FormatInstant(Instant value) => InstantPattern.Format(value);

    private static byte[] ToUtf8(string value, string typeName)
    {
        try
        {
            return NotificationContractLimits.StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                $"Serialized {typeName} contains invalid UTF-16.",
                innerException: exception);
        }
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
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

    private static void WriteNullableInstant(Utf8JsonWriter writer, string name, Instant? value)
    {
        if (value is { } instant)
        {
            writer.WriteString(name, FormatInstant(instant));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string ToWireCapability(NotificationCapability capability) => capability switch
    {
        NotificationCapability.PRESENT => "PRESENT",
        NotificationCapability.REPLACE_LOGICAL_REMINDER => "REPLACE_LOGICAL_REMINDER",
        NotificationCapability.READ_ON_CLOSE => "READ_ON_CLOSE",
        NotificationCapability.WAKE => "WAKE",
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.SerializationInvalid,
            "Capability is not supported.",
            nameof(capability)),
    };

    private static NotificationCapability ParseCapability(string value) => value switch
    {
        "PRESENT" => NotificationCapability.PRESENT,
        "REPLACE_LOGICAL_REMINDER" => NotificationCapability.REPLACE_LOGICAL_REMINDER,
        "READ_ON_CLOSE" => NotificationCapability.READ_ON_CLOSE,
        "WAKE" => NotificationCapability.WAKE,
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.SerializationInvalid,
            $"Unknown capability '{value}'.",
            "capabilities"),
    };

    private static string ToWireHealth(NotificationChannelHealth health) => health switch
    {
        NotificationChannelHealth.HEALTHY => "HEALTHY",
        NotificationChannelHealth.DEGRADED => "DEGRADED",
        NotificationChannelHealth.BLOCKED => "BLOCKED",
        NotificationChannelHealth.UNAVAILABLE => "UNAVAILABLE",
        NotificationChannelHealth.UNKNOWN => "UNKNOWN",
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.SerializationInvalid,
            "Channel health is not supported.",
            nameof(health)),
    };

    private static NotificationChannelHealth ParseHealth(string value) => value switch
    {
        "HEALTHY" => NotificationChannelHealth.HEALTHY,
        "DEGRADED" => NotificationChannelHealth.DEGRADED,
        "BLOCKED" => NotificationChannelHealth.BLOCKED,
        "UNAVAILABLE" => NotificationChannelHealth.UNAVAILABLE,
        "UNKNOWN" => NotificationChannelHealth.UNKNOWN,
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.SerializationInvalid,
            $"Unknown channel health '{value}'.",
            "health"),
    };

    private static string ToWirePriority(NotificationPriority priority) => priority switch
    {
        NotificationPriority.LOW => "LOW",
        NotificationPriority.NORMAL => "NORMAL",
        NotificationPriority.HIGH => "HIGH",
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.InvalidPriority,
            "Priority is not supported.",
            nameof(priority)),
    };

    private static NotificationPriority ParsePriority(string value) => value switch
    {
        "LOW" => NotificationPriority.LOW,
        "NORMAL" => NotificationPriority.NORMAL,
        "HIGH" => NotificationPriority.HIGH,
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.InvalidPriority,
            $"Unknown priority '{value}'.",
            "prioritySnapshot"),
    };

    private static string ToWireOutcome(NotificationDeliveryOutcome outcome) => outcome switch
    {
        NotificationDeliveryOutcome.DELIVERED => "DELIVERED",
        NotificationDeliveryOutcome.BLOCKED => "BLOCKED",
        NotificationDeliveryOutcome.UNAVAILABLE => "UNAVAILABLE",
        NotificationDeliveryOutcome.FAILED => "FAILED",
        NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS => "SUPPRESSED_QUIET_HOURS",
        NotificationDeliveryOutcome.NOT_ATTEMPTED => "NOT_ATTEMPTED",
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.DeliveryOutcomeInvalid,
            "Delivery outcome is not supported.",
            nameof(outcome)),
    };

    private static NotificationDeliveryOutcome ParseOutcome(string value) => value switch
    {
        "DELIVERED" => NotificationDeliveryOutcome.DELIVERED,
        "BLOCKED" => NotificationDeliveryOutcome.BLOCKED,
        "UNAVAILABLE" => NotificationDeliveryOutcome.UNAVAILABLE,
        "FAILED" => NotificationDeliveryOutcome.FAILED,
        "SUPPRESSED_QUIET_HOURS" => NotificationDeliveryOutcome.SUPPRESSED_QUIET_HOURS,
        "NOT_ATTEMPTED" => NotificationDeliveryOutcome.NOT_ATTEMPTED,
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.DeliveryOutcomeInvalid,
            $"Unknown delivery outcome '{value}'.",
            "outcome"),
    };

    private static string ToWireLifecycle(NotificationReminderLifecycle lifecycle) => lifecycle switch
    {
        NotificationReminderLifecycle.UNREAD => "UNREAD",
        NotificationReminderLifecycle.READ => "READ",
        NotificationReminderLifecycle.RESOLVED => "RESOLVED",
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.InvalidLifecycle,
            "Reminder lifecycle is not supported.",
            nameof(lifecycle)),
    };

    private static NotificationReminderLifecycle ParseLifecycle(string value) => value switch
    {
        "UNREAD" => NotificationReminderLifecycle.UNREAD,
        "READ" => NotificationReminderLifecycle.READ,
        "RESOLVED" => NotificationReminderLifecycle.RESOLVED,
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.InvalidLifecycle,
            $"Unknown reminder lifecycle '{value}'.",
            "lifecycle"),
    };

    private static string ToWireResolutionAction(NotificationResolutionAction action) => action switch
    {
        NotificationResolutionAction.DONE => "DONE",
        NotificationResolutionAction.SNOOZE => "SNOOZE",
        NotificationResolutionAction.WATCHED => "WATCHED",
        NotificationResolutionAction.WATCH_LATER => "WATCH_LATER",
        NotificationResolutionAction.SKIP => "SKIP",
        NotificationResolutionAction.IGNORE => "IGNORE",
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.SerializationInvalid,
            "Resolution action is not supported.",
            nameof(action)),
    };

    private static NotificationResolutionAction ParseResolutionAction(string value) => value switch
    {
        "DONE" => NotificationResolutionAction.DONE,
        "SNOOZE" => NotificationResolutionAction.SNOOZE,
        "WATCHED" => NotificationResolutionAction.WATCHED,
        "WATCH_LATER" => NotificationResolutionAction.WATCH_LATER,
        "SKIP" => NotificationResolutionAction.SKIP,
        "IGNORE" => NotificationResolutionAction.IGNORE,
        _ => throw NotificationContractException.Invalid(
            NotificationErrorCodes.SerializationInvalid,
            $"Unknown resolution action '{value}'.",
            "resolutionAction"),
    };
}
