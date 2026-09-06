using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using NodaTime;

#pragma warning disable CA1707 // Frozen wire names use uppercase underscore codes.
namespace ReminNote.Core.Reminders.Notifications;

/// <summary>
/// Bounds shared by the channel-neutral notification contract. These bounds
/// keep channel diagnostics and serialized delivery facts finite and free of
/// user content.
/// </summary>
public static class NotificationContractLimits
{
    public const int MaxChannelIdCharacters = 32;
    public const int MaxCapabilityCount = 8;
    public const int MaxStableCodeBytes = 96;
    public const int MaxPurposeCodeCharacters = 64;
    public const int MaxSummaryMemberCount = 64;
    public const int MaxSerializedChannelStatusBytes = 4_096;
    public const int MaxSerializedDeliveryAttemptBytes = 4_096;

    internal static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
}

/// <summary>
/// Machine-readable failures raised while constructing or consuming the
/// notification contract. Human-facing wording belongs outside Core.
/// </summary>
public sealed class NotificationContractException : ArgumentException
{
    public NotificationContractException(
        string code,
        string message,
        string? fieldName = null,
        Exception? innerException = null)
        : base(message, fieldName, innerException)
    {
        Code = code;
        FieldName = fieldName;
    }

    public string Code { get; }

    public string? FieldName { get; }

    public static NotificationContractException Invalid(
        string code,
        string message,
        string? fieldName = null,
        Exception? innerException = null) =>
        new(code, message, fieldName, innerException);
}

/// <summary>
/// Stable error codes for notification contract failures. Adapter-specific
/// diagnostics may be more detailed internally, but persisted/channel-facing
/// values must remain bounded stable codes.
/// </summary>
public static class NotificationErrorCodes
{
    public const string InvalidChannelId = "notification.channel.invalid_id";
    public const string UnknownChannel = "notification.channel.unknown";
    public const string ChannelIdentityMismatch = "notification.channel.identity_mismatch";
    public const string CapabilityMissing = "notification.channel.capability_missing";
    public const string ChannelBlocked = "notification.channel.blocked";
    public const string ChannelUnavailable = "notification.channel.unavailable";
    public const string ChannelMissing = "notification.channel.missing";
    public const string DeliveryFailed = "notification.delivery.failed";
    public const string DeliveryCancelled = "notification.delivery.cancelled";
    public const string DeliveryOutcomeInvalid = "notification.delivery.outcome_invalid";
    public const string IdempotencyConflict = "notification.idempotency.conflict";
    public const string InvalidLifecycle = "notification.lifecycle.invalid_state";
    public const string LifecycleTransitionInvalid = "notification.lifecycle.invalid_transition";
    public const string ResolutionConflict = "notification.lifecycle.action_conflict";
    public const string InvalidTimestamp = "notification.timestamp.invalid";
    public const string InvalidPurpose = "notification.purpose.invalid";
    public const string InvalidPriority = "notification.priority.invalid";
    public const string SerializationInvalid = "notification.serialization.invalid";
    public const string SerializationUnknownField = "notification.serialization.unknown_field";
    public const string SerializationTooLarge = "notification.serialization.too_large";
    public const string PolicySuppressedQuietHours = "notification.policy.suppressed_quiet_hours";
    /// <summary>
    /// A normal reminder was intentionally held for the end-of-quiet summary
    /// window. It remains retryable until the Agent emits the bounded summary
    /// effect.
    /// </summary>
    public const string PolicySummaryQueued = "notification.policy.summary_queued";
    /// <summary>
    /// This core reminder was included in an already-delivered quiet-hours
    /// summary. The core trigger and its durable attempt remain queryable; no
    /// second channel effect is required for the same summary window.
    /// </summary>
    public const string PolicySummaryAggregated = "notification.policy.summary_aggregated";

    /// <summary>
    /// A transient channel failure while presenting a summary. The suffix is
    /// the quiet-window end expressed as Unix seconds, allowing all members
    /// of one summary to remain grouped even when their retry time moves.
    /// </summary>
    public const string PolicySummaryRetryPrefix = "notification.policy.summary_retry.";

