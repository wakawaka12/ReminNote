using System.Buffers.Binary;
using ReminNote.Core.Transport;

namespace ReminNote.Tests;

public sealed class TransportWriteGateTests
{
    [Fact]
    public async Task ConcurrentCompleteFrameWritesRemainSerialized()
    {
        var firstPayload = new byte[] { 0x01, 0x02, 0x03 };
        var secondPayload = new byte[] { 0xAA, 0xBB };
        var stream = new BlockingFirstWriteStream();
        var codec = new LengthPrefixedFrameCodec(TransportTestLimits.Create());
        await using var writeGate = new TransportWriteGate();

        var secondEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = writeGate.WriteAsync(() => WriteFrameAsync(
            codec,
            stream,
            firstPayload), TestContext.Current.CancellationToken).AsTask();

        await stream.FirstWriteStarted.WaitAsync(TestContext.Current.CancellationToken);

        var second = writeGate.WriteAsync(async () =>
        {
            secondEntered.TrySetResult(null);
            await WriteFrameAsync(
                codec,
                stream,
                secondPayload).ConfigureAwait(false);
        }, TestContext.Current.CancellationToken).AsTask();

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => secondEntered.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(100),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            stream.ReleaseFirstWrite();
        }

        await Task.WhenAll(first, second);

        Assert.Equal(
            Combine(Encode(firstPayload), Encode(secondPayload)),
            stream.ToArray());
    }

    private static ValueTask WriteFrameAsync(
        LengthPrefixedFrameCodec codec,
        Stream stream,
        byte[] payload)
    {
        return codec.WriteAsync(
            stream,
            payload,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
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

    private sealed class BlockingFirstWriteStream : MemoryStream
    {
        private readonly TaskCompletionSource<object?> firstWriteStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> releaseFirstWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int writeCount;

        public Task FirstWriteStarted => firstWriteStarted.Task;

        public void ReleaseFirstWrite()
        {
            releaseFirstWrite.TrySetResult(null);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var write = base.WriteAsync(buffer, cancellationToken);
            if (Interlocked.Increment(ref writeCount) != 1)
            {
                return write;
            }

            return BlockFirstWriteAsync(write, cancellationToken);
        }

        private async ValueTask BlockFirstWriteAsync(
            ValueTask write,
            CancellationToken cancellationToken)
        {
            firstWriteStarted.TrySetResult(null);
            await write.ConfigureAwait(false);
            await releaseFirstWrite.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
