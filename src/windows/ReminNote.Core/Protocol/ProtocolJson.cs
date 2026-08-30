using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ReminNote.Core.Protocol;

/// <summary>
/// Strict, deterministic JSON codec for the protocol envelopes. Named pipes
/// and message framing are intentionally outside this class.
/// </summary>
public static class ProtocolJson
{
    private static readonly string[] UtcPatterns =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
    ];

    public static JsonElement ParseObject(ReadOnlySpan<byte> utf8Json) =>
        StrictJson.ParseObject(utf8Json);

    public static JsonElement ParseObject(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return ParseObject(ProtocolLimits.StrictUtf8.GetBytes(json));
    }

    public static byte[] Serialize(IProtocolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope switch
        {
            ProtocolRequest request => SerializeRequest(request),
            ProtocolResponse response => SerializeResponse(response),
            ProtocolEvent @event => SerializeEvent(@event),
            _ => throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "The envelope type is not supported by the v1 codec.",
                nameof(envelope)),
        };
    }

    public static byte[] SerializeRequest(ProtocolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return Finish(writer =>
        {
            writer.WriteString("protocolVersion", request.ProtocolVersion.ToString());
            writer.WriteString("messageType", ProtocolMessageTypes.Request);
            writer.WriteString("requestId", request.RequestId);
            writer.WriteString("clientKind", request.ClientKind);
            writer.WriteString("clientInstanceId", request.ClientInstanceId);
            writer.WriteString("sentAtUtc", FormatUtc(request.SentAtUtc));
            writer.WriteNumber("timeoutMs", request.TimeoutMs);
            writer.WriteString("operation", request.Operation);
            WriteCanonicalValue(writer, "payload", request.Payload);
            if (request.IdempotencyKey is not null)
            {
                writer.WriteString("idempotencyKey", request.IdempotencyKey);
            }

            if (request.ExpectedRevision is { } expectedRevision)
            {
                writer.WriteNumber("expectedRevision", expectedRevision);
            }
        });
    }

    public static byte[] SerializeStatusRequest(ProtocolStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return SerializeRequest(request.Envelope);
    }

    public static byte[] SerializeCancelRequest(ProtocolCancelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        return SerializeRequest(request.Envelope);
    }

    public static byte[] SerializeResponse(ProtocolResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Validate();
        return Finish(writer =>
        {
            writer.WriteString("protocolVersion", response.ProtocolVersion.ToString());
            writer.WriteString("messageType", ProtocolMessageTypes.Response);
            writer.WriteString("requestId", response.RequestId);
            writer.WriteString("operation", response.Operation);
            writer.WriteString("agentInstanceId", response.AgentInstanceId);
            writer.WriteNumber("serverRevision", response.ServerRevision);
            writer.WriteBoolean("ok", response.Ok);
            writer.WriteBoolean("replayed", response.Replayed);
            writer.WriteString("outcome", response.Outcome);
            if (response.CommittedRevision is { } committedRevision)
            {
                writer.WriteNumber("committedRevision", committedRevision);
            }
            else
            {
                writer.WriteNull("committedRevision");
            }

            if (response.Payload is { } payload)
            {
                WriteCanonicalValue(writer, "payload", payload);
            }
            else
            {
                writer.WriteNull("payload");
            }

            if (response.Error is { } error)
            {
                writer.WritePropertyName("error");
                WriteErrorObject(writer, error);
            }
            else
            {
                writer.WriteNull("error");
            }
        });
    }

    public static byte[] SerializeEvent(ProtocolEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        @event.Validate();
        return Finish(writer =>
        {
            writer.WriteString("protocolVersion", @event.ProtocolVersion.ToString());
            writer.WriteString("messageType", ProtocolMessageTypes.Event);
            writer.WriteString("eventId", @event.EventId.ToString("D"));
            writer.WriteString("eventType", @event.EventType);
            writer.WriteString("agentInstanceId", @event.AgentInstanceId.ToString("D"));
            writer.WriteString("occurredAtUtc", FormatUtc(@event.OccurredAtUtc));
            writer.WriteNumber("revision", @event.Revision);
            WriteCanonicalValue(writer, "payload", @event.Payload);
        });
    }

    public static byte[] SerializeError(ProtocolError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        error.Validate();
        return Finish(writer => WriteErrorObject(writer, error));
    }

    public static ProtocolRequest DeserializeRequest(ReadOnlySpan<byte> utf8Json)
    {
        using var document = StrictJson.Parse(utf8Json);
        var bag = PropertyBag.Create(document.RootElement, RequestFields, rejectUnknown: true);
        RequireMessageType(bag, ProtocolMessageTypes.Request);
        var operation = bag.RequireString("operation");
        if (!ProtocolOperations.IsMutation(operation) &&
            (bag.Contains("idempotencyKey") || bag.Contains("expectedRevision")))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "idempotencyKey and expectedRevision are not allowed for this operation.");
        }

        var request = new ProtocolRequest(
            ReadVersion(bag.RequireString("protocolVersion")),
            bag.RequireLowercaseUuid("requestId"),
            bag.RequireString("clientKind"),
            bag.RequireLowercaseUuid("clientInstanceId"),
            bag.RequireUtc("sentAtUtc"),
            bag.RequireInt32("timeoutMs"),
            operation,
            bag.RequireObject("payload").Clone(),
            bag.OptionalLowercaseUuid("idempotencyKey"),
            bag.OptionalNonNegativeInt64("expectedRevision"));
        request.Validate();
        return request;
    }

    public static ProtocolResponse DeserializeResponse(ReadOnlySpan<byte> utf8Json)
    {
        using var document = StrictJson.Parse(utf8Json);
        var bag = PropertyBag.Create(document.RootElement, ResponseFields, rejectUnknown: false);
        RequireMessageType(bag, ProtocolMessageTypes.Response);
        var payloadValue = bag.Require("payload");
        var errorValue = bag.Require("error");
        _ = bag.Require("committedRevision");
        var payload = payloadValue.ValueKind == JsonValueKind.Null
            ? (JsonElement?)null
            : payloadValue.Clone();
        var error = errorValue.ValueKind == JsonValueKind.Null
            ? null
            : ParseError(errorValue);
        var committedRevision = bag.OptionalNonNegativeInt64("committedRevision");
        var response = new ProtocolResponse(
            ReadVersion(bag.RequireString("protocolVersion")),
            bag.RequireLowercaseUuid("requestId"),
            bag.RequireString("operation"),
            bag.RequireLowercaseUuid("agentInstanceId"),
            bag.RequireNonNegativeInt64("serverRevision"),
            bag.RequireBoolean("ok"),
            bag.RequireBoolean("replayed"),
            bag.RequireString("outcome"),
            committedRevision,
            payload,
            error);
        response.Validate();
        return response;
    }

    public static ProtocolEvent DeserializeEvent(ReadOnlySpan<byte> utf8Json)
    {
        using var document = StrictJson.Parse(utf8Json);
        var bag = PropertyBag.Create(document.RootElement, EventFields, rejectUnknown: false);
        RequireMessageType(bag, ProtocolMessageTypes.Event);
        var @event = new ProtocolEvent(
            ReadVersion(bag.RequireString("protocolVersion")),
            bag.RequireGuid("eventId"),
            bag.RequireString("eventType"),
            bag.RequireGuid("agentInstanceId"),
            bag.RequireUtc("occurredAtUtc"),
            bag.RequireNonNegativeInt64("revision"),
            bag.RequireObject("payload").Clone());
        @event.Validate();
        return @event;
    }

    public static ProtocolError DeserializeError(ReadOnlySpan<byte> utf8Json)
    {
        using var document = StrictJson.Parse(utf8Json);
        return ParseError(document.RootElement);
    }

    public static IProtocolEnvelope Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        using var document = StrictJson.Parse(utf8Json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Protocol envelope must be a JSON object.");
        }

        var messageType = GetRequiredString(root, "messageType");
        return messageType switch
        {
            ProtocolMessageTypes.Request => DeserializeRequest(utf8Json),
            ProtocolMessageTypes.Response => DeserializeResponse(utf8Json),
            ProtocolMessageTypes.Event => DeserializeEvent(utf8Json),
            _ => throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "messageType is not part of the v1 allow-list.",
                "messageType"),
        };
    }

    public static void SerializeTo(Stream destination, IProtocolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Serialize(envelope));
    }

    public static IProtocolEnvelope Deserialize(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Deserialize(ReadAll(source));
    }

    public static ProtocolStatusRequestPayload ReadStatusPayload(ProtocolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request.Operation != ProtocolOperations.CommandStatus)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "The request is not command.status.",
                nameof(request));
        }

        var bag = PropertyBag.Create(request.Payload, ["idempotencyKey"], rejectUnknown: true);
        var result = new ProtocolStatusRequestPayload(bag.RequireLowercaseUuid("idempotencyKey"));
        result.Validate();
        return result;
    }

    public static ProtocolCancelRequestPayload ReadCancelPayload(ProtocolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request.Operation != ProtocolOperations.RequestCancel)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "The request is not request.cancel.",
                nameof(request));
        }

        var bag = PropertyBag.Create(
            request.Payload,
            ["targetRequestId", "targetIdempotencyKey"],
            rejectUnknown: true);
        var result = new ProtocolCancelRequestPayload(
            bag.RequireLowercaseUuid("targetRequestId"),
            bag.RequireLowercaseUuid("targetIdempotencyKey"));
        result.Validate();
        return result;
    }

    public static ProtocolChangesRequestPayload ReadChangesPayload(ProtocolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request.Operation != ProtocolOperations.ChangesGetSince)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "The request is not changes.get_since.",
                nameof(request));
        }

        var bag = PropertyBag.Create(request.Payload, ["afterRevision", "maxRevisions"], rejectUnknown: true);
        var result = new ProtocolChangesRequestPayload(
            bag.RequireNonNegativeInt64("afterRevision"),
            bag.RequireInt32("maxRevisions"));
        result.Validate();
        return result;
    }

    public static ProtocolHelloPayload ReadHelloPayload(ProtocolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request.Operation != ProtocolOperations.SessionHello)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "The request is not session.hello.",
                nameof(request));
        }

        var bag = PropertyBag.Create(
            request.Payload,
            ["supportedProtocolVersions", "requestedFeatures", "requiredFeatures", "lastSeenRevision", "profileHint"],
            rejectUnknown: true);
        var supportedVersions = bag.RequireStringArray("supportedProtocolVersions")
            .Select(ProtocolVersion.Parse)
            .ToArray();
        var requestedFeatures = bag.RequireStringArray("requestedFeatures");
        var requiredFeatures = bag.RequireStringArray("requiredFeatures");
        var result = new ProtocolHelloPayload(
            supportedVersions,
            requestedFeatures,
            requiredFeatures,
            bag.OptionalNonNegativeInt64("lastSeenRevision"),
            bag.OptionalString("profileHint"));
        result.Validate();
        return result;
    }

    public static ProtocolStatusResponsePayload ReadStatusResponsePayload(ProtocolResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Validate();
        if (response.Operation != ProtocolOperations.CommandStatus || response.Payload is not { } payload)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "The response is not a successful command.status response.",
                nameof(response));
        }

        var bag = PropertyBag.Create(
            payload,
            ["status", "operation", "canonicalPayloadHash", "changed", "committedRevision", "errorCode", "retryable"],
            rejectUnknown: true);
        _ = bag.Require("committedRevision");
        _ = bag.Require("errorCode");
        var result = new ProtocolStatusResponsePayload(
            bag.RequireString("status"),
            bag.RequireString("operation"),
            bag.RequireString("canonicalPayloadHash"),
            bag.RequireBoolean("changed"),
            bag.OptionalNonNegativeInt64("committedRevision"),
            bag.OptionalNullableString("errorCode"),
            bag.RequireBoolean("retryable"));
        result.Validate();
        return result;
    }

    public static JsonElement CreateStatusPayload(string idempotencyKey)
    {
        ProtocolValidation.RequireLowercaseUuid(idempotencyKey, nameof(idempotencyKey));
        return CreateObject(writer => writer.WriteString("idempotencyKey", idempotencyKey));
    }

    public static JsonElement CreateCancelPayload(string targetRequestId, string targetIdempotencyKey)
    {
        ProtocolValidation.RequireLowercaseUuid(targetRequestId, nameof(targetRequestId));
        ProtocolValidation.RequireLowercaseUuid(targetIdempotencyKey, nameof(targetIdempotencyKey));
        return CreateObject(writer =>
        {
            writer.WriteString("targetRequestId", targetRequestId);
            writer.WriteString("targetIdempotencyKey", targetIdempotencyKey);
        });
    }

    public static JsonElement CreateChangesPayload(long afterRevision, int maxRevisions)
    {
        var payload = new ProtocolChangesRequestPayload(afterRevision, maxRevisions);
        payload.Validate();
        return CreateObject(writer =>
        {
            writer.WriteNumber("afterRevision", afterRevision);
            writer.WriteNumber("maxRevisions", maxRevisions);
        });
    }

    public static JsonElement CreateHelloPayload(ProtocolHelloPayload hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        hello.Validate();
        return CreateObject(writer =>
        {
            writer.WritePropertyName("supportedProtocolVersions");
            writer.WriteStartArray();
            foreach (var version in hello.SupportedProtocolVersions)
            {
                writer.WriteStringValue(version.ToString());
            }

            writer.WriteEndArray();
            writer.WritePropertyName("requestedFeatures");
            WriteStringArray(writer, hello.RequestedFeatures);
            writer.WritePropertyName("requiredFeatures");
            WriteStringArray(writer, hello.RequiredFeatures);
            if (hello.LastSeenRevision is { } lastSeenRevision)
            {
                writer.WriteNumber("lastSeenRevision", lastSeenRevision);
            }

            if (hello.ProfileHint is not null)
            {
                writer.WriteString("profileHint", hello.ProfileHint);
            }
        });
    }

    public static JsonElement CreateStatusResponsePayload(ProtocolStatusResponsePayload status)
    {
        ArgumentNullException.ThrowIfNull(status);
        status.Validate();
        return CreateObject(writer =>
        {
            writer.WriteString("status", status.Status);
            writer.WriteString("operation", status.Operation);
            writer.WriteString("canonicalPayloadHash", status.CanonicalPayloadHash);
            writer.WriteBoolean("changed", status.Changed);
            if (status.CommittedRevision is { } committedRevision)
            {
                writer.WriteNumber("committedRevision", committedRevision);
            }
            else
            {
                writer.WriteNull("committedRevision");
            }

            if (status.ErrorCode is { } errorCode)
            {
                writer.WriteString("errorCode", errorCode);
            }
            else
            {
                writer.WriteNull("errorCode");
            }

            writer.WriteBoolean("retryable", status.Retryable);
        });
    }

    private static ProtocolError ParseError(JsonElement value)
    {
        var bag = PropertyBag.Create(value, ErrorFields, rejectUnknown: false);
        var error = new ProtocolError(
            bag.RequireString("code"),
            bag.RequireBoolean("retryable"),
            bag.RequireObject("details").Clone(),
            bag.OptionalString("humanMessage"));
        error.Validate();
        return error;
    }

    private static void WriteErrorObject(Utf8JsonWriter writer, ProtocolError error)
    {
        writer.WriteStartObject();
        writer.WriteString("code", error.Code);
        writer.WriteBoolean("retryable", error.Retryable);
        WriteCanonicalValue(writer, "details", error.Details);
        if (error.HumanMessage is not null)
        {
            writer.WriteString("humanMessage", error.HumanMessage);
        }

        writer.WriteEndObject();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, IEnumerable<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteCanonicalValue(Utf8JsonWriter writer, string propertyName, JsonElement value)
    {
        writer.WritePropertyName(propertyName);
        var canonical = RnCj1Canonicalizer.Canonicalize(value);
        writer.WriteRawValue(ProtocolLimits.StrictUtf8.GetString(canonical), skipInputValidation: true);
    }

    private static JsonElement CreateObject(Action<Utf8JsonWriter> writeProperties)
    {
        ArgumentNullException.ThrowIfNull(writeProperties);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        return StrictJson.ParseObject(buffer.WrittenSpan);
    }

    private static byte[] Finish(Action<Utf8JsonWriter> writeProperties)
    {
        ArgumentNullException.ThrowIfNull(writeProperties);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        if (buffer.WrittenCount > ProtocolLimits.MaxFrameBytes)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.FrameTooLarge,
                "Serialized envelope exceeds the maximum frame size.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static Utf8JsonWriter CreateWriter(IBufferWriter<byte> buffer) =>
        new(
            buffer,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = false,
                SkipValidation = false,
            });

    private static ProtocolVersion ReadVersion(string value)
    {
        var version = ProtocolVersion.Parse(value);
        version.ValidateCurrent();
        return version;
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        ProtocolValidation.RequireUtc(value, nameof(value));
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtc(string value, string fieldName)
    {
        if (!value.EndsWith('Z'))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"{fieldName} must be an RFC3339 UTC value ending in Z.",
                fieldName);
        }

        if (!DateTimeOffset.TryParseExact(
                value,
                UtcPatterns,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var result) || result.Offset != TimeSpan.Zero)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"{fieldName} must be an RFC3339 UTC value.",
                fieldName);
        }

        return result;
    }

    private static string GetRequiredString(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String || property.GetString() is not { } result)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.MissingField,
                $"Required string property '{propertyName}' is missing.",
                propertyName);
        }

        return result;
    }

    private static void RequireMessageType(PropertyBag bag, string expectedMessageType)
    {
        var actualMessageType = bag.RequireString("messageType");
        if (!string.Equals(actualMessageType, expectedMessageType, StringComparison.Ordinal))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"messageType must be '{expectedMessageType}'.",
                "messageType");
        }
    }

    private static byte[] ReadAll(Stream source)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Span<byte> chunk = stackalloc byte[4096];
        while (true)
        {
            var read = source.Read(chunk);
            if (read == 0)
            {
                break;
            }

            if (buffer.WrittenCount > ProtocolLimits.MaxFrameBytes - read)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.FrameTooLarge,
                    "Serialized envelope exceeds the maximum frame size.");
            }

            buffer.Write(chunk[..read]);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static readonly string[] RequestFields =
    [
        "protocolVersion",
        "messageType",
        "requestId",
        "clientKind",
        "clientInstanceId",
        "sentAtUtc",
        "timeoutMs",
        "operation",
        "payload",
        "idempotencyKey",
        "expectedRevision",
    ];

    private static readonly string[] ResponseFields =
    [
        "protocolVersion",
        "messageType",
        "requestId",
        "operation",
        "agentInstanceId",
        "serverRevision",
        "ok",
        "replayed",
        "outcome",
        "committedRevision",
        "payload",
        "error",
    ];

    private static readonly string[] EventFields =
    [
        "protocolVersion",
        "messageType",
        "eventId",
        "eventType",
        "agentInstanceId",
        "occurredAtUtc",
        "revision",
        "payload",
    ];

    private static readonly string[] ErrorFields =
    [
        "code",
        "retryable",
        "details",
        "humanMessage",
    ];

    private sealed class PropertyBag
    {
        private readonly IReadOnlyDictionary<string, JsonElement> properties;

        private PropertyBag(IReadOnlyDictionary<string, JsonElement> properties)
        {
            this.properties = properties;
        }

        public static PropertyBag Create(
            JsonElement value,
            IEnumerable<string> allowedFields,
            bool rejectUnknown)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidJson,
                    "Envelope or payload must be a JSON object.");
            }

            var allowed = new HashSet<string>(allowedFields, StringComparer.Ordinal);
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var normalizedName = ProtocolValidation.NormalizeUnicode(property.Name, "property name");
                if (!properties.TryAdd(normalizedName, property.Value))
                {
                    throw ProtocolContractException.Invalid(
                        ProtocolErrorCodes.DuplicateKey,
                        "Duplicate JSON property names are not allowed.",
                        property.Name);
                }

                if (rejectUnknown && !allowed.Contains(normalizedName))
                {
                    throw ProtocolContractException.Invalid(
                        ProtocolErrorCodes.UnknownField,
                        $"Property '{property.Name}' is not allowed.",
                        property.Name);
                }
            }

            return new PropertyBag(properties);
        }

        public JsonElement Require(string name)
        {
            if (!properties.TryGetValue(name, out var value))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.MissingField,
                    $"Required property '{name}' is missing.",
                    name);
            }

            return value;
        }

        public bool Contains(string name) => properties.ContainsKey(name);

        public string RequireString(string name)
        {
            var value = Require(name);
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    $"Property '{name}' must be a string.",
                    name);
            }

            return result;
        }

        public string? OptionalString(string name)
        {
            if (!properties.TryGetValue(name, out var value))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    $"Property '{name}' must be a string.",
                    name);
            }

            return result;
        }

        public string? OptionalNullableString(string name)
        {
            if (!properties.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    $"Property '{name}' must be a string or null.",
                    name);
            }

            return result;
        }

        public string RequireLowercaseUuid(string name)
        {
            return ProtocolValidation.RequireLowercaseUuid(RequireString(name), name);
        }

        public string? OptionalLowercaseUuid(string name)
        {
            if (!properties.ContainsKey(name))
            {
                return null;
            }

            return ProtocolValidation.RequireLowercaseUuid(OptionalString(name), name);
        }

        public Guid RequireGuid(string name)
        {
            var text = ProtocolValidation.RequireLowercaseUuid(RequireString(name), name);
            return Guid.ParseExact(text, "D");
        }

        public List<string> RequireStringArray(string name)
        {
            var value = Require(name);
            if (value.ValueKind != JsonValueKind.Array)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    $"Property '{name}' must be an array.",
                    name);
            }

            var result = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text)
                {
                    throw ProtocolContractException.Invalid(
                        ProtocolErrorCodes.InvalidRequest,
                        $"Property '{name}' must contain only strings.",
                        name);
                }

                result.Add(text);
            }

            return result;
        }

        public int RequireInt32(string name)
        {
            var value = Require(name);
            if (value.ValueKind != JsonValueKind.Number ||
                !ProtocolNumber.TryParseInt32(value.GetRawText(), out var result))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidNumber,
                    $"Property '{name}' must be a 32-bit integer.",
                    name);
            }

            return result;
        }

        public long RequireNonNegativeInt64(string name)
        {
            var value = Require(name);
            if (value.ValueKind != JsonValueKind.Number ||
                !ProtocolNumber.TryParseNonNegativeInt64(value.GetRawText(), out var result))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidNumber,
                    $"Property '{name}' must be a non-negative 64-bit integer.",
                    name);
            }

            return result;
        }

        public long? OptionalNonNegativeInt64(string name)
        {
            if (!properties.TryGetValue(name, out var value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Number ||
                !ProtocolNumber.TryParseNonNegativeInt64(value.GetRawText(), out var result))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidNumber,
                    $"Property '{name}' must be a non-negative 64-bit integer.",
                    name);
            }

            return result;
        }

        public bool RequireBoolean(string name)
        {
            var value = Require(name);
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    $"Property '{name}' must be a boolean.",
                    name);
            }

            return value.GetBoolean();
        }

        public DateTimeOffset RequireUtc(string name) =>
            ParseUtc(RequireString(name), name);

        public JsonElement RequireObject(string name)
        {
            var value = Require(name);
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    $"Property '{name}' must be a JSON object.",
                    name);
            }

            return value;
        }
    }
}
