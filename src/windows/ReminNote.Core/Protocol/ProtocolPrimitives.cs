using System.Globalization;
using System.Text;

namespace ReminNote.Core.Protocol;

/// <summary>
/// The only wire version implemented by the first ReminNote business contract.
/// </summary>
public readonly record struct ProtocolVersion(int Major, int Minor)
{
    public static ProtocolVersion Current { get; } = new(1, 0);

    public override string ToString() => $"{Major.ToString(CultureInfo.InvariantCulture)}.{Minor.ToString(CultureInfo.InvariantCulture)}";

    public static ProtocolVersion Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var separator = value.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1 || value.IndexOf('.', separator + 1) >= 0)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.UnsupportedVersion,
                "Protocol version must use the major.minor form.",
                nameof(value));
        }

        var majorText = value[..separator];
        var minorText = value[(separator + 1)..];
        if (!IsUnsignedDecimal(majorText) || !IsUnsignedDecimal(minorText))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.UnsupportedVersion,
                "Protocol version must contain unsigned decimal components.",
                nameof(value));
        }

        var majorParsed = int.TryParse(
            majorText,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var major);
        var minorParsed = int.TryParse(
            minorText,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var minor);
        if (majorText.Length > 1 && majorText[0] == '0' ||
            minorText.Length > 1 && minorText[0] == '0' ||
            !majorParsed || !minorParsed)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.UnsupportedVersion,
                "Protocol version contains an out-of-range or non-canonical component.",
                nameof(value));
        }

        return new ProtocolVersion(major, minor);
    }

    public static bool TryParse(string? value, out ProtocolVersion version)
    {
        if (value is null)
        {
            version = default;
            return false;
        }

        try
        {
            version = Parse(value);
            return true;
        }
        catch (ProtocolContractException)
        {
            version = default;
            return false;
        }
    }

    public void ValidateShape()
    {
        if (Major < 0 || Minor < 0)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.UnsupportedVersion,
                "Protocol version components cannot be negative.",
                nameof(ProtocolVersion));
        }
    }

    public void ValidateCurrent()
    {
        if (this != Current)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.UnsupportedVersion,
                $"Protocol version {this} is not supported by the v1 contract.",
                nameof(ProtocolVersion));
        }
    }

    private static bool IsUnsignedDecimal(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Machine-readable protocol failure. The code is the contract; Message is
/// intentionally non-localized diagnostic text for tests and developers.
/// </summary>
public sealed class ProtocolContractException : FormatException
{
    public ProtocolContractException(
        string code,
        string message,
        string? fieldName = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        FieldName = fieldName;
    }

    public string Code { get; }

    public string? FieldName { get; }

    public static ProtocolContractException Invalid(
        string code,
        string message,
        string? fieldName = null,
        Exception? innerException = null) =>
        new(code, message, fieldName, innerException);
}

public static class ProtocolMessageTypes
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
}

public static class ProtocolClientKinds
{
    public const string Main = "main";
    public const string Widget = "widget";
}

public static class ProtocolOperations
{
    public const string SessionHello = "session.hello";
    public const string CommandStatus = "command.status";
    public const string RequestCancel = "request.cancel";
    public const string ChangesGetSince = "changes.get_since";
    public const string TaskCreate = "command.task.create";
    public const string TaskRename = "command.task.rename";
    public const string TaskRecordResult = "command.task.record_result";
    public const string TaskUpdatePlan = "command.task.update_plan";
    public const string TaskReorder = "command.task.reorder";
    public const string TaskContinue = "command.task.continue";
    public const string TaskDelete = "command.task.delete";
    public const string ReminderRuleUpsert = "command.reminder.rule.upsert";
    public const string ReminderMarkRead = "command.reminder.mark_read";
    public const string ReminderResolve = "command.reminder.resolve";

    public static bool IsKnown(string operation) =>
        operation is SessionHello or CommandStatus or RequestCancel or ChangesGetSince or
            TaskCreate or TaskRename or TaskRecordResult or TaskUpdatePlan or TaskReorder or
            TaskContinue or TaskDelete or ReminderRuleUpsert or ReminderMarkRead or ReminderResolve;

    public static bool IsMutation(string operation) =>
        operation is TaskCreate or TaskRename or TaskRecordResult or TaskUpdatePlan or
            TaskReorder or TaskContinue or TaskDelete or ReminderRuleUpsert or ReminderMarkRead or ReminderResolve;
}

