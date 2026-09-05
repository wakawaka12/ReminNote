using System.Security.Cryptography;

namespace ReminNote.Core.Protocol;

/// <summary>
/// Identity helpers shared by clients and the future Agent. They are pure
/// value generation/derivation helpers; OS token and path canonicalization are
/// deliberately supplied by the hosting layer.
/// </summary>
public static class ProtocolIds
{
    public static string NewWireRequestId() => NewUuid();

    public static string NewClientInstanceId() => NewUuid();

    public static string NewIdempotencyKey() => NewUuid();

    public static Guid NewEventId() => Guid.CreateVersion7();

    private static string NewUuid() => Guid.CreateVersion7().ToString("D");
}

public static class ProtocolProfileScope
{
    private const string Prefix = "ReminNote.ProfileScope.v1";

    public static string Derive(string actualUserSid, string canonicalDbPath)
    {
        var normalizedSid = ValidateInput(actualUserSid, nameof(actualUserSid));
        var normalizedPath = ValidateInput(canonicalDbPath, nameof(canonicalDbPath));
        var input = string.Concat(Prefix, '\0', normalizedSid, '\0', normalizedPath);
        var digest = SHA256.HashData(ProtocolLimits.StrictUtf8.GetBytes(input));
        return $"p1-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    public static void Validate(string? profileScope)
    {
        if (profileScope is null || profileScope.Length != 67 ||
            !profileScope.StartsWith("p1-", StringComparison.Ordinal) ||
            profileScope.Skip(3).Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "ProfileScope must be p1- followed by 64 lowercase hexadecimal characters.",
                nameof(profileScope));
        }
    }

    private static string ValidateInput(string? value, string fieldName)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\0', StringComparison.Ordinal))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "ProfileScope derivation inputs must be non-empty and contain no NUL.",
                fieldName);
        }

        return ProtocolValidation.NormalizeUnicode(value, fieldName);
    }
}

public static class ProtocolPipeNames
{
    public static string Business(string profileScope) =>
        Create("ReminNote.Business", profileScope);

    public static string Control(string profileScope) =>
        Create("ReminNote.Control", profileScope);

    public static string MainActivation(string profileScope) =>
        Create("ReminNote.Windows.Main.Activation", profileScope);

    /// <summary>
    /// Per-host notification effect bridge. Main and Widget deliberately use
    /// separate endpoints so a Widget request can never be accepted by the
    /// Main host (or vice versa) and then reported as a false delivery.
    /// </summary>
    public static string NotificationHost(string profileScope, string hostKind)
    {
        ProtocolProfileScope.Validate(profileScope);
        if (!string.Equals(hostKind, "Main", StringComparison.Ordinal) &&
            !string.Equals(hostKind, "Widget", StringComparison.Ordinal))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Notification host kind must be Main or Widget.",
                nameof(hostKind));
        }

        return Create($"ReminNote.Notification.{hostKind}", profileScope);
    }

    public static string WidgetActivation(string profileScope, string widgetInstanceId)
    {
        ProtocolProfileScope.Validate(profileScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetInstanceId);

        var input = string.Concat("ReminNote.WidgetInstance.v1", '\0', widgetInstanceId);
        var digest = SHA256.HashData(ProtocolLimits.StrictUtf8.GetBytes(input));
        var instanceHash = Convert.ToHexString(digest).ToLowerInvariant()[..32];
        return $"ReminNote.Widget.Activation.{profileScope}.{instanceHash}";
    }

    private static string Create(string prefix, string profileScope)
    {
        ProtocolProfileScope.Validate(profileScope);
        return $"{prefix}.{profileScope}";
    }
}
