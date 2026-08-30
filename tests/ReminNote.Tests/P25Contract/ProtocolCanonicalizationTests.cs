using System.Text;
using ReminNote.Core.Protocol;

namespace ReminNote.Tests.P25Contract;

public sealed class ProtocolCanonicalizationTests
{
    public static TheoryData<string, string, string, long, string, string> GoldenVectors =>
        new()
        {
            {
                "V1",
                "command.task.create",
                "{}",
                0,
                "{\"expectedRevision\":0,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.create\",\"payload\":{},\"profileScope\":\"p-demo\"}",
                "c0f918047e32c6ae6ccbd68cdd970083df2ae657cedd3fa8f1816348d9d0ad39"
            },
            {
                "V2",
                "command.task.rename",
                "{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"title\":\"cafe\u0301\"}",
                7,
                "{\"expectedRevision\":7,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.rename\",\"payload\":{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"title\":\"café\"},\"profileScope\":\"p-demo\"}",
                "b6b7896d96cedf4d5a15ad4ca38b4f3b9e37be049a03971b1c2ea8ea6a668179"
            },
            {
                "V3",
                "command.task.create",
                "{\"values\":[-0,1.0,1e3,1e-3]}",
                7,
                "{\"expectedRevision\":7,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.create\",\"payload\":{\"values\":[0,1,1000,0.001]},\"profileScope\":\"p-demo\"}",
                "556d20c33d42ba11a02ea5f4cdbd20a88e25d258dd34d83724205fbdfa7356c2"
            },
            {
                "V4a",
                "command.task.rename",
                "{}",
                7,
                "{\"expectedRevision\":7,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.rename\",\"payload\":{},\"profileScope\":\"p-demo\"}",
                "923b95058e517a903b7998e2e7d3b227385887a16158bb7a4183cb950e55a484"
            },
            {
                "V4b",
                "command.task.rename",
                "{\"note\":null}",
                7,
                "{\"expectedRevision\":7,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.rename\",\"payload\":{\"note\":null},\"profileScope\":\"p-demo\"}",
                "e3e38311577adcf8a804828b825918c765c757565b9749db9a2bd506e99c0406"
            },
            {
                "V5",
                "command.task.create",
                "{\"values\":[1,2]}",
                7,
                "{\"expectedRevision\":7,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.create\",\"payload\":{\"values\":[1,2]},\"profileScope\":\"p-demo\"}",
                "12a87e535499f5eb72d5be610bf21ba4cb0b506769d1c16954c633e9bd84670c"
            },
            {
                "V6",
                "command.task.create",
                "{\"values\":[2,1]}",
                7,
                "{\"expectedRevision\":7,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.create\",\"payload\":{\"values\":[2,1]},\"profileScope\":\"p-demo\"}",
                "d7052fbb90bdc033d4fd6980b6d970abced6cbea39d342f0f0721a4bf7838162"
            },
        };

    [Theory]
    [MemberData(nameof(GoldenVectors))]
    public void FrozenGoldenVectorMatchesExactly(
        string id,
        string operation,
        string payload,
        long expectedRevision,
        string expectedPreimage,
        string expectedHash)
    {
        var result = RnCj1Canonicalizer.ComputeHash(
            "p-demo",
            operation,
            expectedRevision,
            payload);

        Assert.Equal(expectedPreimage, result.PreimageText);
        Assert.Equal(expectedHash, result.HashHex);
        Assert.Equal(32, result.Hash.Length);
        Assert.StartsWith("V", id, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalizationSortsObjectsByRecursiveUtf8BytesAndPreservesArrays()
    {
        var canonical = RnCj1Canonicalizer.Canonicalize(
            "{\"é\":{\"z\":1,\"a\":2},\"e\":0,\"items\":[3,1,2]}");

        Assert.Equal(
            "{\"e\":0,\"items\":[3,1,2],\"é\":{\"a\":2,\"z\":1}}",
            ProtocolLimits.StrictUtf8.GetString(canonical));
    }

    [Fact]
    public void ComposedAndDecomposedUnicodeProduceIdenticalCanonicalBytesAndHash()
    {
        var composed = RnCj1Canonicalizer.ComputeHash(
            "p-demo",
            ProtocolOperations.TaskRename,
            7,
            "{\"title\":\"café\",\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\"}");
        var decomposed = RnCj1Canonicalizer.ComputeHash(
            "p-demo",
            ProtocolOperations.TaskRename,
            7,
            "{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"title\":\"cafe\u0301\"}");

        Assert.Equal(composed.PreimageText, decomposed.PreimageText);
        Assert.Equal(composed.HashHex, decomposed.HashHex);
    }

    [Theory]
    [InlineData("{\"title\":\"a\",\"title\":\"b\"}", ProtocolErrorCodes.DuplicateKey)]
    [InlineData("{\"é\":1,\"e\u0301\":2}", ProtocolErrorCodes.DuplicateKey)]
    [InlineData("{\"value\":1e128}", ProtocolErrorCodes.InvalidNumber)]
    [InlineData("{\"value\":1e-129}", ProtocolErrorCodes.InvalidNumber)]
    [InlineData("{\"value\":123456789012345678901234567890123456789}", ProtocolErrorCodes.InvalidNumber)]
    [InlineData("{\"value\":NaN}", ProtocolErrorCodes.InvalidJson)]
    [InlineData("{\"value\":1,}", ProtocolErrorCodes.InvalidJson)]
    [InlineData("{\"value\":/* comment */1}", ProtocolErrorCodes.InvalidJson)]
    public void InvalidJsonAndNumbersAreRejectedBeforeHash(string json, string expectedCode)
    {
        var exception = Assert.Throws<ProtocolContractException>(
            () => RnCj1Canonicalizer.Canonicalize(json));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void Utf8BomIsRejectedAndCanonicalOutputHasNoBom()
    {
        var withBom = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("{}"))
            .ToArray();
        var exception = Assert.Throws<ProtocolContractException>(
            () => RnCj1Canonicalizer.Canonicalize(withBom));

        Assert.Equal(ProtocolErrorCodes.InvalidJson, exception.Code);
        Assert.False(RnCj1Canonicalizer.Canonicalize("{}").AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
    }

    [Fact]
    public void ExplicitNullAndMissingPropertyHaveDifferentHashes()
    {
        var missing = RnCj1Canonicalizer.ComputeHash("p-demo", ProtocolOperations.TaskRename, 7, "{}");
        var explicitNull = RnCj1Canonicalizer.ComputeHash(
            "p-demo",
            ProtocolOperations.TaskRename,
            7,
            "{\"note\":null}");

        Assert.NotEqual(missing.HashHex, explicitNull.HashHex);
    }
}