public static class ProtocolEventTypes
{
    public const string ChangesAvailable = "changes.available";
}

public static class ProtocolOutcomes
{
    public const string Changed = "changed";
    public const string NoOp = "no-op";
    public const string Replayed = "replayed";
    public const string Stale = "stale";
    public const string Rejected = "rejected";
    public const string RolledBack = "rolledBack";
    public const string Cancelled = "cancelled";
    public const string Timeout = "timeout";
    public const string Pending = "pending";
    public const string Unknown = "unknown";

    public static bool IsKnown(string value) =>
        value is Changed or NoOp or Replayed or Stale or Rejected or RolledBack or Cancelled or
            Timeout or Pending or Unknown;
}

public static class ProtocolReceiptStatuses
{
    public const string Pending = "PENDING";
    public const string Committed = "COMMITTED";
    public const string RejectedStale = "REJECTED_STALE";
    public const string Rejected = "REJECTED";
    public const string RolledBack = "ROLLED_BACK";
    public const string Cancelled = "CANCELLED";
    public const string TimedOut = "TIMED_OUT";
    public const string Unknown = "UNKNOWN";
}

public static class ProtocolErrorCodes
{
    public const string InvalidJson = "ipc.protocol.invalid_json";
    public const string InvalidFrame = "ipc.protocol.invalid_frame";
    public const string FrameTooLarge = "ipc.protocol.frame_too_large";
    public const string MissingField = "ipc.protocol.missing_field";
    public const string UnknownField = "ipc.protocol.unknown_field";
    public const string DuplicateKey = "ipc.protocol.duplicate_key";
    public const string UnicodeCollision = "ipc.protocol.unicode_collision";
    public const string UnsupportedVersion = "ipc.protocol.unsupported_version";
    public const string FeatureRequired = "ipc.protocol.feature_required";
    public const string AccessDenied = "ipc.auth.access_denied";
    public const string UserMismatch = "ipc.auth.user_mismatch";
    public const string ProfileMismatch = "ipc.auth.profile_mismatch";
    public const string AgentUnavailable = "ipc.agent.unavailable";
    public const string AgentNotReady = "ipc.agent.not_ready";
    public const string AgentShuttingDown = "ipc.agent.shutting_down";
    public const string InvalidRequest = "ipc.request.invalid";
    public const string InvalidNumber = "ipc.request.invalid_number";
    public const string Overloaded = "ipc.request.overloaded";
    public const string Timeout = "ipc.request.timeout";
    public const string Cancelled = "ipc.request.cancelled";
    public const string NotFound = "ipc.request.not_found";
    public const string IdempotencyConflict = "ipc.idempotency.conflict";
    public const string ExpectedRevisionMismatch = "revision.expected_mismatch";
    public const string RevisionGap = "revision.gap";
    public const string RevisionAhead = "revision.ahead";
    public const string RevisionBatchTooLarge = "revision.batch_too_large";
    public const string StorageNotReady = "storage.not_ready";
    public const string StorageReadOnly = "storage.read_only";
    public const string StorageBusy = "storage.busy";
    public const string TransactionFailed = "storage.transaction_failed";
    public const string IntegrityFailed = "storage.integrity_failed";
    public const string SingleInstanceExists = "lifecycle.single_instance_exists";
    public const string ActivationFailed = "lifecycle.activation_failed";
    public const string SafeMode = "lifecycle.safe_mode";
    public const string WriterGenerationConflict = "lifecycle.writer_generation_conflict";
    public const string ReceiptCapacity = "storage.receipt_capacity";

    public static bool IsProtocolCode(string code) =>
        code is InvalidJson or InvalidFrame or FrameTooLarge or MissingField or UnknownField or
            DuplicateKey or UnicodeCollision or UnsupportedVersion or FeatureRequired;

    public static bool IsKnownOrDomainCode(string code) =>
        IsProtocolCode(code) || code.StartsWith("ipc.", StringComparison.Ordinal) ||
        code.StartsWith("revision.", StringComparison.Ordinal) ||
        code.StartsWith("storage.", StringComparison.Ordinal) ||
        code.StartsWith("lifecycle.", StringComparison.Ordinal) ||
        code.StartsWith("task.", StringComparison.Ordinal) ||
        code.StartsWith("reminder.", StringComparison.Ordinal);

