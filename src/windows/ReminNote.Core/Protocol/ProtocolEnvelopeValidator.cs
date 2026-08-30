using System.Text.Json;

namespace ReminNote.Core.Protocol;

internal static class ProtocolEnvelopeValidator
{
    public static void Validate(ProtocolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.ProtocolVersion.ValidateCurrent();
        RequireExact(request.MessageType, ProtocolMessageTypes.Request, nameof(request.MessageType));
        ProtocolValidation.RequireLowercaseUuid(request.RequestId, nameof(request.RequestId));
        if (request.ClientKind is not (ProtocolClientKinds.Main or ProtocolClientKinds.Widget))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "clientKind must be main or widget.",
                nameof(request.ClientKind));
        }

        ProtocolValidation.RequireLowercaseUuid(request.ClientInstanceId, nameof(request.ClientInstanceId));
        ProtocolValidation.RequireUtc(request.SentAtUtc, nameof(request.SentAtUtc));
        if (request.TimeoutMs is < 1 or > ProtocolLimits.MaxRequestDeadlineMilliseconds)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "timeoutMs must be between 1 and 30000 milliseconds.",
                nameof(request.TimeoutMs));
        }

        ProtocolValidation.RequireOperation(request.Operation);
        ProtocolJsonValueValidation.RequireObject(request.Payload, nameof(request.Payload));
        ProtocolOperationSchemas.ValidatePayload(request.Operation, request.Payload);

        if (ProtocolOperations.IsMutation(request.Operation))
        {
            ProtocolValidation.RequireLowercaseUuid(request.IdempotencyKey, nameof(request.IdempotencyKey));
            ProtocolValidation.RequireNonNegativeRevision(request.ExpectedRevision, nameof(request.ExpectedRevision));

            var payloadBytes = RnCj1Canonicalizer.Canonicalize(request.Payload);
            if (payloadBytes.Length > ProtocolLimits.MaxWriteCommandPayloadBytes)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    "Write command payload exceeds its UTF-8 byte limit.",
                    nameof(request.Payload));
            }
        }
        else if (request.IdempotencyKey is not null || request.ExpectedRevision is not null)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "idempotencyKey and expectedRevision are only valid for mutation operations.",
                nameof(request.IdempotencyKey));
        }
        else
        {
            var payloadBytes = RnCj1Canonicalizer.Canonicalize(request.Payload);
            if (payloadBytes.Length > ProtocolLimits.MaxSuccessPayloadBytes)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    "Read/status request payload exceeds its UTF-8 byte limit.",
                    nameof(request.Payload));
            }
        }

        if (request.Operation == ProtocolOperations.CommandStatus)
        {
            ValidateStatusPayload(request.Payload);
        }
        else if (request.Operation == ProtocolOperations.RequestCancel)
        {
            ValidateCancelPayload(request.Payload);
        }
        else if (request.Operation == ProtocolOperations.ChangesGetSince)
        {
            ValidateChangesPayload(request.Payload);
        }
    }

    public static void Validate(ProtocolResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.ProtocolVersion.ValidateCurrent();
        RequireExact(response.MessageType, ProtocolMessageTypes.Response, nameof(response.MessageType));
        ProtocolValidation.RequireLowercaseUuid(response.RequestId, nameof(response.RequestId));
        ProtocolValidation.RequireOperation(response.Operation);
        ProtocolValidation.RequireLowercaseUuid(response.AgentInstanceId, nameof(response.AgentInstanceId));
        RequireNonNegative(response.ServerRevision, nameof(response.ServerRevision));

        if (!ProtocolOutcomes.IsKnown(response.Outcome))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Response outcome is not part of the v1 allow-list.",
                nameof(response.Outcome));
        }

        if (response.CommittedRevision is < 0)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidNumber,
                "committedRevision must be non-negative when present.",
                nameof(response.CommittedRevision));
        }

        if (response.Ok)
        {
            if (response.Error is not null || response.Payload is not { } payload ||
                payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    "A successful response must contain payload and a null error.",
                    nameof(response.Payload));
            }

            ProtocolJsonValueValidation.RequireObject(payload, nameof(response.Payload));
            var payloadBytes = RnCj1Canonicalizer.Canonicalize(payload);
            if (payloadBytes.Length > ProtocolLimits.MaxSuccessPayloadBytes)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    "Response payload exceeds its UTF-8 byte limit.",
                    nameof(response.Payload));
            }
        }
        else
        {
            if (response.Payload is not null || response.Error is null)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    "An error response must contain a null payload and an error object.",
                    nameof(response.Error));
            }

            response.Error.Validate();
        }
    }

    public static void Validate(ProtocolEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        @event.ProtocolVersion.ValidateCurrent();
        RequireExact(@event.MessageType, ProtocolMessageTypes.Event, nameof(@event.MessageType));
        RequireNonEmptyUuid(@event.EventId, nameof(@event.EventId));
        if (@event.EventType != ProtocolEventTypes.ChangesAvailable)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Event type is not part of the v1 allow-list.",
                nameof(@event.EventType));
        }

        RequireNonEmptyUuid(@event.AgentInstanceId, nameof(@event.AgentInstanceId));
        ProtocolValidation.RequireUtc(@event.OccurredAtUtc, nameof(@event.OccurredAtUtc));
        RequireNonNegative(@event.Revision, nameof(@event.Revision));
        ProtocolJsonValueValidation.RequireObject(@event.Payload, nameof(@event.Payload));

        var payloadBytes = RnCj1Canonicalizer.Canonicalize(@event.Payload);
        if (payloadBytes.Length > ProtocolLimits.MaxEventPayloadBytes)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Event payload exceeds its UTF-8 byte limit.",
                nameof(@event.Payload));
        }

        if (!TryGetProperty(@event.Payload, "fromRevision", out var fromRevision) ||
            !TryGetProperty(@event.Payload, "toRevision", out var toRevision))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.MissingField,
                "changes.available payload requires fromRevision and toRevision.",
                nameof(@event.Payload));
        }

        RequireJsonNonNegativeRevision(fromRevision, "fromRevision");
        RequireJsonNonNegativeRevision(toRevision, "toRevision");
    }

    public static void Validate(ProtocolError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        ProtocolErrorValidation.ValidateCode(error.Code);
        if (ProtocolErrorCodes.TryGetDefaultRetryable(error.Code, out var defaultRetryable) &&
            error.Retryable != defaultRetryable)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "retryable does not match the frozen error-code policy.",
                nameof(error.Retryable));
        }

        ProtocolJsonValueValidation.RequireObject(error.Details, nameof(error.Details));
        ProtocolErrorValidation.ValidateDetails(error.Details);
        var detailsBytes = RnCj1Canonicalizer.Canonicalize(error.Details);
        if (detailsBytes.Length > ProtocolLimits.MaxErrorDetailsBytes)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Error details exceed their UTF-8 byte limit.",
                nameof(error.Details));
        }

        if (error.HumanMessage is not null)
        {
            ProtocolValidation.NormalizeUnicode(error.HumanMessage, nameof(error.HumanMessage));
            ProtocolValidation.RequireUtf8ByteLength(
                error.HumanMessage,
                ProtocolLimits.MaxHumanMessageBytes,
                nameof(error.HumanMessage));
        }
    }

    private static void ValidateStatusPayload(JsonElement payload)
    {
        var key = RequireStringProperty(payload, "idempotencyKey");
        ProtocolValidation.RequireLowercaseUuid(key, "idempotencyKey");
    }

    private static void ValidateCancelPayload(JsonElement payload)
    {
        var requestId = RequireStringProperty(payload, "targetRequestId");
        var key = RequireStringProperty(payload, "targetIdempotencyKey");
        ProtocolValidation.RequireLowercaseUuid(requestId, "targetRequestId");
        ProtocolValidation.RequireLowercaseUuid(key, "targetIdempotencyKey");
    }

    private static void ValidateChangesPayload(JsonElement payload)
    {
        var afterRevision = RequireJsonNonNegativeRevision(
            RequireProperty(payload, "afterRevision"),
            "afterRevision");
        _ = afterRevision;

        var maxRevisions = RequireStrictInt32(RequireProperty(payload, "maxRevisions"), "maxRevisions");
        if (maxRevisions is < 1 or > 128)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "maxRevisions must be between 1 and 128.",
                "maxRevisions");
        }
    }

    private static JsonElement RequireProperty(JsonElement objectValue, string name)
    {
        if (!TryGetProperty(objectValue, name, out var value))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.MissingField,
                $"Required property '{name}' is missing.",
                name);
        }

        return value;
    }

    private static string RequireStringProperty(JsonElement objectValue, string name)
    {
        var value = RequireProperty(objectValue, name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"Property '{name}' must be a string.",
                name);
        }

        return text;
    }

    private static long RequireJsonNonNegativeRevision(JsonElement value, string fieldName)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !ProtocolNumber.TryParseNonNegativeInt64(value.GetRawText(), out var revision))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidNumber,
                $"{fieldName} must be a non-negative signed 64-bit integer.",
                fieldName);
        }

        return revision;
    }

    private static int RequireStrictInt32(JsonElement value, string fieldName)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !ProtocolNumber.TryParseInt32(value.GetRawText(), out var parsed))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidNumber,
                $"{fieldName} must be an integer.",
                fieldName);
        }

        return parsed;
    }

    private static bool TryGetProperty(JsonElement objectValue, string name, out JsonElement value)
    {
        foreach (var property in objectValue.EnumerateObject())
        {
            var normalized = ProtocolValidation.NormalizeUnicode(property.Name, "property name");
            if (string.Equals(normalized, name, StringComparison.Ordinal))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void RequireExact(string actual, string expected, string fieldName)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"{fieldName} must be '{expected}'.",
                fieldName);
        }
    }

    private static void RequireNonNegative(long value, string fieldName)
    {
        if (value < 0)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidNumber,
                $"{fieldName} must be non-negative.",
                fieldName);
        }
    }

    private static void RequireNonEmptyUuid(Guid value, string fieldName)
    {
        if (value == Guid.Empty)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"{fieldName} must not be empty.",
                fieldName);
        }
    }
}