    public static string CreatePolicySummaryRetryCode(Instant summaryWindowEndUtc) =>
        PolicySummaryRetryPrefix +
        summaryWindowEndUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    public static bool TryParsePolicySummaryRetryCode(
        string? code,
        out long summaryWindowEndUnixSeconds)
    {
        summaryWindowEndUnixSeconds = default;
        if (code is null || !code.StartsWith(PolicySummaryRetryPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = code[PolicySummaryRetryPrefix.Length..];
        return suffix.Length > 0 &&
            long.TryParse(
                suffix,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out summaryWindowEndUnixSeconds);
    }
}

/// <summary>
/// Stable channel identifiers. The value object remains extensible for future
/// adapters, while P3 production dispatch accepts only the five frozen IDs.
/// </summary>
public readonly record struct NotificationChannelId
{
    public NotificationChannelId(string value)
    {
        Value = NotificationValidation.RequireChannelId(value);
    }

    public string Value { get; }

    public static NotificationChannelId Toast { get; } = new("TOAST");

    public static NotificationChannelId Tray { get; } = new("TRAY");

    public static NotificationChannelId Widget { get; } = new("WIDGET");

    public static NotificationChannelId Sound { get; } = new("SOUND");

    public static NotificationChannelId WakeTimer { get; } = new("WAKE_TIMER");

    public static NotificationChannelId Parse(string value) => new(value);

    public bool IsKnownP3Channel => NotificationChannels.IsKnown(this);

    public override string ToString() => Value ?? string.Empty;
}

public static class NotificationChannels
{
    public static IReadOnlyList<NotificationChannelId> P3 { get; } =
        new ReadOnlyCollection<NotificationChannelId>(
        [
            NotificationChannelId.Toast,
            NotificationChannelId.Tray,
            NotificationChannelId.Widget,
            NotificationChannelId.Sound,
            NotificationChannelId.WakeTimer,
        ]);

    public static bool IsKnown(NotificationChannelId channelId) =>
        channelId.Value is "TOAST" or "TRAY" or "WIDGET" or "SOUND" or "WAKE_TIMER";

    public static NotificationChannelId RequireKnown(NotificationChannelId channelId)
    {
        if (!IsKnown(channelId))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.UnknownChannel,
                $"Channel '{channelId.Value}' is not part of the P3 allow-list.",
                nameof(channelId));
        }

        return channelId;
    }
}

/// <summary>
/// Capabilities are deliberately separate from health. A healthy channel may
/// still lack a capability such as logical replacement or read-on-close.
/// </summary>
public enum NotificationCapability
{
    PRESENT,
    REPLACE_LOGICAL_REMINDER,
    READ_ON_CLOSE,
    WAKE,
}

public enum NotificationChannelHealth
{
    HEALTHY,
    DEGRADED,
    BLOCKED,
    UNAVAILABLE,
    UNKNOWN,
}

public enum NotificationPriority
{
    LOW,
    NORMAL,
    HIGH,
}

