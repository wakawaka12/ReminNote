using ReminNote.Core.Protocol;

namespace ReminNote.Tests.P25Contract;

public sealed class ProtocolIdentityTests
{
    [Fact]
    public void RetryGetsNewWireRequestIdButPreservesLogicalMutationIdentity()
    {
        var clientInstanceId = ProtocolIds.NewClientInstanceId();
        var key = ProtocolIds.NewIdempotencyKey();
        var request = ProtocolRequest.CreateMutationAttempt(
            ProtocolClientKinds.Main,
            clientInstanceId,
            new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero),
            5_000,
            ProtocolOperations.TaskCreate,
            ProtocolJson.ParseObject("{\"title\":\"x\",\"timeSpec\":{}}"),
            key,
            0);
        var retry = request.CreateRetryAttempt(
            new DateTimeOffset(2026, 8, 30, 0, 0, 1, TimeSpan.Zero),
            2_000);

        Assert.NotEqual(request.RequestId, retry.RequestId);
        Assert.Equal(request.ClientInstanceId, retry.ClientInstanceId);
        Assert.Equal(request.IdempotencyKey, retry.IdempotencyKey);
        Assert.Equal(request.ExpectedRevision, retry.ExpectedRevision);
        Assert.Equal(
            RnCj1Canonicalizer.ComputeHash("p-demo", request.Operation, 0, request.Payload).HashHex,
            RnCj1Canonicalizer.ComputeHash("p-demo", retry.Operation, 0, retry.Payload).HashHex);

        request.Validate();
        retry.Validate();
        Assert.Equal(request.RequestId, request.RequestId.ToLowerInvariant());
        Assert.Equal(retry.RequestId, retry.RequestId.ToLowerInvariant());
    }

    [Fact]
    public void ProfileScopeDerivationIsStableAndNotOnlyPipeNameSecurity()
    {
        var first = ProtocolProfileScope.Derive(
            "S-1-5-21-100-200-300-1001",
            @"C:\Profiles\default\reminnote.sqlite");
        var same = ProtocolProfileScope.Derive(
            "S-1-5-21-100-200-300-1001",
            @"C:\Profiles\default\reminnote.sqlite");
        var otherUser = ProtocolProfileScope.Derive(
            "S-1-5-21-100-200-300-1002",
            @"C:\Profiles\default\reminnote.sqlite");

        Assert.Equal(first, same);
        Assert.NotEqual(first, otherUser);
        ProtocolProfileScope.Validate(first);
        Assert.StartsWith("p1-", first, StringComparison.Ordinal);
        Assert.Equal(67, first.Length);
    }
}