    public static bool TryGetDefaultRetryable(string code, out bool retryable)
    {
        retryable = code switch
        {
            InvalidJson or InvalidFrame or FrameTooLarge or MissingField or UnknownField or
                DuplicateKey or UnicodeCollision or UnsupportedVersion or FeatureRequired or
                AccessDenied or UserMismatch or ProfileMismatch or InvalidRequest or InvalidNumber or
                Cancelled or NotFound or IdempotencyConflict or ExpectedRevisionMismatch or
                RevisionBatchTooLarge or StorageReadOnly or IntegrityFailed or SingleInstanceExists or
                ActivationFailed or WriterGenerationConflict or ReceiptCapacity => false,
            AgentUnavailable or AgentNotReady or AgentShuttingDown or Overloaded or Timeout or
                RevisionGap or RevisionAhead or StorageNotReady or StorageBusy or TransactionFailed or
                SafeMode => true,
            _ => false,
        };

        return code is InvalidJson or InvalidFrame or FrameTooLarge or MissingField or UnknownField or
            DuplicateKey or UnicodeCollision or UnsupportedVersion or FeatureRequired or AccessDenied or
            UserMismatch or ProfileMismatch or AgentUnavailable or AgentNotReady or AgentShuttingDown or
            InvalidRequest or InvalidNumber or Overloaded or Timeout or Cancelled or NotFound or
            IdempotencyConflict or ExpectedRevisionMismatch or RevisionGap or RevisionAhead or
            RevisionBatchTooLarge or StorageNotReady or StorageReadOnly or StorageBusy or
            TransactionFailed or IntegrityFailed or SingleInstanceExists or ActivationFailed or SafeMode or
            WriterGenerationConflict or ReceiptCapacity;
    }
}

public static class ProtocolLimits
{
    public const int MaxFrameBytes = 1_048_576;
    public const int MaxWriteCommandPayloadBytes = 262_144;
    public const int MaxEventPayloadBytes = 65_536;
    public const int MaxErrorDetailsBytes = 8_192;
    public const int MaxSuccessPayloadBytes = 16_384;
    public const int MaxJsonNestingDepth = 32;
    public const int MaxInFlightPerConnection = 32;
    public const int MaxQueuedRequestsPerProfile = 256;
    public const int ConnectDeadlineMilliseconds = 2_000;
    public const int ReadDeadlineMilliseconds = 2_000;
    public const int DefaultMutationTimeoutMilliseconds = 5_000;
    public const int MaxRequestDeadlineMilliseconds = 30_000;
    public const int MaxHumanMessageBytes = 512;
    public const int MaxDetailsBytes = MaxErrorDetailsBytes;
    public const int MaxOperationBytes = 96;
    public const long MaxRevision = long.MaxValue;
    public const int MaxSupportedProtocolVersions = 8;

    public static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}

public static class ProtocolValidation
{
    public static string RequireLowercaseUuid(string? value, string fieldName)
    {
        if (value is null || !Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"{fieldName} must be a lowercase UUID in D format.",
                fieldName);
        }

        return value;
    }

    public static string RequireOperation(string? value)
    {
        if (value is null || !ProtocolOperations.IsKnown(value))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Operation is not part of the v1 allow-list.",
                nameof(value));
        }

        RequireUtf8ByteLength(value, ProtocolLimits.MaxOperationBytes, nameof(value));
        return value;
    }

    public static void RequireUtf8ByteLength(string value, int maximumBytes, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount;
        try
        {
            byteCount = ProtocolLimits.StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"{fieldName} contains invalid Unicode.",
                fieldName,
                exception);
        }

        if (byteCount > maximumBytes)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"{fieldName} exceeds its UTF-8 byte limit.",
                fieldName);
        }
    }

    public static long RequireNonNegativeRevision(long? value, string fieldName)
    {
        if (value is null || value.Value < 0)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidNumber,
                $"{fieldName} must be a non-negative signed 64-bit integer.",
                fieldName);
        }

        return value.Value;
    }

    public static void RequireUtc(DateTimeOffset value, string fieldName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                $"{fieldName} must use UTC.",
                fieldName);
        }
    }

    public static string NormalizeUnicode(string value, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value);

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw ProtocolContractException.Invalid(
                        ProtocolErrorCodes.InvalidJson,
                        $"{fieldName} contains an unpaired UTF-16 surrogate.",
                        fieldName);
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidJson,
                    $"{fieldName} contains an unpaired UTF-16 surrogate.",
                    fieldName);
            }
        }

        return value.Normalize(NormalizationForm.FormC);
    }
}
