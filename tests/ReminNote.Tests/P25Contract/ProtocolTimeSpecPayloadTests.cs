using NodaTime;
using ReminNote.Core.Protocol;
using ReminNote.Core.Tasks;

namespace ReminNote.Tests.P25Contract;

public sealed class ProtocolTimeSpecPayloadTests
{
    [Theory]
    [InlineData("{\"type\":\"ANYTIME\",\"localDate\":\"2026-08-31\"}")]
    [InlineData("{\"type\":\"TIME\",\"localDate\":\"2026-08-31\",\"time\":\"09:05\"}")]
    [InlineData("{\"type\":\"RANGE\",\"localDate\":\"2026-08-31\",\"start\":\"23:00\",\"end\":\"01:00\"}")]
    public void WireShapesRoundTripToDomainAndBack(string json)
    {
        var parsed = ProtocolTimeSpecPayload.Parse(ProtocolJson.ParseObject(json));
        var domain = parsed.ToDomain();
        var roundTrip = ProtocolTimeSpecPayload.FromDomain(domain);

        Assert.Equal(parsed, roundTrip);
    }

    [Fact]
    public void RangeCrossMidnightIsPreservedAndEqualEndpointsAreRejected()
    {
        var parsed = ProtocolTimeSpecPayload.Parse(ProtocolJson.ParseObject(
            "{\"type\":\"RANGE\",\"localDate\":\"2026-08-31\",\"start\":\"23:00\",\"end\":\"01:00\"}"));

        var range = Assert.IsType<TimeRangeSpec>(parsed.ToDomain());
        Assert.True(range.IsCrossMidnight);
        Assert.Equal(new LocalDate(2026, 9, 1), range.EndLocalDate);

        var exception = Assert.Throws<ProtocolContractException>(() =>
            ProtocolTimeSpecPayload.Parse(ProtocolJson.ParseObject(
                "{\"type\":\"RANGE\",\"localDate\":\"2026-08-31\",\"start\":\"09:00\",\"end\":\"09:00\"}")));
        Assert.Equal(ProtocolErrorCodes.InvalidRequest, exception.Code);
    }

    [Theory]
    [InlineData("{}", ProtocolErrorCodes.MissingField)]
    [InlineData("{\"type\":\"ANYTIME\"}", ProtocolErrorCodes.MissingField)]
    [InlineData("{\"type\":\"TIME\",\"localDate\":\"2026-08-31\"}", ProtocolErrorCodes.MissingField)]
    [InlineData("{\"type\":\"ANYTIME\",\"localDate\":\"2026-08-31\",\"time\":\"09:00\"}", ProtocolErrorCodes.InvalidRequest)]
    [InlineData("{\"type\":\"TIME\",\"localDate\":\"2026-08-31\",\"time\":\"9:00\"}", ProtocolErrorCodes.InvalidRequest)]
    [InlineData("{\"type\":\"TIME\",\"localDate\":\"2026-02-30\",\"time\":\"09:00\"}", ProtocolErrorCodes.InvalidRequest)]
    [InlineData("{\"type\":\"TIME\",\"localDate\":\"2026-08-31\",\"time\":null}", ProtocolErrorCodes.InvalidRequest)]
    [InlineData("{\"type\":\"TIME\",\"localDate\":\"2026-08-31\",\"time\":\"09:00\",\"start\":\"10:00\"}", ProtocolErrorCodes.InvalidRequest)]
    [InlineData("{\"type\":\"NOPE\",\"localDate\":\"2026-08-31\"}", ProtocolErrorCodes.InvalidRequest)]
    [InlineData("{\"type\":\"TIME\",\"localDate\":\"2026-08-31\",\"future\":true}", ProtocolErrorCodes.UnknownField)]
    public void InvalidNestedShapesAreRejectedBeforeHash(string json, string expectedCode)
    {
        var exception = Assert.Throws<ProtocolContractException>(() =>
            ProtocolTimeSpecPayload.Parse(ProtocolJson.ParseObject(json)));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void CanonicalHashIncludesTheValidatedNestedShape()
    {
        var anytime = RnCj1Canonicalizer.ComputeHash(
            "p-demo",
            ProtocolOperations.TaskCreate,
            0,
            "{\"title\":\"x\",\"timeSpec\":{\"type\":\"ANYTIME\",\"localDate\":\"2026-08-31\"}}");
        var timed = RnCj1Canonicalizer.ComputeHash(
            "p-demo",
            ProtocolOperations.TaskCreate,
            0,
            "{\"title\":\"x\",\"timeSpec\":{\"type\":\"TIME\",\"localDate\":\"2026-08-31\",\"time\":\"09:00\"}}");

        Assert.NotEqual(anytime.HashHex, timed.HashHex);
    }
}
