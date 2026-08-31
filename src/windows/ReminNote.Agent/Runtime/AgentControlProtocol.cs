using System.Text.Json;
using ReminNote.Core.Protocol;

namespace ReminNote.Agent.Runtime;

internal static class AgentControlProtocol
{
    private const string HealthOperation = "health";
    private const string BootstrapClient = "bootstrap";
    private const int MaxControlPayloadBytes = 8 * 1024;

    public static byte[] CreateHealthRequest(string profileScope)
    {
        ProtocolProfileScope.Validate(profileScope);
        return Serialize(new
        {
            operation = HealthOperation,
            client = BootstrapClient,
            profileScope
        });
    }

    public static byte[] CreateHealthResponse(string profileScope, Guid agentInstanceId)
    {
        ProtocolProfileScope.Validate(profileScope);
        return Serialize(new
        {
            operation = HealthOperation,
            ready = true,
            profileScope,
            agentInstanceId = agentInstanceId.ToString("D")
        });
    }

    public static void ValidateHealthRequest(ReadOnlyMemory<byte> payload, string expectedProfileScope)
    {
        ProtocolProfileScope.Validate(expectedProfileScope);
        using var document = Parse(payload);
        var root = document.RootElement;
        RequireObject(root);
        RequireExactProperties(root, "operation", "client", "profileScope");
        RequireString(root, "operation", HealthOperation);
        RequireString(root, "client", BootstrapClient);
        RequireString(root, "profileScope", expectedProfileScope);
    }

    public static AgentHealthInfo ParseHealthResponse(
        ReadOnlyMemory<byte> payload,
        string expectedProfileScope)
    {
        ProtocolProfileScope.Validate(expectedProfileScope);
        using var document = Parse(payload);
        var root = document.RootElement;
        RequireObject(root);
        RequireExactProperties(root, "operation", "ready", "profileScope", "agentInstanceId");
        RequireString(root, "operation", HealthOperation);
        if (!root.GetProperty("ready").GetBoolean())
        {
            throw new InvalidOperationException("The Agent control health response is not ready.");
        }

        var profileScope = RequireString(root, "profileScope");
        if (!string.Equals(profileScope, expectedProfileScope, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Agent control profile does not match.");
        }

        var agentInstanceId = RequireString(root, "agentInstanceId");
        ProtocolValidation.RequireLowercaseUuid(agentInstanceId, "agentInstanceId");
        return new AgentHealthInfo(profileScope, agentInstanceId);
    }

    private static byte[] Serialize(object value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaxControlPayloadBytes)
        {
            throw new InvalidOperationException("The Agent control payload exceeds its limit.");
        }

        return payload;
    }

    private static JsonDocument Parse(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > MaxControlPayloadBytes)
        {
            throw new InvalidOperationException("The Agent control payload size is invalid.");
        }

        return JsonDocument.Parse(
            payload,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
    }

    private static void RequireObject(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The Agent control payload must be an object.");
        }
    }

    private static void RequireExactProperties(JsonElement root, params string[] expected)
    {
        var actual = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length ||
            actual.Except(expected, StringComparer.Ordinal).Any() ||
            expected.Except(actual, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("The Agent control payload contains an unknown or missing field.");
        }
    }

    private static string RequireString(JsonElement root, string propertyName, string? expected = null)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            throw new InvalidOperationException($"The Agent control field '{propertyName}' must be a string.");
        }

        if (expected is not null && !string.Equals(text, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The Agent control field '{propertyName}' is not accepted.");
        }

        return text;
    }
}

public sealed record AgentHealthInfo(string ProfileScope, string AgentInstanceId);
