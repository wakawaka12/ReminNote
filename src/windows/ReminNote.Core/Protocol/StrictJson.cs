using System.Buffers;
using System.Text;
using System.Text.Json;

namespace ReminNote.Core.Protocol;

internal static class StrictJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = ProtocolLimits.MaxJsonNestingDepth,
    };

    public static JsonDocument Parse(ReadOnlySpan<byte> utf8Json)
    {
        Validate(utf8Json);

        try
        {
            return JsonDocument.Parse(utf8Json.ToArray(), DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidJson,
                "The JSON document is not valid RFC 8259 JSON.",
                innerException: exception);
        }
    }

    public static JsonElement ParseObject(ReadOnlySpan<byte> utf8Json)
    {
        using var document = Parse(utf8Json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "The JSON document must contain an object.");
        }

        return document.RootElement.Clone();
    }

    public static void Validate(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidJson,
                "The JSON document cannot be empty.");
        }

        if (utf8Json.Length > ProtocolLimits.MaxFrameBytes)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.FrameTooLarge,
                "The JSON document exceeds the maximum frame size.");
        }

        if (utf8Json.Length >= 3 &&
            utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidJson,
                "UTF-8 BOM is not allowed on the wire.");
        }

        var reader = new Utf8JsonReader(
            utf8Json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                // Leave one level for the reader so the contract-specific
                // depth check below can report the stable invalid-request
                // code instead of a generic JsonException.
                MaxDepth = ProtocolLimits.MaxJsonNestingDepth + 1,
            });
        var objectScopes = new Stack<HashSet<string>>();
        var depth = 0;
        var rootCompleted = false;

        try
        {
            while (reader.Read())
            {
                if (rootCompleted)
                {
                    throw ProtocolContractException.Invalid(
                        ProtocolErrorCodes.InvalidJson,
                        "A JSON document must contain exactly one root value.");
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        depth++;
                        RequireDepth(depth);
                        objectScopes.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;

                    case JsonTokenType.EndObject:
                        if (objectScopes.Count == 0)
                        {
                            throw ProtocolContractException.Invalid(
                                ProtocolErrorCodes.InvalidJson,
                                "Unexpected end of JSON object.");
                        }

                        objectScopes.Pop();
                        depth--;
                        CompleteRootIfNeeded(ref rootCompleted, depth);
                        break;

                    case JsonTokenType.StartArray:
                        depth++;
                        RequireDepth(depth);
                        break;

                    case JsonTokenType.EndArray:
                        if (depth == 0)
                        {
                            throw ProtocolContractException.Invalid(
                                ProtocolErrorCodes.InvalidJson,
                                "Unexpected end of JSON array.");
                        }

                        depth--;
                        CompleteRootIfNeeded(ref rootCompleted, depth);
                        break;

                    case JsonTokenType.PropertyName:
                        if (objectScopes.Count == 0)
                        {
                            throw ProtocolContractException.Invalid(
                                ProtocolErrorCodes.InvalidJson,
                                "A property name must occur inside an object.");
                        }

                        var propertyName = reader.GetString();
                        if (propertyName is null)
                        {
                            throw ProtocolContractException.Invalid(
                                ProtocolErrorCodes.InvalidJson,
                                "A JSON property name cannot be null.");
                        }

                        var normalizedName = ProtocolValidation.NormalizeUnicode(propertyName, "property name");
                        if (!objectScopes.Peek().Add(normalizedName))
                        {
                            throw ProtocolContractException.Invalid(
                                ProtocolErrorCodes.DuplicateKey,
                                "Duplicate JSON property names, including NFC collisions, are not allowed.",
                                propertyName);
                        }

                        break;

                    case JsonTokenType.String:
                        _ = ProtocolValidation.NormalizeUnicode(
                            reader.GetString() ?? throw new InvalidOperationException("JSON string was null."),
                            "JSON string");
                        CompleteScalarIfNeeded(ref rootCompleted, depth);
                        break;

                    case JsonTokenType.Number:
                        _ = ProtocolDecimal.Normalize(GetRawToken(ref reader));
                        CompleteScalarIfNeeded(ref rootCompleted, depth);
                        break;

                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                        CompleteScalarIfNeeded(ref rootCompleted, depth);
                        break;
                }
            }
        }
        catch (ProtocolContractException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidJson,
                "The JSON document is not valid RFC 8259 JSON.",
                innerException: exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidJson,
                "The JSON document is not valid UTF-8.",
                innerException: exception);
        }
        catch (InvalidOperationException exception)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidJson,
                "The JSON document is not valid RFC 8259 JSON.",
                innerException: exception);
        }

        if (!rootCompleted || depth != 0 || objectScopes.Count != 0)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidJson,
                "The JSON document is incomplete.");
        }
    }

    private static string GetRawToken(ref Utf8JsonReader reader)
    {
        ReadOnlySpan<byte> bytes = reader.HasValueSequence
            ? reader.ValueSequence.ToArray()
            : reader.ValueSpan;
        return ProtocolLimits.StrictUtf8.GetString(bytes);
    }

    private static void RequireDepth(int depth)
    {
        if (depth > ProtocolLimits.MaxJsonNestingDepth)
        {
            throw ProtocolContractException.Invalid(
                ProtocolErrorCodes.InvalidRequest,
                "JSON nesting depth exceeds the v1 limit.");
        }
    }

    private static void CompleteScalarIfNeeded(ref bool rootCompleted, int depth)
    {
        if (depth == 0)
        {
            rootCompleted = true;
        }
    }

    private static void CompleteRootIfNeeded(ref bool rootCompleted, int depth)
    {
        if (depth == 0)
        {
            rootCompleted = true;
        }
    }
}
