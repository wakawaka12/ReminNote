using System.Buffers.Binary;
using ReminNote.Core.Transport;

namespace ReminNote.Tests;

public sealed class LengthPrefixedFrameCodecTests
{
    [Fact]
    public async Task WriteUsesFourByteUnsignedBigEndianLength()
    {
        var stream = new MemoryStream();
        var codec = new LengthPrefixedFrameCodec(TransportTestLimits.Create());
        var payload = new byte[] { 0x7B, 0x22, 0x7D };

        await codec.WriteAsync(
            stream,
            payload,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            new byte[] { 0, 0, 0, 3, 0x7B, 0x22, 0x7D },
            stream.ToArray());
    }

    [Fact]
    public async Task ReadHandlesPartialPrefixAndBodyAndMergedFrames()
    {
        var first = new byte[] { 0x01, 0x02, 0x03 };
        var second = new byte[] { 0xAA, 0xBB };
        var stream = new ChunkedReadStream(
            Combine(Encode(first), Encode(second)),
            maximumReadSize: 1);
        var codec = new LengthPrefixedFrameCodec(TransportTestLimits.Create());

        var firstRead = await codec.ReadAsync(
            stream,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        var secondRead = await codec.ReadAsync(
            stream,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(first, firstRead);
        Assert.Equal(second, secondRead);
    }

    [Fact]
    public async Task CleanEofReturnsNoFrame()
    {
        var codec = new LengthPrefixedFrameCodec(TransportTestLimits.Create());

        var frame = await codec.ReadAsync(
            new MemoryStream(),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Null(frame);
    }

    [Theory]
    [MemberData(nameof(TruncatedFrames))]
    public async Task TruncatedPrefixOrBodyFailsClosed(byte[] bytes)
    {
        var codec = new LengthPrefixedFrameCodec(TransportTestLimits.Create());

        var exception = await Assert.ThrowsAsync<TransportFailureException>(
            () => codec.ReadAsync(
                new MemoryStream(bytes),
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(TransportFailureKind.InvalidFrame, exception.Kind);
        Assert.True(exception.ConnectionMustClose);
    }

    public static IEnumerable<object[]> TruncatedFrames()
    {
        yield return [new byte[] { 0, 0 }];
        yield return [new byte[] { 0, 0, 0, 3, 0x01, 0x02 }];
    }

    [Fact]
    public async Task ZeroLengthFrameFailsClosedWithoutAllocatingPayload()
    {
        var codec = new LengthPrefixedFrameCodec(TransportTestLimits.Create());

        var exception = await Assert.ThrowsAsync<TransportFailureException>(
            () => codec.ReadAsync(
                new MemoryStream(new byte[] { 0, 0, 0, 0 }),
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(TransportFailureKind.InvalidFrame, exception.Kind);
        Assert.True(exception.ConnectionMustClose);
    }

    [Fact]
    public async Task FrameAboveConfiguredLimitIsRejectedBeforePayloadAllocation()
    {
        var limits = new TransportLimits(
            new TransportTestLimitSource
            {
                MaxFrameBytes = 4,
                MaxCommandPayloadBytes = 4,
                MaxEventPayloadBytes = 4,
                MaxErrorDetailsBytes = 4,
                MaxSuccessPayloadBytes = 4
            });
        var codec = new LengthPrefixedFrameCodec(limits);
        var oversizedPrefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(oversizedPrefix, 5);

        var exception = await Assert.ThrowsAsync<TransportFailureException>(
            () => codec.ReadAsync(
                new MemoryStream(oversizedPrefix),
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(TransportFailureKind.FrameTooLarge, exception.Kind);
        Assert.True(exception.ConnectionMustClose);
    }

    [Fact]
    public void PayloadValidationHonorsTheCommandBudget()
    {
        var codec = new LengthPrefixedFrameCodec(TransportTestLimits.Create());
        var commandLimit = new TransportTestLimitSource().MaxCommandPayloadBytes;

        codec.ValidatePayloadSize(
            new byte[commandLimit],
            TransportPayloadKind.Command);

        var exception = Assert.Throws<TransportFailureException>(
            () => codec.ValidatePayloadSize(
                new byte[commandLimit + 1],
                TransportPayloadKind.Command));

        Assert.Equal(TransportFailureKind.FrameTooLarge, exception.Kind);
        Assert.True(exception.ConnectionMustClose);
    }

    [Fact]
    public async Task DeadlineCancelsAStalledReadAsTransportDeadline()
    {
        var codec = new LengthPrefixedFrameCodec(TransportTestLimits.Create());

        var exception = await Assert.ThrowsAsync<TransportDeadlineExceededException>(
            () => codec.ReadAsync(
                new BlockingReadStream(),
                TimeSpan.FromMilliseconds(25),
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(TransportFailureKind.DeadlineExpired, exception.Kind);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationAndIsNotRelabeledAsDeadline()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var codec = new LengthPrefixedFrameCodec(TransportTestLimits.Create());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => codec.ReadAsync(
                new BlockingReadStream(),
                TimeSpan.FromSeconds(1),
                cancellation.Token).AsTask());
    }

    private static byte[] Encode(byte[] payload)
    {
        var result = new byte[sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)payload.Length);
        payload.CopyTo(result, sizeof(uint));
        return result;
    }

    private static byte[] Combine(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    private sealed class ChunkedReadStream(byte[] content, int maximumReadSize) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var count = Math.Min(buffer.Length, maximumReadSize);
            return base.ReadAsync(buffer[..count], cancellationToken);
        }
    }

    private sealed class BlockingReadStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
