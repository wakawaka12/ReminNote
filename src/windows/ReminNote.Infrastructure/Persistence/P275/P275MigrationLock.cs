using System.Diagnostics;
using System.Text;

namespace ReminNote.Infrastructure.Persistence.P275;

/// <summary>
/// A profile-scoped lock backed by an exclusively opened file. The handle is
/// held for the whole migration/promotion attempt, so another process cannot
/// enter the same profile while the candidate is being prepared or promoted.
/// Waiting is deliberately bounded; a missing lease never becomes permission
/// to touch the Active DB.
/// </summary>
public sealed class P275FileMigrationLockProvider : IP275MigrationLockProvider
{
    public async ValueTask<IP275MigrationLockLease?> AcquireAsync(
        P275MigrationLockRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        request.Paths.EnsureRuntimeDirectories();
        if (P275FileSafety.PathExists(request.Paths.MigrationLockPath) &&
            !P275FileSafety.IsRegularFile(request.Paths.MigrationLockPath))
        {
            return null;
        }

        var startedAt = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(startedAt) < request.Timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    request.Paths.MigrationLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 128,
                    options: FileOptions.Asynchronous | FileOptions.WriteThrough);
                try
                {
                    var runIdBytes = Encoding.UTF8.GetBytes(request.RunId);
                    stream.SetLength(0);
                    await stream.WriteAsync(runIdBytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return new P275FileMigrationLockLease(stream);
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (IOException)
            {
                // FileShare.None is the cross-process ownership boundary. A
                // short retry avoids a busy spin while preserving the bound.
            }
            catch (UnauthorizedAccessException)
            {
                // A denied lock is indistinguishable from a competing holder
                // to this seam. The bounded null result is fail-closed.
            }

            var remaining = request.Timeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(50)
                        ? remaining
                        : TimeSpan.FromMilliseconds(50),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    private static void ValidateRequest(P275MigrationLockRequest request)
    {
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The migration lock wait must be bounded to 30 seconds.");
        }

        if (!Guid.TryParseExact(request.RunId, "D", out var parsed) ||
            !string.Equals(parsed.ToString("D"), request.RunId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The migration lock RunId must be a lower-case UUID.", nameof(request));
        }
    }

    private sealed class P275FileMigrationLockLease : IP275MigrationLockLease
    {
        private readonly FileStream stream;
        private int disposed;

        public P275FileMigrationLockLease(FileStream stream)
        {
            this.stream = stream;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
