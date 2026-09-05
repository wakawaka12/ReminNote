using System.Collections.ObjectModel;
using System.Text.Json;

namespace ReminNote.Core.Protocol;

/// <summary>
/// The v1 operation allow-list and the fields that may occur in each payload.
/// This is deliberately small: SQL, EF entities and product features outside
/// the existing P2 Task surface are not wire operations.
/// </summary>
public static class ProtocolOperationSchemas
{
    private static readonly ReadOnlyDictionary<string, OperationSchema> Schemas =
        new ReadOnlyDictionary<string, OperationSchema>(
            new Dictionary<string, OperationSchema>(StringComparer.Ordinal)
            {
                [ProtocolOperations.SessionHello] = new(
                    ProtocolOperations.SessionHello,
                    requiredFields: ["supportedProtocolVersions", "requestedFeatures", "requiredFeatures"],
                    optionalFields: ["lastSeenRevision", "profileHint"]),
                [ProtocolOperations.CommandStatus] = new(
                    ProtocolOperations.CommandStatus,
                    requiredFields: ["idempotencyKey"],
                    optionalFields: []),
                [ProtocolOperations.RequestCancel] = new(
                    ProtocolOperations.RequestCancel,
                    requiredFields: ["targetRequestId", "targetIdempotencyKey"],
                    optionalFields: []),
                [ProtocolOperations.ChangesGetSince] = new(
                    ProtocolOperations.ChangesGetSince,
                    requiredFields: ["afterRevision", "maxRevisions"],
                    optionalFields: []),
                [ProtocolOperations.TaskCreate] = new(
                    ProtocolOperations.TaskCreate,
                    requiredFields: ["title", "timeSpec"],
                    optionalFields: ["reminder"]),
                [ProtocolOperations.TaskRename] = new(
                    ProtocolOperations.TaskRename,
                    requiredFields: ["taskId", "title"],
                    optionalFields: []),
                [ProtocolOperations.TaskRecordResult] = new(
                    ProtocolOperations.TaskRecordResult,
                    requiredFields: ["taskId", "result"],
                    optionalFields: ["note"],
                    nullableFields: ["note"]),
                [ProtocolOperations.TaskUpdatePlan] = new(
                    ProtocolOperations.TaskUpdatePlan,
                    requiredFields: ["taskId", "timeSpec"],
                    optionalFields: ["title", "reminder"]),
                [ProtocolOperations.TaskReorder] = new(
                    ProtocolOperations.TaskReorder,
                    requiredFields: ["taskId", "sortOrder"],
                    optionalFields: []),
                [ProtocolOperations.TaskContinue] = new(
                    ProtocolOperations.TaskContinue,
                    requiredFields: ["sourceTaskId", "title", "timeSpec"],
                    optionalFields: ["reminder"]),
                [ProtocolOperations.TaskDelete] = new(
                    ProtocolOperations.TaskDelete,
                    requiredFields: ["taskId"],
                    optionalFields: []),
                [ProtocolOperations.ReminderRuleUpsert] = new(
                    ProtocolOperations.ReminderRuleUpsert,
                    requiredFields: ["taskId", "reminder"],
                    optionalFields: ["ruleId"]),
                [ProtocolOperations.ReminderMarkRead] = new(
                    ProtocolOperations.ReminderMarkRead,
                    requiredFields: ["instanceId"],
                    optionalFields: []),
                [ProtocolOperations.ReminderResolve] = new(
                    ProtocolOperations.ReminderResolve,
                    requiredFields: ["instanceId", "action"],
                    optionalFields: ["snoozeSeconds"]),
            });

    public static bool TryGet(string operation, out OperationSchema schema) =>
        Schemas.TryGetValue(operation, out schema!);