/// <summary>
/// Purpose is a bounded semantic snapshot, not display text. Reserved Anime
/// values are accepted as data but are not activated by the P3 coordinator.
/// </summary>
public readonly record struct NotificationPurposeSnapshot
{
    public NotificationPurposeSnapshot(string value)
    {
        Value = NotificationValidation.RequirePurpose(value);
    }

    public string Value { get; }

    public static NotificationPurposeSnapshot TaskPreStart { get; } = new("TASK_PRE_START");

    public static NotificationPurposeSnapshot TaskStart { get; } = new("TASK_START");

    public static NotificationPurposeSnapshot TaskRangeEnd { get; } = new("TASK_RANGE_END");

    public static NotificationPurposeSnapshot TaskCustom { get; } = new("TASK_CUSTOM");

    public static NotificationPurposeSnapshot AnimePreAiring { get; } = new("ANIME_PRE_AIRING");

    public static NotificationPurposeSnapshot AnimeAiring { get; } = new("ANIME_AIRING");

    public static NotificationPurposeSnapshot AnimeCustom { get; } = new("ANIME_CUSTOM");

    public static NotificationPurposeSnapshot Parse(string value) => new(value);

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// Bounded machine-readable metadata for one quiet-hours summary effect. It
/// contains only logical reminder IDs; the host turns the count and IDs into
/// constant UI text plus a collection launch URI, never arbitrary user text.
/// </summary>
public sealed record NotificationSummarySnapshot
{
    public NotificationSummarySnapshot(
        IEnumerable<Guid> logicalReminderIds,
        Instant? summaryWindowEndUtc = null)
    {
        ArgumentNullException.ThrowIfNull(logicalReminderIds);
        var values = logicalReminderIds.ToArray();
        if (values.Length == 0 || values.Length > NotificationContractLimits.MaxSummaryMemberCount)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationTooLarge,
                $"A notification summary must contain between one and {NotificationContractLimits.MaxSummaryMemberCount} members.",
                nameof(logicalReminderIds));
        }

        if (values.Distinct().Count() != values.Length)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "A notification summary cannot contain duplicate logical reminder IDs.",
                nameof(logicalReminderIds));
        }

        for (var index = 0; index < values.Length; index++)
        {
            NotificationValidation.RequireUuidV7(values[index], $"{nameof(logicalReminderIds)}[{index}]");
        }

        LogicalReminderIds = new ReadOnlyCollection<Guid>(
            values
                .OrderBy(value => value.ToString("D"), StringComparer.Ordinal)
                .ToArray());
        SummaryWindowEndUtc = summaryWindowEndUtc;
    }

    public IReadOnlyList<Guid> LogicalReminderIds { get; }

    public int Count => LogicalReminderIds.Count;

    /// <summary>
    /// Internal grouping context. It is not user content and is omitted from
    /// the host bridge payload; the Agent uses it only to keep retries in one
    /// quiet-window group.
    /// </summary>
    public Instant? SummaryWindowEndUtc { get; }
}

/// <summary>
/// A channel's observed capability and health snapshot. This is read-only
/// input to dispatch policy; it is never a substitute for a core trigger fact.
/// </summary>
public sealed record NotificationChannelStatus
{
    public NotificationChannelStatus(
        NotificationChannelId channelId,
        IEnumerable<NotificationCapability> capabilities,
        NotificationChannelHealth health,
        Instant observedAtUtc,
        string? healthCode = null)
    {
        // P3-00 freezes the channel allow-list.  The value object itself can
        // still carry a future identifier, but a status advertised to the
        // dispatcher must be one of the channels the Agent understands.
        ChannelId = NotificationChannels.RequireKnown(channelId);
        ArgumentNullException.ThrowIfNull(capabilities);

        var values = capabilities.ToArray();
        if (values.Length > NotificationContractLimits.MaxCapabilityCount)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationTooLarge,
                "A channel cannot advertise more than the bounded capability count.",
                nameof(capabilities));
        }

        if (values.Any(capability => !Enum.IsDefined(capability)))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.CapabilityMissing,
                "The channel capability list contains an unknown value.",
                nameof(capabilities));
        }

        if (values.Distinct().Count() != values.Length)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "The channel capability list cannot contain duplicates.",
                nameof(capabilities));
        }

        if (!Enum.IsDefined(health))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                "The channel health value is not supported.",
                nameof(health));
        }

        NotificationValidation.ValidateOptionalStableCode(healthCode, nameof(healthCode));

        var ordered = values.OrderBy(capability => capability).ToArray();
        Capabilities = new ReadOnlyCollection<NotificationCapability>(ordered);
        Health = health;
        ObservedAtUtc = observedAtUtc;
        HealthCode = healthCode;
    }

    public NotificationChannelId ChannelId { get; }

    public IReadOnlyList<NotificationCapability> Capabilities { get; }

    public NotificationChannelHealth Health { get; }

    public Instant ObservedAtUtc { get; }

    public string? HealthCode { get; }

    public bool Supports(NotificationCapability capability) => Capabilities.Contains(capability);

    public bool CanPresent =>
        Supports(NotificationCapability.PRESENT) &&
        Health is NotificationChannelHealth.HEALTHY or NotificationChannelHealth.DEGRADED;

    public bool IsBlocked => Health == NotificationChannelHealth.BLOCKED;

    public bool IsUnavailable =>
        Health is NotificationChannelHealth.UNAVAILABLE or NotificationChannelHealth.UNKNOWN;
}

