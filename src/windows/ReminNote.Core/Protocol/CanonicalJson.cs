using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReminNote.Core.Protocol;

/// <summary>
/// RN-CJ-1 canonical JSON and write-command hashing. The implementation is
/// intentionally independent of System.Text.Json property-order behavior.
/// </summary>
public static class RnCj1Canonicalizer
{
    public const string HashVersion = "rn-cj-1";

    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json)
    {
        using var document = StrictJson.Parse(utf8Json);
        return Canonicalize(document.RootElement);
    }

    public static byte[] Canonicalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Canonicalize(ProtocolLimits.StrictUtf8.GetBytes(json));
    }

    public static byte[] Canonicalize(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidJson,
                "Undefined JSON is not a wire value.");
        }

        ValidateNestingDepth(value);
        var builder = new StringBuilder();
        WriteCanonicalValue(value, builder);
        return ProtocolLimits.StrictUtf8.GetBytes(builder.ToString());
    }

    public static CanonicalHashResult ComputeHash(
        string profileScope,
        string operation,
        long expectedRevision,
        JsonElement effectivePayload)
    {
        var normalizedScope = ValidateProfileScope(profileScope);
        ProtocolValidation.RequireOperation(operation);
        ProtocolValidation.RequireNonNegativeRevision(expectedRevision, nameof(expectedRevision));
        if (effectivePayload.ValueKind != JsonValueKind.Object)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "The effective write payload must be a JSON object.",
                nameof(effectivePayload));
        }

        // Hashing is the final step of the request validation pipeline. Keeping
        // the operation schema check here prevents callers that bypass the wire
        // envelope validator from creating durable hashes for invalid commands.
        ProtocolOperationSchemas.ValidatePayload(operation, effectivePayload);
        var payload = Canonicalize(effectivePayload);
        if (payload.Length > ProtocolLimits.MaxWriteCommandPayloadBytes)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "Write command payload exceeds its UTF-8 byte limit.",
                nameof(effectivePayload));
        }

        var builder = new StringBuilder(payload.Length + 256);
        builder.Append("{\"expectedRevision\":");
        builder.Append(expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(",\"hashVersion\":");
        WriteCanonicalString(HashVersion, builder);
        builder.Append(",\"operation\":");
        WriteCanonicalString(operation, builder);
        builder.Append(",\"payload\":");
        builder.Append(ProtocolLimits.StrictUtf8.GetString(payload));
        builder.Append(",\"profileScope\":");
        WriteCanonicalString(normalizedScope, builder);
        builder.Append('}');

        var preimage = ProtocolLimits.StrictUtf8.GetBytes(builder.ToString());
        return new CanonicalHashResult(preimage, SHA256.HashData(preimage));
    }

    public static CanonicalHashResult ComputeHash(
        string profileScope,
        string operation,
        long expectedRevision,
        ReadOnlySpan<byte> effectivePayloadUtf8Json)
    {
        using var document = StrictJson.Parse(effectivePayloadUtf8Json);
        return ComputeHash(profileScope, operation, expectedRevision, document.RootElement);
    }

    public static CanonicalHashResult ComputeHash(
        string profileScope,
        string operation,
        long expectedRevision,
        string effectivePayloadJson)
    {
        ArgumentNullException.ThrowIfNull(effectivePayloadJson);
        return ComputeHash(
            profileScope,
            operation,
            expectedRevision,
            ProtocolLimits.StrictUtf8.GetBytes(effectivePayloadJson));
    }

    private static string ValidateProfileScope(string? profileScope)
    {
        if (string.IsNullOrWhiteSpace(profileScope))
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "ProfileScope must be non-empty.",
                nameof(profileScope));
        }

        var normalized = ProtocolValidation.NormalizeUnicode(profileScope, nameof(profileScope));
        ProtocolValidation.RequireUtf8ByteLength(normalized, 128, nameof(profileScope));
        foreach (var character in normalized)
        {
            if (character > 0x7F || char.IsControl(character) || char.IsWhiteSpace(character))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidRequest,
                    "ProfileScope must be a stable non-whitespace ASCII value.",
                    nameof(profileScope));
            }
        }

        return normalized;
    }

    private static void WriteCanonicalValue(JsonElement value, StringBuilder builder)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteCanonicalObject(value, builder);
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var firstArrayValue = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstArrayValue)
                    {
                        builder.Append(',');
                    }

                    firstArrayValue = false;
                    WriteCanonicalValue(item, builder);
                }

                builder.Append(']');
                break;

            case JsonValueKind.String:
                WriteCanonicalString(
                    ProtocolValidation.NormalizeUnicode(value.GetString() ?? string.Empty, "JSON string"),
                    builder);
                break;

            case JsonValueKind.Number:
                builder.Append(ProtocolDecimal.Normalize(value.GetRawText()));
                break;

            case JsonValueKind.True:
                builder.Append("true");
                break;

            case JsonValueKind.False:
                builder.Append("false");
                break;

            case JsonValueKind.Null:
                builder.Append("null");
                break;

            default:
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidJson,
                    "Unsupported JSON value kind.");
        }
    }

    private static void ValidateNestingDepth(JsonElement root)
    {
        var pending = new Stack<(JsonElement Value, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            var (value, depth) = pending.Pop();
            if (depth > ProtocolLimits.MaxJsonNestingDepth)
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.InvalidJson,
                    "JSON nesting depth exceeds the protocol limit.");
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                    {
                        pending.Push((property.Value, depth + 1));
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in value.EnumerateArray())
                    {
                        pending.Push((item, depth + 1));
                    }

                    break;
            }
        }
    }

    private static void WriteCanonicalObject(JsonElement value, StringBuilder builder)
    {
        var properties = new List<CanonicalProperty>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var normalizedName = ProtocolValidation.NormalizeUnicode(property.Name, "property name");
            if (!names.Add(normalizedName))
            {
                throw ProtocolContractException.Invalid(
                    ProtocolErrorCodes.DuplicateKey,
                    "Duplicate JSON property names, including NFC collisions, are not allowed.",
                    property.Name);
            }

            properties.Add(new CanonicalProperty(normalizedName, property.Value));
        }

        properties.Sort(static (left, right) => CompareUtf8(left.Name, right.Name));
        builder.Append('{');
        for (var index = 0; index < properties.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            WriteCanonicalString(properties[index].Name, builder);
            builder.Append(':');
            WriteCanonicalValue(properties[index].Value, builder);
        }

        builder.Append('}');
    }

    private static void WriteCanonicalString(string value, StringBuilder builder)
    {
        builder.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character <= 0x1F)
                    {
                        builder.Append("\\u00");
                        builder.Append(GetHexDigit(character >> 4));
                        builder.Append(GetHexDigit(character & 0x0F));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private static int CompareUtf8(string left, string right)
    {
        var leftBytes = ProtocolLimits.StrictUtf8.GetBytes(left);
        var rightBytes = ProtocolLimits.StrictUtf8.GetBytes(right);
        var length = Math.Min(leftBytes.Length, rightBytes.Length);
        for (var index = 0; index < length; index++)
        {
            var comparison = leftBytes[index].CompareTo(rightBytes[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftBytes.Length.CompareTo(rightBytes.Length);
    }

    private static char GetHexDigit(int value) =>
        (char)(value < 10 ? '0' + value : 'a' + value - 10);

    private readonly record struct CanonicalProperty(string Name, JsonElement Value);
}

/// <summary>
/// The canonical preimage and its binary SHA-256 digest. Hash bytes are always
/// 32 bytes and HashHex is lowercase for diagnostics/transport.
/// </summary>
public sealed class CanonicalHashResult
{
    public CanonicalHashResult(byte[] preimageUtf8, byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(preimageUtf8);
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length != 32)
        {
            throw new ArgumentException("A SHA-256 hash must contain 32 bytes.", nameof(hash));
        }

        PreimageUtf8 = preimageUtf8.ToArray();
        Hash = hash.ToArray();
    }

    public byte[] PreimageUtf8 { get; }

    public byte[] Hash { get; }

    public ReadOnlyMemory<byte> HashBytes => Hash;

    public string HashHex => Convert.ToHexString(Hash).ToLowerInvariant();

    public string PreimageText => ProtocolLimits.StrictUtf8.GetString(PreimageUtf8);
}

/// <summary>
/// Friendly façade for callers that do not need to reference the RN-CJ-1
/// implementation name directly.
/// </summary>
public static class ProtocolCanonicalization
{
    public const string HashVersion = RnCj1Canonicalizer.HashVersion;

    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json) =>
        RnCj1Canonicalizer.Canonicalize(utf8Json);

    public static byte[] Canonicalize(string json) =>
        RnCj1Canonicalizer.Canonicalize(json);

    public static byte[] Canonicalize(JsonElement value) =>
        RnCj1Canonicalizer.Canonicalize(value);

    public static CanonicalHashResult ComputeHash(
        string profileScope,
        string operation,
        long expectedRevision,
        JsonElement effectivePayload) =>
        RnCj1Canonicalizer.ComputeHash(profileScope, operation, expectedRevision, effectivePayload);

    public static CanonicalHashResult ComputeHash(
        string profileScope,
        string operation,
        long expectedRevision,
        ReadOnlySpan<byte> effectivePayloadUtf8Json) =>
        RnCj1Canonicalizer.ComputeHash(profileScope, operation, expectedRevision, effectivePayloadUtf8Json);
}

internal static class ProtocolDecimal
{
    public static string Normalize(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length == 0 || token.Length > 128)
        {
            throw InvalidNumber();
        }

        var index = 0;
        var negative = false;
        if (token[index] == '-')
        {
            negative = true;
            index++;
        }

        var integerStart = index;
        if (index >= token.Length)
        {
            throw InvalidNumber();
        }

        if (token[index] == '0')
        {
            index++;
            if (index < token.Length && IsDigit(token[index]))
            {
                throw InvalidNumber();
            }
        }
        else if (token[index] is >= '1' and <= '9')
        {
            index++;
            while (index < token.Length && IsDigit(token[index]))
            {
                index++;
            }
        }
        else
        {
            throw InvalidNumber();
        }

        var integerLength = index - integerStart;
        var fractionStart = index;
        var fractionLength = 0;
        if (index < token.Length && token[index] == '.')
        {
            index++;
            fractionStart = index;
            while (index < token.Length && IsDigit(token[index]))
            {
                index++;
            }

            fractionLength = index - fractionStart;
            if (fractionLength == 0)
            {
                throw InvalidNumber();
            }
        }

        var exponent = 0;
        if (index < token.Length && (token[index] == 'e' || token[index] == 'E'))
        {
            index++;
            var exponentNegative = false;
            if (index < token.Length && (token[index] == '+' || token[index] == '-'))
            {
                exponentNegative = token[index] == '-';
                index++;
            }

            var exponentDigits = index;
            var absoluteExponent = 0;
            while (index < token.Length && IsDigit(token[index]))
            {
                var digit = token[index] - '0';
                if (absoluteExponent > 128 || absoluteExponent > (128 - digit) / 10)
                {
                    throw InvalidNumber();
                }

                absoluteExponent = absoluteExponent * 10 + digit;
                index++;
            }

            if (index == exponentDigits || absoluteExponent > (exponentNegative ? 128 : 127))
            {
                throw InvalidNumber();
            }

            exponent = exponentNegative ? -absoluteExponent : absoluteExponent;
        }

        if (index != token.Length)
        {
            throw InvalidNumber();
        }

        var coefficient =
            token[integerStart..(integerStart + integerLength)] +
            token[fractionStart..(fractionStart + fractionLength)];
        var firstNonZero = 0;
        while (firstNonZero < coefficient.Length && coefficient[firstNonZero] == '0')
        {
            firstNonZero++;
        }

        if (firstNonZero == coefficient.Length)
        {
            return "0";
        }

        var significantDigits = coefficient[firstNonZero..];
        if (significantDigits.Length > 38)
        {
            throw InvalidNumber();
        }

        var decimalPosition = integerLength + exponent - firstNonZero;
        var plain = new StringBuilder(significantDigits.Length + 130);
        if (decimalPosition <= 0)
        {
            plain.Append("0.");
            plain.Append('0', -decimalPosition);
            plain.Append(significantDigits);
        }
        else if (decimalPosition >= significantDigits.Length)
        {
            plain.Append(significantDigits);
            plain.Append('0', decimalPosition - significantDigits.Length);
        }
        else
        {
            plain.Append(significantDigits[..decimalPosition]);
            plain.Append('.');
            plain.Append(significantDigits[decimalPosition..]);
        }

        var plainText = plain.ToString();
        var decimalPoint = plainText.IndexOf('.', StringComparison.Ordinal);
        if (decimalPoint >= 0)
        {
            var end = plainText.Length;
            while (end > decimalPoint + 1 && plainText[end - 1] == '0')
            {
                end--;
            }

            if (end == decimalPoint + 1)
            {
                end = decimalPoint;
            }

            plainText = plainText[..end];
        }

        return negative ? $"-{plainText}" : plainText;
    }

    private static bool IsDigit(char character) => character is >= '0' and <= '9';

    private static ProtocolContractException InvalidNumber() =>
        ProtocolContractException.Invalid(
            ProtocolErrorCodes.InvalidNumber,
            "JSON number is outside the RN-CJ-1 exact decimal grammar.");
}
