using System.Text;
using System.Text.Json;
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
                "{\"title\":\"x\",\"timeSpec\":{\"type\":\"ANYTIME\",\"localDate\":\"2026-08-30\"}}",
                0,
                "{\"expectedRevision\":0,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.create\",\"payload\":{\"timeSpec\":{\"localDate\":\"2026-08-30\",\"type\":\"ANYTIME\"},\"title\":\"x\"},\"profileScope\":\"p-demo\"}",
                "e445f733020bef0299f1f0e797b8aa159120cfe5ea1197d589cecb74fa1b4ebd"
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
                "command.task.reorder",
                "{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"sortOrder\":-0}",
                7,
                "{\"expectedRevision\":7,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.reorder\",\"payload\":{\"sortOrder\":0,\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\"},\"profileScope\":\"p-demo\"}",
                "6549f1026a827c5175d947620278a6d649bb3888a98a5386879fd72a9fba950a"
            },
            {
                "V4a",
                "command.task.record_result",
                "{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"result\":\"COMPLETED\"}",
                7,
                "{\"expectedRevision\":7,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.record_result\",\"payload\":{\"result\":\"COMPLETED\",\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\"},\"profileScope\":\"p-demo\"}",
                "9c27378b473c8617021083bc7299cfe59482bfd2ec77207708d2ec6f4b446c3c"
            },
            {
                "V4b",
                "command.task.record_result",
                "{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"result\":\"COMPLETED\",\"note\":null}",
                7,
                "{\"expectedRevision\":7,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.record_result\",\"payload\":{\"note\":null,\"result\":\"COMPLETED\",\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\"},\"profileScope\":\"p-demo\"}",
                "e4717fdb8bc509fde6683a4712d7979f86c0cb283f28f01768630dde5285b739"
            },
            {
                "V5",
                "command.task.reorder",
                "{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"sortOrder\":1}",
                7,
                "{\"expectedRevision\":7,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.reorder\",\"payload\":{\"sortOrder\":1,\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\"},\"profileScope\":\"p-demo\"}",
                "0904c213f9e3e8f5adab5af5c798df0cc2c051380c676ef3a9c773033b5cf173"
            },
            {
                "V6",
                "command.task.reorder",
                "{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"sortOrder\":2}",
                7,
                "{\"expectedRevision\":7,\"hashVersion\":\"rn-cj-1\",\"operation\":\"command.task.reorder\",\"payload\":{\"sortOrder\":2,\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\"},\"profileScope\":\"p-demo\"}",
                "43271523dbcee90cccbbe8a150774438034f52167e2d14accf4ab883c0e3d4a0"
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
        var missing = RnCj1Canonicalizer.ComputeHash(
            "p-demo",
            ProtocolOperations.TaskRecordResult,
            7,
            "{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"result\":\"COMPLETED\"}");
        var explicitNull = RnCj1Canonicalizer.ComputeHash(
            "p-demo",
            ProtocolOperations.TaskRecordResult,
            7,
            "{\"taskId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"result\":\"COMPLETED\",\"note\":null}");

        Assert.NotEqual(missing.HashHex, explicitNull.HashHex);
    }

    [Fact]
    public void InvalidOperationPayloadIsRejectedBeforeHashing()
    {
        var exception = Assert.Throws<ProtocolContractException>(
            () => RnCj1Canonicalizer.ComputeHash(
                "p-demo",
                ProtocolOperations.TaskCreate,
                0,
                "{}"));

        Assert.Equal(ProtocolErrorCodes.MissingField, exception.Code);
    }

    [Fact]
    public void ProgrammaticJsonElementCannotBypassNestingLimit()
    {
        var json = "{}";
        for (var index = 0; index < ProtocolLimits.MaxJsonNestingDepth + 2; index++)
        {
            json = $"{{\"nested\":{json}}}";
        }

        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                MaxDepth = 128,
            });

        var exception = Assert.Throws<ProtocolContractException>(
            () => RnCj1Canonicalizer.Canonicalize(document.RootElement));

        Assert.Equal(ProtocolErrorCodes.InvalidJson, exception.Code);
    }
}