internal static class NotificationValidation
{
    public static string RequireChannelId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > NotificationContractLimits.MaxChannelIdCharacters)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidChannelId,
                "Channel ID must be non-empty and bounded.",
                nameof(value));
        }

        foreach (var character in value)
        {
            if (character is not (>= 'A' and <= 'Z') &&
                character is not (>= '0' and <= '9') &&
                character != '_')
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.InvalidChannelId,
                    "Channel ID must use uppercase ASCII letters, digits, and underscores.",
                    nameof(value));
            }
        }

        return value;
    }

    public static string RequirePurpose(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > NotificationContractLimits.MaxPurposeCodeCharacters)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidPurpose,
                "Purpose must be a bounded non-empty code.",
                nameof(value));
        }

        foreach (var character in value)
        {
            if (character is not (>= 'A' and <= 'Z') &&
                character is not (>= '0' and <= '9') &&
                character != '_')
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.InvalidPurpose,
                    "Purpose must use uppercase ASCII letters, digits, and underscores.",
                    nameof(value));
            }
        }

        return value;
    }

    public static Guid RequireUuidV7(Guid value, string fieldName)
    {
        var text = value.ToString("D", CultureInfo.InvariantCulture);
        Span<byte> bytes = stackalloc byte[16];
        var hasBytes = value.TryWriteBytes(bytes);
        if (value == Guid.Empty ||
            text.Length != 36 ||
            text[14] != '7' ||
            !hasBytes ||
            (bytes[8] & 0xC0) != 0x80)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                $"{fieldName} must be a non-empty UUID v7.",
                fieldName);
        }

        return value;
    }

    public static string RequireIdempotencyCode(string? value, string fieldName)
    {
        if (value is null || !Guid.TryParseExact(value, "D", out var parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            parsed.ToString("D", CultureInfo.InvariantCulture)[14] != '7')
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                $"{fieldName} must be a lowercase UUID v7 in D format.",
                fieldName);
        }

        return value;
    }

    public static void ValidateOptionalStableCode(string? value, string fieldName)
    {
        if (value is null)
        {
            return;
        }

        int bytes;
        try
        {
            bytes = NotificationContractLimits.StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                $"{fieldName} must be valid UTF-8 stable text.",
                fieldName,
                exception);
        }
        if (value.Length == 0 || bytes > NotificationContractLimits.MaxStableCodeBytes)
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.SerializationInvalid,
                $"{fieldName} must be a bounded stable code.",
                fieldName);
        }

        foreach (var character in value)
        {
            if (character is not (>= 'a' and <= 'z') &&
                character is not (>= '0' and <= '9') &&
                character is not ('.' or '_' or '-'))
            {
                throw NotificationContractException.Invalid(
                    NotificationErrorCodes.SerializationInvalid,
                    $"{fieldName} must use lowercase ASCII stable-code characters.",
                    fieldName);
            }
        }
    }

    public static void ValidateKnownP3Channel(NotificationChannelId channelId) =>
        NotificationChannels.RequireKnown(channelId);

    public static void ValidatePriority(NotificationPriority priority)
    {
        if (!Enum.IsDefined(priority))
        {
            throw NotificationContractException.Invalid(
                NotificationErrorCodes.InvalidPriority,
                "Priority is not part of the notification allow-list.",
                nameof(priority));
        }
    }
}

#pragma warning restore CA1707