    public static OperationSchema Get(string operation)
    {
        if (!TryGet(operation, out var schema))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Operation is not part of the v1 allow-list.",
                nameof(operation));
        }

        return schema;
    }

    public static void ValidatePayload(string operation, JsonElement payload)
    {
        var schema = Get(operation);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Request payload must be a JSON object.",
                nameof(payload));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            var normalizedName = ProtocolValidation.NormalizeUnicode(property.Name, "payload property name");
            if (!seen.Add(normalizedName))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.UnicodeCollision,
                    "Payload property names collide after NFC normalization.",
                    property.Name);
            }

            if (!schema.AllowedFields.Contains(normalizedName))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.UnknownField,
                    $"Payload field '{property.Name}' is not allowed for {operation}.",
                    property.Name);
            }

            if (property.Value.ValueKind == JsonValueKind.Null &&
                !schema.NullableFields.Contains(normalizedName))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    $"Payload field '{property.Name}' cannot be null for {operation}.",
                    property.Name);
            }

            ValidateKnownFieldShape(operation, normalizedName, property.Value);
        }

        foreach (var requiredField in schema.RequiredFields)
        {
            if (!seen.Contains(requiredField))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.MissingField,
                    $"Payload field '{requiredField}' is required for {operation}.",
                    requiredField);
            }
        }
    }

    private static void ValidateKnownFieldShape(
        string operation,
        string fieldName,
        JsonElement value)
    {
        var requiresString = operation switch
        {
            ProtocolOperations.TaskCreate when fieldName == "title" => true,
            ProtocolOperations.TaskRename when fieldName is "taskId" or "title" => true,
            ProtocolOperations.TaskRecordResult when fieldName is "taskId" or "result" => true,
            ProtocolOperations.TaskRecordResult when fieldName == "note" => value.ValueKind != JsonValueKind.Null,
            ProtocolOperations.TaskUpdatePlan when fieldName is "taskId" or "title" => true,
            ProtocolOperations.TaskReorder when fieldName == "taskId" => true,
            ProtocolOperations.TaskContinue when fieldName is "sourceTaskId" or "title" => true,
            ProtocolOperations.TaskDelete when fieldName == "taskId" => true,
            ProtocolOperations.ReminderRuleUpsert when fieldName is "taskId" or "ruleId" => true,
            ProtocolOperations.ReminderMarkRead when fieldName == "instanceId" => true,
            ProtocolOperations.ReminderResolve when fieldName is "instanceId" or "action" => true,
            _ => false,
        };

        if (requiresString && value.ValueKind != JsonValueKind.String)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"Payload field '{fieldName}' must be a string.",
                fieldName);
        }

        if (fieldName == "timeSpec")
        {
            ProtocolTimeSpecPayload.Parse(value);
        }

        if (fieldName == "reminder")
        {
            _ = ProtocolReminderRulePayloadParser.Parse(value);
        }

        if (operation == ProtocolOperations.ReminderRuleUpsert && fieldName == "ruleId" &&
            value.ValueKind == JsonValueKind.String)
        {
            ProtocolValidation.RequireLowercaseUuid(value.GetString(), fieldName);
        }

        if (operation == ProtocolOperations.SessionHello)
        {
            if (fieldName is "supportedProtocolVersions" or "requestedFeatures" or "requiredFeatures" &&
                value.ValueKind != JsonValueKind.Array)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    $"Payload field '{fieldName}' must be an array.",
                    fieldName);
            }

            if (fieldName is "requestedFeatures" or "requiredFeatures")
            {
                if (value.GetArrayLength() > 64)
                {
                    throw ProtocolContractException.Invalid(
                        ProtocolErrorCodes.InvalidRequest,
                        $"Payload field '{fieldName}' contains too many values.",
                        fieldName);
                }

                var featureNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } feature)
                    {
                        throw ProtocolContractException.Invalid(
                            ProtocolErrorCodes.InvalidRequest,
                            $"Payload field '{fieldName}' must contain only strings.",
                            fieldName);
                    }

                    if (!featureNames.Add(feature))
                    {
                        throw ProtocolContractException.Invalid(
                            ProtocolErrorCodes.InvalidRequest,
                            $"Payload field '{fieldName}' contains duplicate feature names.",
                            fieldName);
                    }

                    ProtocolValidation.RequireUtf8ByteLength(feature, 96, fieldName);
                }
            }
            else if (fieldName == "supportedProtocolVersions")
            {
                if (value.GetArrayLength() is < 1 or > ProtocolLimits.MaxSupportedProtocolVersions)
                {
                    throw ProtocolContractException.Invalid(
                        ProtocolErrorCodes.InvalidRequest,
                        "supportedProtocolVersions must contain between 1 and 8 values.",
                        fieldName);
                }

                var versions = new HashSet<ProtocolVersion>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } versionText)
                    {
                        throw ProtocolContractException.Invalid(
                            ProtocolErrorCodes.InvalidRequest,
                            "supportedProtocolVersions must contain version strings.",
                            fieldName);
                        }

                    var version = ProtocolVersion.Parse(versionText);
                    if (!versions.Add(version))
                    {
                        throw ProtocolContractException.Invalid(
                            ProtocolErrorCodes.InvalidRequest,
                            "supportedProtocolVersions must not contain duplicates.",
                            fieldName);
                    }
                }
            }
        }

        if (operation == ProtocolOperations.SessionHello && fieldName == "lastSeenRevision" &&
            (value.ValueKind != JsonValueKind.Number ||
             !ProtocolNumber.TryParseNonNegativeInt64(value.GetRawText(), out _)))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidNumber,
                "lastSeenRevision must be a non-negative signed 64-bit integer.",
                fieldName);
        }

        if (operation == ProtocolOperations.SessionHello && fieldName == "profileHint" &&
            value.ValueKind != JsonValueKind.String)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "profileHint must be a string.",
                fieldName);
        }

        if (operation == ProtocolOperations.TaskReorder && fieldName == "sortOrder" &&
            (value.ValueKind != JsonValueKind.Number ||
             !ProtocolNumber.TryParseInt32(value.GetRawText(), out _)))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidNumber,
                "sortOrder must be a 32-bit integer.",
                fieldName);
        }

        if (operation is ProtocolOperations.ReminderMarkRead or ProtocolOperations.ReminderResolve &&
            fieldName == "instanceId" && value.ValueKind == JsonValueKind.String)
        {
            ProtocolValidation.RequireLowercaseUuid(value.GetString(), fieldName);
        }

        if (operation == ProtocolOperations.ReminderResolve && fieldName == "action" &&
            value.ValueKind == JsonValueKind.String)
        {
            var action = value.GetString();
            if (action is not ("DONE" or "SNOOZE" or "SKIP" or "IGNORE"))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    "Reminder action is not enabled for TASK_INSTANCE.",
                    fieldName);
            }
        }

        if (operation == ProtocolOperations.ReminderResolve && fieldName == "snoozeSeconds" &&
            (value.ValueKind != JsonValueKind.Number ||
             !ProtocolNumber.TryParseNonNegativeInt64(value.GetRawText(), out var snoozeSeconds) ||
             snoozeSeconds is < 1 or > 604_800))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidNumber,
                "snoozeSeconds must be an integer between 1 and 604800.",
                fieldName);
        }
    }

    public sealed class OperationSchema
    {
        internal OperationSchema(
            string operation,
            IReadOnlyCollection<string> requiredFields,
            IReadOnlyCollection<string> optionalFields,
            IReadOnlyCollection<string>? nullableFields = null)
        {
            Operation = operation;
            RequiredFields = Array.AsReadOnly(requiredFields.ToArray());
            AllowedFields = new HashSet<string>(
                requiredFields.Concat(optionalFields),
                StringComparer.Ordinal);
            NullableFields = new HashSet<string>(nullableFields ?? [], StringComparer.Ordinal);
        }

        public string Operation { get; }

        public IReadOnlyList<string> RequiredFields { get; }

        public IReadOnlySet<string> AllowedFields { get; }

        public IReadOnlySet<string> NullableFields { get; }
    }
}