internal static class ProtocolJsonValueValidation
{
    public static void RequireObject(JsonElement value, string fieldName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"{fieldName} must be a JSON object.",
                fieldName);
        }
    }
}

internal static class ProtocolErrorValidation
{
    private static readonly string[] ForbiddenDetailNames =
    [
        "title",
        "notes",
        "attachment",
        "token",
        "authorization",
        "authorizationheader",
        "header",
        "headers",
        "sql",
        "payload",
        "password",
        "secret",
        "credential",
        "connectionstring",
        "databasepath",
        "dataroot",
        "profilepath",
    ];

    public static void ValidateCode(string? code)
    {
        if (code is null || code.Length == 0 || code.Length > ProtocolLimits.MaxOperationBytes ||
            !ProtocolErrorCodes.IsKnownOrDomainCode(code) ||
            code.Any(character => !IsCodeCharacter(character)))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Error code is not a valid stable lowercase machine code.",
                nameof(code));
        }
    }

    private static bool IsCodeCharacter(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';

    public static void ValidateDetails(JsonElement value)
    {
        ValidateValue(value);
    }

    private static void ValidateValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var normalized = ProtocolValidation.NormalizeUnicode(property.Name, "error detail property name");
                var compactName = normalized
                    .Replace("_", string.Empty, StringComparison.Ordinal)
                    .Replace("-", string.Empty, StringComparison.Ordinal);
                if (ForbiddenDetailNames.Contains(compactName, StringComparer.OrdinalIgnoreCase))
                {
                    throw ProtocolContractException.Invalid(
                        ProtocolErrorCodes.InvalidRequest,
                        "Error details contain a forbidden sensitive field.",
                        property.Name);
                }

                ValidateValue(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateValue(item);
            }
        }
    }

}

