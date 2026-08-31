using System.Text;
using System.Text.Json;
using ReminNote.Core.Protocol;

namespace ReminNote.Tests.P25Contract;

public sealed class ProtocolEnvelopeTests
{
    private const string RequestId = "019b2b36-4444-7abc-8def-0123456789ab";
    private const string ClientInstanceId = "019b2b36-4444-7abc-8def-0123456789ac";
    private const string AgentInstanceId = "019b2b36-4444-7abc-8def-0123456789ad";
    private const string IdempotencyKey = "019b2b36-4444-7abc-8def-0123456789ae";

    private static readonly DateTimeOffset SentAtUtc =
        new(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MutationRequestUsesExplicitStableFieldOrderAndRoundTrips()
    {
        var request = new ProtocolRequest(
            ProtocolVersion.Current,
            RequestId,
            ProtocolClientKinds.Main,
            ClientInstanceId,
            SentAtUtc,
            5_000,
            ProtocolOperations.TaskCreate,
            ProtocolJson.ParseObject("{\"title\":\"整理房间\",\"timeSpec\":{\"type\":\"ANYTIME\",\"localDate\":\"2026-08-30\"}}"),
            IdempotencyKey,
            7);

        var wire = ProtocolJson.SerializeRequest(request);
        var text = Encoding.UTF8.GetString(wire);

        Assert.Equal(
            "{\"protocolVersion\":\"1.0\",\"messageType\":\"request\",\"requestId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"clientKind\":\"main\",\"clientInstanceId\":\"019b2b36-4444-7abc-8def-0123456789ac\",\"sentAtUtc\":\"2026-08-30T00:00:00.0000000Z\",\"timeoutMs\":5000,\"operation\":\"command.task.create\",\"payload\":{\"timeSpec\":{\"localDate\":\"2026-08-30\",\"type\":\"ANYTIME\"},\"title\":\"整理房间\"},\"idempotencyKey\":\"019b2b36-4444-7abc-8def-0123456789ae\",\"expectedRevision\":7}",
            text);

        var roundTrip = ProtocolJson.DeserializeRequest(wire);
        Assert.Equal(request.ProtocolVersion, roundTrip.ProtocolVersion);
        Assert.Equal(request.RequestId, roundTrip.RequestId);
        Assert.Equal(request.ClientInstanceId, roundTrip.ClientInstanceId);
        Assert.Equal(request.Operation, roundTrip.Operation);
        Assert.Equal(request.IdempotencyKey, roundTrip.IdempotencyKey);
        Assert.Equal(request.ExpectedRevision, roundTrip.ExpectedRevision);
        Assert.Equal("整理房间", roundTrip.Payload.GetProperty("title").GetString());
    }

    [Fact]
    public void ReadOnlyStatusCancelAndChangesRequestsHaveTheirOwnPayloadRules()
    {
        var status = new ProtocolRequest(
            ProtocolVersion.Current,
            RequestId,
            ProtocolClientKinds.Widget,
            ClientInstanceId,
            SentAtUtc,
            2_000,
            ProtocolOperations.CommandStatus,
            ProtocolJson.CreateStatusPayload(IdempotencyKey));
        var statusWire = ProtocolJson.SerializeRequest(status);
        var statusRoundTrip = ProtocolJson.DeserializeRequest(statusWire);
        Assert.Equal(IdempotencyKey, ProtocolJson.ReadStatusPayload(statusRoundTrip).IdempotencyKey);
        Assert.DoesNotContain("idempotencyKey", Encoding.UTF8.GetString(statusWire)[^2..]);

        var cancel = new ProtocolRequest(
            ProtocolVersion.Current,
            RequestId,
            ProtocolClientKinds.Main,
            ClientInstanceId,
            SentAtUtc,
            1_000,
            ProtocolOperations.RequestCancel,
            ProtocolJson.CreateCancelPayload(RequestId, IdempotencyKey));
        var cancelRoundTrip = ProtocolJson.DeserializeRequest(ProtocolJson.SerializeRequest(cancel));
        var cancelPayload = ProtocolJson.ReadCancelPayload(cancelRoundTrip);
        Assert.Equal(RequestId, cancelPayload.TargetRequestId);
        Assert.Equal(IdempotencyKey, cancelPayload.TargetIdempotencyKey);

        var changes = new ProtocolRequest(
            ProtocolVersion.Current,
            RequestId,
            ProtocolClientKinds.Main,
            ClientInstanceId,
            SentAtUtc,
            2_000,
            ProtocolOperations.ChangesGetSince,
            ProtocolJson.CreateChangesPayload(0, 64));
        var changesPayload = ProtocolJson.ReadChangesPayload(
            ProtocolJson.DeserializeRequest(ProtocolJson.SerializeRequest(changes)));
        Assert.Equal(0, changesPayload.AfterRevision);
        Assert.Equal(64, changesPayload.MaxRevisions);

        var typedStatus = ProtocolStatusRequest.Create(
            ProtocolClientKinds.Main,
            ClientInstanceId,
            SentAtUtc,
            2_000,
            IdempotencyKey);
        typedStatus.Validate();
        Assert.Equal(
            IdempotencyKey,
            ProtocolJson.ReadStatusPayload(
                ProtocolJson.DeserializeRequest(ProtocolJson.SerializeStatusRequest(typedStatus))).IdempotencyKey);

        var typedCancel = ProtocolCancelRequest.Create(
            ProtocolClientKinds.Main,
            ClientInstanceId,
            SentAtUtc,
            RequestId,
            IdempotencyKey);
        typedCancel.Validate();
        Assert.Equal(RequestId, typedCancel.Payload.TargetRequestId);
    }

    [Fact]
    public void SuccessfulResponseAlwaysCarriesPayloadAndNullError()
    {
        var response = new ProtocolResponse(
            ProtocolVersion.Current,
            RequestId,
            ProtocolOperations.TaskCreate,
            AgentInstanceId,
            43,
            ok: true,
            replayed: false,
            ProtocolOutcomes.Changed,
            committedRevision: 43,
            ProtocolJson.ParseObject("{\"changed\":true}"),
            error: null);

        var wire = ProtocolJson.SerializeResponse(response);
        var text = Encoding.UTF8.GetString(wire);
        Assert.Contains("\"payload\":{\"changed\":true}", text, StringComparison.Ordinal);
        Assert.EndsWith("\"error\":null}", text, StringComparison.Ordinal);

        var roundTrip = Assert.IsType<ProtocolResponse>(ProtocolJson.Deserialize(wire));
        Assert.True(roundTrip.Ok);
        Assert.Null(roundTrip.Error);
        Assert.NotNull(roundTrip.Payload);
        Assert.Equal(43, roundTrip.CommittedRevision);
    }

    [Fact]
    public void ErrorResponseCarriesNullPayloadAndBoundedErrorObject()
    {
        var error = new ProtocolError(
            ProtocolErrorCodes.ExpectedRevisionMismatch,
            retryable: false,
            ProtocolJson.ParseObject("{\"currentRevision\":43}"),
            "请刷新后重试");
        var response = new ProtocolResponse(
            ProtocolVersion.Current,
            RequestId,
            ProtocolOperations.TaskRename,
            AgentInstanceId,
            43,
            ok: false,
            replayed: false,
            ProtocolOutcomes.Stale,
            committedRevision: null,
            payload: null,
            error);

        var roundTrip = ProtocolJson.DeserializeResponse(ProtocolJson.SerializeResponse(response));
        Assert.False(roundTrip.Ok);
        Assert.Null(roundTrip.Payload);
        Assert.NotNull(roundTrip.Error);
        Assert.Equal(ProtocolErrorCodes.ExpectedRevisionMismatch, roundTrip.Error!.Code);
        Assert.Equal("请刷新后重试", roundTrip.Error.HumanMessage);
        Assert.Equal(43, roundTrip.ServerRevision);
    }

    [Fact]
    public void StatusResponsePayloadHasOnlyBoundedReceiptMetadata()
    {
        var status = new ProtocolStatusResponsePayload(
            ProtocolReceiptStatuses.Committed,
            ProtocolOperations.TaskCreate,
            new string('a', 64),
            Changed: true,
            CommittedRevision: 43,
            ErrorCode: null,
            Retryable: false);
        var response = new ProtocolResponse(
            ProtocolVersion.Current,
            RequestId,
            ProtocolOperations.CommandStatus,
            AgentInstanceId,
            43,
            ok: true,
            replayed: true,
            ProtocolOutcomes.Replayed,
            committedRevision: 43,
            ProtocolJson.CreateStatusResponsePayload(status),
            error: null);

        var parsed = ProtocolJson.ReadStatusResponsePayload(
            ProtocolJson.DeserializeResponse(ProtocolJson.SerializeResponse(response)));
        Assert.Equal(ProtocolReceiptStatuses.Committed, parsed.Status);
        Assert.True(parsed.Changed);
        Assert.Equal(43, parsed.CommittedRevision);
        Assert.Null(parsed.ErrorCode);
    }

    [Fact]
    public void ResponsePayloadAndErrorAreMutuallyExclusive()
    {
        var payload = ProtocolJson.ParseObject("{}");
        var error = new ProtocolError(
            ProtocolErrorCodes.InvalidRequest,
            retryable: false,
            ProtocolJson.ParseObject("{}"));

        var both = new ProtocolResponse(
            ProtocolVersion.Current,
            RequestId,
            ProtocolOperations.TaskCreate,
            AgentInstanceId,
            0,
            ok: true,
            replayed: false,
            ProtocolOutcomes.Changed,
            committedRevision: 1,
            payload,
            error);
        var neither = new ProtocolResponse(
            ProtocolVersion.Current,
            RequestId,
            ProtocolOperations.TaskCreate,
            AgentInstanceId,
            0,
            ok: false,
            replayed: false,
            ProtocolOutcomes.Rejected,
            committedRevision: null,
            payload: null,
            error: null);

        Assert.Equal(ProtocolErrorCodes.InvalidRequest, Assert.Throws<ProtocolContractException>(both.Validate).Code);
        Assert.Equal(ProtocolErrorCodes.InvalidRequest, Assert.Throws<ProtocolContractException>(neither.Validate).Code);
    }

    [Fact]
    public void ResponseUnknownFieldsAreIgnoredButRequestUnknownFieldsAreRejected()
    {
        var response = new ProtocolResponse(
            ProtocolVersion.Current,
            RequestId,
            ProtocolOperations.TaskCreate,
            AgentInstanceId,
            0,
            ok: true,
            replayed: false,
            ProtocolOutcomes.NoOp,
            committedRevision: 0,
            ProtocolJson.ParseObject("{}"),
            error: null);
        var responseText = Encoding.UTF8.GetString(ProtocolJson.SerializeResponse(response));
        responseText = responseText.Insert(responseText.Length - 1, ",\"futureField\":1");
        Assert.NotNull(ProtocolJson.DeserializeResponse(Encoding.UTF8.GetBytes(responseText)));

        var requestText = "{\"protocolVersion\":\"1.0\",\"messageType\":\"request\",\"requestId\":\"019b2b36-4444-7abc-8def-0123456789ab\",\"clientKind\":\"main\",\"clientInstanceId\":\"019b2b36-4444-7abc-8def-0123456789ac\",\"sentAtUtc\":\"2026-08-30T00:00:00Z\",\"timeoutMs\":5000,\"operation\":\"command.task.create\",\"payload\":{\"title\":\"x\",\"timeSpec\":{\"type\":\"ANYTIME\",\"localDate\":\"2026-08-30\"}},\"idempotencyKey\":\"019b2b36-4444-7abc-8def-0123456789ae\",\"expectedRevision\":0,\"futureField\":1}";
        var exception = Assert.Throws<ProtocolContractException>(
            () => ProtocolJson.DeserializeRequest(Encoding.UTF8.GetBytes(requestText)));
        Assert.Equal(ProtocolErrorCodes.UnknownField, exception.Code);
    }

    [Fact]
    public void ResponseCommittedRevisionIsRequiredEvenWhenItIsNull()
    {
        var response = new ProtocolResponse(
            ProtocolVersion.Current,
            RequestId,
            ProtocolOperations.TaskCreate,
            AgentInstanceId,
            0,
            ok: true,
            replayed: false,
            ProtocolOutcomes.NoOp,
            committedRevision: 0,
            ProtocolJson.ParseObject("{}"),
            error: null);
        var text = Encoding.UTF8.GetString(ProtocolJson.SerializeResponse(response))
            .Replace(",\"committedRevision\":0", string.Empty, StringComparison.Ordinal);

        var exception = Assert.Throws<ProtocolContractException>(
            () => ProtocolJson.DeserializeResponse(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(ProtocolErrorCodes.MissingField, exception.Code);
    }

    [Fact]
    public void TypedDeserializersRequireTheirOwnMessageType()
    {
        AssertMessageTypeContract(
            ProtocolJson.SerializeRequest(new ProtocolRequest(
                ProtocolVersion.Current,
                RequestId,
                ProtocolClientKinds.Main,
                ClientInstanceId,
                SentAtUtc,
                5_000,
                ProtocolOperations.TaskCreate,
                ProtocolJson.ParseObject("{\"title\":\"x\",\"timeSpec\":{\"type\":\"ANYTIME\",\"localDate\":\"2026-08-30\"}}"),
                IdempotencyKey,
                0)),
            ProtocolMessageTypes.Request,
            wire => _ = ProtocolJson.DeserializeRequest(wire));

        AssertMessageTypeContract(
            ProtocolJson.SerializeResponse(new ProtocolResponse(
                ProtocolVersion.Current,
                RequestId,
                ProtocolOperations.TaskCreate,
                AgentInstanceId,
                0,
                ok: true,
                replayed: false,
                ProtocolOutcomes.NoOp,
                committedRevision: 0,
                ProtocolJson.ParseObject("{}"),
                error: null)),
            ProtocolMessageTypes.Response,
            wire => _ = ProtocolJson.DeserializeResponse(wire));

        AssertMessageTypeContract(
            ProtocolJson.SerializeEvent(new ProtocolEvent(
                ProtocolVersion.Current,
                Guid.Parse("019b2b36-4444-7abc-8def-0123456789af"),
                ProtocolEventTypes.ChangesAvailable,
                Guid.Parse(AgentInstanceId),
                SentAtUtc,
                1,
                ProtocolJson.ParseObject("{\"fromRevision\":0,\"toRevision\":1}"))),
            ProtocolMessageTypes.Event,
            wire => _ = ProtocolJson.DeserializeEvent(wire));
    }

    [Fact]
    public void EventRoundTripsOnlyAfterCommitMetadata()
    {
        var @event = new ProtocolEvent(
            ProtocolVersion.Current,
            Guid.Parse("019b2b36-4444-7abc-8def-0123456789af"),
            ProtocolEventTypes.ChangesAvailable,
            Guid.Parse(AgentInstanceId),
            SentAtUtc,
            43,
            ProtocolJson.ParseObject("{\"toRevision\":43,\"fromRevision\":42}"));

        var roundTrip = ProtocolJson.DeserializeEvent(ProtocolJson.SerializeEvent(@event));
        Assert.Equal(@event.EventId, roundTrip.EventId);
        Assert.Equal(@event.AgentInstanceId, roundTrip.AgentInstanceId);
        Assert.Equal(43, roundTrip.Revision);
        Assert.Equal(42, roundTrip.Payload.GetProperty("fromRevision").GetInt64());
    }

    [Fact]
    public void ResponseOutcomeMatrixRejectsContradictorySuccessMetadata()
    {
        var missingCommit = new ProtocolResponse(
            ProtocolVersion.Current,
            RequestId,
            ProtocolOperations.TaskCreate,
            AgentInstanceId,
            0,
            ok: true,
            replayed: false,
            ProtocolOutcomes.Changed,
            committedRevision: null,
            ProtocolJson.ParseObject("{}"),
            error: null);
        var mismatchedReplay = new ProtocolResponse(
            ProtocolVersion.Current,
            RequestId,
            ProtocolOperations.TaskCreate,
            AgentInstanceId,
            1,
            ok: true,
            replayed: false,
            ProtocolOutcomes.Replayed,
            committedRevision: 1,
            ProtocolJson.ParseObject("{}"),
            error: null);
        var ahead = new ProtocolResponse(
            ProtocolVersion.Current,
            RequestId,
            ProtocolOperations.TaskCreate,
            AgentInstanceId,
            0,
            ok: true,
            replayed: false,
            ProtocolOutcomes.Changed,
            committedRevision: 1,
            ProtocolJson.ParseObject("{}"),
            error: null);

        Assert.Equal(
            ProtocolErrorCodes.InvalidRequest,
            Assert.Throws<ProtocolContractException>(missingCommit.Validate).Code);
        Assert.Equal(
            ProtocolErrorCodes.InvalidRequest,
            Assert.Throws<ProtocolContractException>(mismatchedReplay.Validate).Code);
        Assert.Equal(
            ProtocolErrorCodes.InvalidRequest,
            Assert.Throws<ProtocolContractException>(ahead.Validate).Code);
    }

    [Fact]
    public void EventRevisionRangeMustBeOrderedAndEndAtEventRevision()
    {
        var reversed = new ProtocolEvent(
            ProtocolVersion.Current,
            Guid.Parse("019b2b36-4444-7abc-8def-0123456789af"),
            ProtocolEventTypes.ChangesAvailable,
            Guid.Parse(AgentInstanceId),
            SentAtUtc,
            4,
            ProtocolJson.ParseObject("{\"fromRevision\":5,\"toRevision\":4}"));
        var mismatchedEnd = new ProtocolEvent(
            ProtocolVersion.Current,
            Guid.Parse("019b2b36-4444-7abc-8def-0123456789af"),
            ProtocolEventTypes.ChangesAvailable,
            Guid.Parse(AgentInstanceId),
            SentAtUtc,
            5,
            ProtocolJson.ParseObject("{\"fromRevision\":4,\"toRevision\":6}"));

        Assert.Equal(
            ProtocolErrorCodes.InvalidRequest,
            Assert.Throws<ProtocolContractException>(reversed.Validate).Code);
        Assert.Equal(
            ProtocolErrorCodes.InvalidRequest,
            Assert.Throws<ProtocolContractException>(mismatchedEnd.Validate).Code);
    }

    [Fact]
    public void StatusPayloadMatrixRejectsCommittedWithoutRevision()
    {
        var status = new ProtocolStatusResponsePayload(
            ProtocolReceiptStatuses.Committed,
            ProtocolOperations.TaskCreate,
            new string('a', 64),
            Changed: true,
            CommittedRevision: null,
            ErrorCode: null,
            Retryable: false);

        Assert.Equal(
            ProtocolErrorCodes.InvalidRequest,
            Assert.Throws<ProtocolContractException>(status.Validate).Code);
    }

    [Fact]
    public void ErrorDetailsRejectSensitiveFieldsAndUnboundedContent()
    {
        var sensitive = new ProtocolError(
            ProtocolErrorCodes.InvalidRequest,
            retryable: false,
            ProtocolJson.ParseObject("{\"title\":\"不要回显\"}"));
        var large = new ProtocolError(
            ProtocolErrorCodes.InvalidRequest,
            retryable: false,
            ProtocolJson.ParseObject($"{{\"reason\":\"{new string('x', 8_200)}\"}}"));

        Assert.Equal(
            ProtocolErrorCodes.InvalidRequest,
            Assert.Throws<ProtocolContractException>(sensitive.Validate).Code);
        Assert.Equal(
            ProtocolErrorCodes.InvalidRequest,
            Assert.Throws<ProtocolContractException>(large.Validate).Code);
    }

    private static void AssertMessageTypeContract(
        byte[] validWire,
        string expectedMessageType,
        Action<byte[]> deserialize)
    {
        var validText = Encoding.UTF8.GetString(validWire);
        var messageType = $"\"messageType\":\"{expectedMessageType}\"";

        var missing = validText.Replace($",{messageType}", string.Empty, StringComparison.Ordinal);
        var missingException = Assert.Throws<ProtocolContractException>(
            () => deserialize(Encoding.UTF8.GetBytes(missing)));
        Assert.Equal(ProtocolErrorCodes.MissingField, missingException.Code);

        var wrong = validText.Replace(messageType, "\"messageType\":\"wrong\"", StringComparison.Ordinal);
        var wrongException = Assert.Throws<ProtocolContractException>(
            () => deserialize(Encoding.UTF8.GetBytes(wrong)));
        Assert.Equal(ProtocolErrorCodes.InvalidRequest, wrongException.Code);
    }
}
