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