internal static class ProtocolNumber
{
    public static bool TryParseNonNegativeInt64(string value, out long result)
    {
        result = 0;
        if (value.Length == 0 || value[0] == '-' || value[0] == '+' || value.Contains('.', StringComparison.Ordinal) ||
            value.Contains('e', StringComparison.OrdinalIgnoreCase) ||
            value.Length > 1 && value[0] == '0')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }

            if (result > (long.MaxValue - (character - '0')) / 10)
            {
                return false;
            }

            result = result * 10 + (character - '0');
        }

        return true;
    }

    public static bool TryParseInt32(string value, out int result)
    {
        result = 0;
        if (value.Length == 0 || value[0] == '+' || value.Contains('.', StringComparison.Ordinal) ||
            value.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var start = 0;
        var negative = false;
        if (value[0] == '-')
        {
            negative = true;
            start = 1;
            if (start == value.Length)
            {
                return false;
            }
        }

        if (value.Length - start > 1 && value[start] == '0')
        {
            return false;
        }

        long magnitude = 0;
        for (var index = start; index < value.Length; index++)
        {
            var character = value[index];
            if (character is < '0' or > '9' || magnitude > (long.MaxValue - (character - '0')) / 10)
            {
                return false;
            }

            magnitude = magnitude * 10 + (character - '0');
        }

        var signed = negative ? -magnitude : magnitude;
        if (signed is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        result = (int)signed;
        return true;
    }
}
