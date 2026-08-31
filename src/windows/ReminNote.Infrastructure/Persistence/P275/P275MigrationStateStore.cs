using System.Security;
using System.Text.Json;

namespace ReminNote.Infrastructure.Persistence.P275;

public enum P275MarkerReadStatus
{
    Missing,
    Valid,
    Invalid
}

public sealed record P275MigrationStateReadResult(
    P275MarkerReadStatus Status,
    P275MigrationStateMarker? Marker,
    string? FailureCode)
{
    public bool IsUsable => Status == P275MarkerReadStatus.Valid && Marker is not null;

    public static P275MigrationStateReadResult Missing() => new(
        P275MarkerReadStatus.Missing,
        Marker: null,
        P275MigrationFailureCodes.RecoveryRequired);

    public static P275MigrationStateReadResult Invalid() => new(
        P275MarkerReadStatus.Invalid,
        Marker: null,
        P275MigrationFailureCodes.RecoveryRequired);

    public static P275MigrationStateReadResult Valid(P275MigrationStateMarker marker) => new(
        P275MarkerReadStatus.Valid,
        marker,
        marker.FailureCode);
}

public sealed class P275MigrationStatePersistenceException : IOException
{
    public P275MigrationStatePersistenceException(
        string failureCode,
        Exception? innerException = null)
        : base(failureCode, innerException)
    {
        FailureCode = failureCode;
    }

    public string FailureCode { get; }
}

/// <summary>
/// Reads and atomically publishes the profile-local migration marker. The
/// reader is deliberately bounded and the writer never deletes the previous
/// marker before the replacement is ready.
/// </summary>
public sealed class P275MigrationStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        MaxDepth = 16
    };

    private readonly P275ProfileLayout layout;

    public P275MigrationStateStore(P275ProfileLayout layout)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public static string MarkerRelativePath => "runtime/migration-state.json";

    public async ValueTask WriteAsync(
        P275MigrationStateMarker marker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marker);
        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(marker.ToDocument(), SerializerOptions);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            JsonException or
            NotSupportedException)
        {
            throw new P275MigrationStatePersistenceException(
                P275MigrationFailureCodes.StateUnwritable,
                exception);
        }

        if (payload.Length is < 1 or > P275MigrationContract.MaxMarkerBytes)
        {
            throw new P275MigrationStatePersistenceException(P275MigrationFailureCodes.StateUnwritable);
        }

        string temporaryPath = string.Empty;
        try
        {
            layout.EnsureRuntimeDirectoryForWrite();
            EnsureMarkerTargetIsNotReparsePoint();
            temporaryPath = Path.Combine(
                layout.RuntimeDirectory,
                $"migration-state.{Guid.CreateVersion7():D}.tmp");

            await using (var stream = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    Options = FileOptions.WriteThrough | FileOptions.SequentialScan,
                    PreallocationSize = payload.Length
                }))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            ReplaceAtomically(temporaryPath, layout.MigrationStatePath);
            temporaryPath = string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (P275MigrationStatePersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            NotSupportedException)
        {
            throw new P275MigrationStatePersistenceException(
                P275MigrationFailureCodes.StateUnwritable,
                exception);
        }
        finally
        {
            if (temporaryPath.Length != 0)
            {
                TryDeleteOwnedTemporaryFile(temporaryPath);
            }
        }
    }

    public async ValueTask<P275MigrationStateReadResult> ReadAsync(
        string expectedProfileScope,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ReminNote.Core.Protocol.ProtocolProfileScope.Validate(expectedProfileScope);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return P275MigrationStateReadResult.Invalid();
        }

        if (!Directory.Exists(layout.ProfileRoot) ||
            !Directory.Exists(layout.RuntimeDirectory))
        {
            return P275MigrationStateReadResult.Missing();
        }

        try
        {
            layout.EnsureProfileRootForRead();
            if (!Directory.Exists(layout.RuntimeDirectory))
            {
                return P275MigrationStateReadResult.Missing();
            }

            EnsureNotReparse(layout.RuntimeDirectory);
            if (!File.Exists(layout.MigrationStatePath))
            {
                return P275MigrationStateReadResult.Missing();
            }

            EnsureNotReparse(layout.MigrationStatePath);
            var payload = await ReadBoundedAsync(
                    layout.MigrationStatePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (payload is null)
            {
                return P275MigrationStateReadResult.Invalid();
            }

            var document = DeserializeBounded(payload);
            var marker = P275MigrationStateMarker.FromDocument(document);
            if (!string.Equals(marker.ProfileScope, expectedProfileScope, StringComparison.Ordinal))
            {
                return P275MigrationStateReadResult.Invalid();
            }

            return P275MigrationStateReadResult.Valid(marker);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            JsonException or
            FormatException or
            NotSupportedException or
            OverflowException or
            ArgumentException or
            InvalidOperationException)
        {
            return P275MigrationStateReadResult.Invalid();
        }
    }

    private static async ValueTask<byte[]?> ReadBoundedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[P275MigrationContract.MaxMarkerBytes + 1];
        var total = 0;
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                BufferSize = 4096,
                Options = FileOptions.SequentialScan
            });

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(
                    buffer.AsMemory(total, buffer.Length - total),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total == 0 || total > P275MigrationContract.MaxMarkerBytes)
        {
            return null;
        }

        return buffer.AsSpan(0, total).ToArray();
    }

    private static P275MarkerDocument DeserializeBounded(byte[] payload)
    {
        using var document = JsonDocument.Parse(
            payload,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The migration marker root must be an object.");
        }

        var expectedProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "contractVersion",
            "state",
            "runId",
            "profileScope",
            "sourceSchema",
            "targetSchema",
            "backupArtifact",
            "backupSha256",
            "candidateArtifact",
            "startedAtUtc",
            "updatedAtUtc",
            "failureCode",
            "retryable",
            "nextAction",
            "lastKnownGoodSchema",
            "lastAgentInstanceId"
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !expectedProperties.Remove(property.Name))
            {
                throw new FormatException("The migration marker has an unknown or duplicate field.");
            }
        }

        if (expectedProperties.Count != 0)
        {
            throw new FormatException("The migration marker is missing a required field.");
        }

        return JsonSerializer.Deserialize<P275MarkerDocument>(payload, SerializerOptions)
            ?? throw new FormatException("The migration marker is empty.");
    }

    private void EnsureMarkerTargetIsNotReparsePoint()
    {
        if (File.Exists(layout.MigrationStatePath))
        {
            EnsureNotReparse(layout.MigrationStatePath);
        }
    }

    private static void ReplaceAtomically(string temporaryPath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            EnsureNotReparse(targetPath);
            File.Replace(
                temporaryPath,
                targetPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            return;
        }

        try
        {
            File.Move(temporaryPath, targetPath);
        }
        catch (FileNotFoundException) when (!File.Exists(targetPath))
        {
            throw;
        }
    }

    private static void EnsureNotReparse(string path)
    {
        FileSystemInfo info = File.Exists(path)
            ? new FileInfo(path)
            : new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new P275MigrationStatePersistenceException(P275MigrationFailureCodes.StateUnwritable);
        }
    }

    private static void TryDeleteOwnedTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SecurityException)
        {
            // The marker write already failed. Do not replace its stable code
            // with a cleanup diagnostic or expose the path to a caller.
        }
    }
}
