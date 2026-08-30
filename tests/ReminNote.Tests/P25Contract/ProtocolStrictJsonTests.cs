using System.Text;
using ReminNote.Core.Protocol;

namespace ReminNote.Tests.P25Contract;

public sealed class ProtocolStrictJsonTests
{
    private const string ValidMutationRequest =
        "{\"protocolVersion\":\"1.0\",\"messageType\":\"request\",\"requestId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"clientKind\":\"main\",\"clientInstanceId\":\"019b2b36-4444-7abc-8def-0123456789ac\",\"sentAtUtc\":\"2026-08-30T00:00:00Z\",\"timeoutMs\":5000,\"operation\":\"command.task.create\",\"payload\":{\"title\":\"x\",\"timeSpec\":{}},\"idempotencyKey\":\"019b2b36-4444-7abc-8def-0123456789ae\",\"expectedRevision\":0}";

    [Theory]
    [InlineData("timeoutMs", "0", ProtocolErrorCodes.InvalidRequest)]
    [InlineData("timeoutMs", "30001", ProtocolErrorCodes.InvalidRequest)]
    [InlineData("timeoutMs", "5.0", ProtocolErrorCodes.InvalidNumber)]
    [InlineData("timeoutMs", "1e3", ProtocolErrorCodes.InvalidNumber)]
    [InlineData("expectedRevision", "-1", ProtocolErrorCodes.InvalidNumber)]
    [InlineData("expectedRevision", "1.0", ProtocolErrorCodes.InvalidNumber)]
    [InlineData("expectedRevision", "9223372036854775808", ProtocolErrorCodes.InvalidNumber)]
    public void IntegerAndRangeFieldsDoNotAcceptFloatingPointOrOutOfRangeNumbers(
        string field,
        string value,
        string expectedCode)
    {
        var request = ValidMutationRequest;
        var oldValue = field == "timeoutMs" ? "5000" : "0";
        request = request.Replace($"\"{field}\":{oldValue}", $"\"{field}\":{value}", StringComparison.Ordinal);

        var exception = Assert.Throws<ProtocolContractException>(
            () => ProtocolJson.DeserializeRequest(Encoding.UTF8.GetBytes(request)));
        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void RequestRejectsUppercaseUuidAndNonUtcTimestamp()
    {
        var uppercase = ValidMutationRequest.Replace(
            "019b2b36-4444-7abc-8def-0123456789ab",
            "019B2B36-4444-7abc-8def-0123456789ab",
            StringComparison.Ordinal);
        var uppercaseException = Assert.Throws<ProtocolContractException>(
            () => ProtocolJson.DeserializeRequest(Encoding.UTF8.GetBytes(uppercase)));
        Assert.Equal(ProtocolErrorCodes.InvalidRequest, uppercaseException.Code);

        var nonUtc = ValidMutationRequest.Replace(
            "2026-08-30T00:00:00Z",
            "2026-08-30T08:00:00+08:00",
            StringComparison.Ordinal);
        var nonUtcException = Assert.Throws<ProtocolContractException>(
            () => ProtocolJson.DeserializeRequest(Encoding.UTF8.GetBytes(nonUtc)));
        Assert.Equal(ProtocolErrorCodes.InvalidRequest, nonUtcException.Code);
    }

    [Fact]
    public void RequestRejectsMissingRequiredFieldsAndUnknownWritePayloadFields()
    {
        var missing = ValidMutationRequest.Replace(
            ",\"timeSpec\":{}",
            string.Empty,
            StringComparison.Ordinal);
        var missingException = Assert.Throws<ProtocolContractException>(
            () => ProtocolJson.DeserializeRequest(Encoding.UTF8.GetBytes(missing)));
        Assert.Equal(ProtocolErrorCodes.MissingField, missingException.Code);

        var unknown = ValidMutationRequest.Replace(
            "\"timeSpec\":{}",
            "\"timeSpec\":{},\"notAllowed\":true",
            StringComparison.Ordinal);
        var unknownException = Assert.Throws<ProtocolContractException>(
            () => ProtocolJson.DeserializeRequest(Encoding.UTF8.GetBytes(unknown)));
        Assert.Equal(ProtocolErrorCodes.UnknownField, unknownException.Code);

        var explicitNull = ValidMutationRequest.Replace(
            "\"title\":\"x\"",
            "\"title\":null",
            StringComparison.Ordinal);
        var nullException = Assert.Throws<ProtocolContractException>(
            () => ProtocolJson.DeserializeRequest(Encoding.UTF8.GetBytes(explicitNull)));
        Assert.Equal(ProtocolErrorCodes.InvalidRequest, nullException.Code);
    }

    [Fact]
    public void StrictJsonRejectsInvalidUtf8BomAndOverDepthWithoutDispatch()
    {
        var invalidUtf8 = new byte[] { (byte)'{', (byte)'"', (byte)'x', (byte)'"', (byte)':', 0xC3, 0x28, (byte)'}' };
        var invalidUtf8Exception = Assert.Throws<ProtocolContractException>(
            () => RnCj1Canonicalizer.Canonicalize(invalidUtf8));
        Assert.Equal(ProtocolErrorCodes.InvalidJson, invalidUtf8Exception.Code);

        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{}"));
        var bomException = Assert.Throws<ProtocolContractException>(
            () => RnCj1Canonicalizer.Canonicalize(withBom.ToArray()));
        Assert.Equal(ProtocolErrorCodes.InvalidJson, bomException.Code);

        var nested = "{}";
        for (var index = 0; index < ProtocolLimits.MaxJsonNestingDepth + 1; index++)
        {
            nested = $"{{\"x\":{nested}}}";
        }

        var depthException = Assert.Throws<ProtocolContractException>(
            () => RnCj1Canonicalizer.Canonicalize(nested));
        Assert.Equal(ProtocolErrorCodes.InvalidRequest, depthException.Code);
    }

    [Fact]
    public void StreamCodecUsesOnlyBoundedUtf8JsonBytes()
    {
        var request = ProtocolJson.DeserializeRequest(Encoding.UTF8.GetBytes(ValidMutationRequest));
        using var stream = new MemoryStream();
        ProtocolJson.SerializeTo(stream, request);
        stream.Position = 0;

        var result = ProtocolJson.Deserialize(stream);
        Assert.IsType<ProtocolRequest>(result);
        Assert.Equal(request.RequestId, ((ProtocolRequest)result).RequestId);
    }
}
