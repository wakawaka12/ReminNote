using ReminNote.Core.Protocol;

namespace ReminNote.Tests.P25Contract;

public sealed class ProtocolNegotiationTests
{
    [Fact]
    public void NegotiationSelectsHighestCommonMinorAndSeparatesDowngradedFeatures()
    {
        var result = ProtocolVersionNegotiator.Negotiate(
            [new ProtocolVersion(1, 0), new ProtocolVersion(1, 1)],
            [new ProtocolVersion(1, 1), new ProtocolVersion(1, 2)],
            ["changes.available", "future.optional"],
            [],
            ["changes.available"]);

        Assert.Equal(new ProtocolVersion(1, 1), result.SelectedProtocolVersion);
        Assert.Equal(["changes.available"], result.AcceptedFeatures);
        Assert.Equal(["future.optional"], result.DowngradedFeatures);
        Assert.True(result.Ready);
    }

    [Fact]
    public void RequiredFeatureOrMajorMismatchIsStableFailure()
    {
        var missingFeature = Assert.Throws<ProtocolContractException>(() =>
            ProtocolVersionNegotiator.Negotiate(
                [ProtocolVersion.Current],
                [ProtocolVersion.Current],
                [],
                ["readonly-snapshot-v1"],
                []));
        Assert.Equal(ProtocolErrorCodes.FeatureRequired, missingFeature.Code);

        var unsupportedVersion = Assert.Throws<ProtocolContractException>(() =>
            ProtocolVersionNegotiator.Negotiate(
                [new ProtocolVersion(2, 0)],
                [ProtocolVersion.Current],
                [],
                [],
                []));
        Assert.Equal(ProtocolErrorCodes.UnsupportedVersion, unsupportedVersion.Code);
    }

    [Fact]
    public void HelloPayloadValidatesBoundedListsAndRevision()
    {
        var hello = new ProtocolHelloPayload(
            [ProtocolVersion.Current],
            ["changes.available"],
            ["changes.available"],
            LastSeenRevision: 0,
            ProfileHint: "p-demo");
        hello.Validate();

        var invalid = new ProtocolHelloPayload(
            [ProtocolVersion.Current],
            [],
            [],
            LastSeenRevision: -1);
        var exception = Assert.Throws<ProtocolContractException>(invalid.Validate);
        Assert.Equal(ProtocolErrorCodes.InvalidNumber, exception.Code);
    }
}
