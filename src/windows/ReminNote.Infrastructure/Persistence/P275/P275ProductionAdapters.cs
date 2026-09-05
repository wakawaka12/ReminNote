using System.Data;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReminNote.Core.Protocol;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.Backup;

namespace ReminNote.Infrastructure.Persistence.P275;

/// <summary>
/// Carries a stable failure code across a provider boundary without exposing
/// provider exception text to the migration state machine.
/// </summary>
public sealed class P275MigrationOperationException : IOException
{
    public P275MigrationOperationException(
        string failureCode,
        Exception? innerException = null)
        : base(failureCode, innerException)
    {
        if (!P275MigrationFailureCodes.IsStable(failureCode))
        {
            throw new ArgumentException("The failure code is not stable.", nameof(failureCode));
        }

        FailureCode = failureCode;
    }

    public string FailureCode { get; }
}

/// <summary>
/// The canonical source reader used by the integrated Agent composition. It
/// only opens the fixed Active path read-only and captures preservation facts
/// before the backup provider is allowed to stage a Candidate.
/// </summary>
public sealed class P275ActiveSourceReader : IP275ActiveSourceReader
{
    private static readonly (string Table, string[] Keys)[] PreservationTables =
    [
        ("tasks", ["id"]),
        ("task_history", ["id"]),
        ("app_settings", ["id"])
    ];

    public async ValueTask<P275SourceInventory> ReadAsync(
        P275ProfilePaths paths,
        string profileScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ProtocolProfileScope.Validate(profileScope);

        var databasePath = paths.ActiveDatabasePath;
        if (!File.Exists(databasePath))
        {
            if (Directory.Exists(databasePath))
            {
                throw new IOException("The Active database path is a directory.");
            }

            return new P275SourceInventory(
                exists: false,
                Array.Empty<string>(),
                new P275SourceFingerprint("empty"));
        }

        if (!P275FileSafety.IsRegularFile(databasePath))
        {
            throw new IOException("The Active database is not a regular file.");
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true
        }.ToString();

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureReadOnlyAsync(connection, cancellationToken).ConfigureAwait(false);
            var migrations = await ReadMigrationHistoryAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            await VerifyIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
            var baseline = await CaptureBaselineAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            var fingerprint = await P275SourceFingerprintCodec
                .CaptureAsync(databasePath, migrations, cancellationToken)
                .ConfigureAwait(false);
            return new P275SourceInventory(
                exists: true,
                migrations,
                fingerprint,
                baseline);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new IOException("The Active source could not be inspected.", exception);
        }
    }

    private static async ValueTask ConfigureReadOnlyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
                connection,
                "PRAGMA foreign_keys = ON; PRAGMA query_only = ON;",
                cancellationToken)
            .ConfigureAwait(false);

        var foreignKeys = await ExecuteScalarLongAsync(
                connection,
                "PRAGMA foreign_keys;",
                cancellationToken)
            .ConfigureAwait(false);
        var queryOnly = await ExecuteScalarLongAsync(
                connection,
                "PRAGMA query_only;",
                cancellationToken)
            .ConfigureAwait(false);
        if (foreignKeys != 1 || queryOnly != 1)
        {
            throw new InvalidOperationException("The source read contract was not enabled.");
        }
    }

    private static async ValueTask<IReadOnlyList<string>> ReadMigrationHistoryAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT MigrationId FROM \"__EFMigrationsHistory\" ORDER BY rowid;";
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0) || values.Count >= SafetyBackupContract.MaxSchemaEntries)
                {
                    throw new FormatException("The migration history is invalid.");
                }

                var value = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(value) ||
                    Encoding.UTF8.GetByteCount(value) > SafetyBackupContract.MaxMigrationIdBytes ||
                    value.Any(character =>
                        character is not (>= 'a' and <= 'z') and
                            not (>= 'A' and <= 'Z') and
                            not (>= '0' and <= '9') and
                            not ('_' or '-' or '.')) ||
                    !seen.Add(value))
                {
                    throw new FormatException("The migration history is invalid.");
                }

                values.Add(value);
            }

            return values;
        }
        catch (SqliteException exception) when (
            exception.SqliteErrorCode == 1 &&
            exception.Message.Contains("__EFMigrationsHistory", StringComparison.Ordinal))
        {
            // A valid empty SQLite file has no EF history yet. The approved
            // migration plan decides whether that source is compatible.
            return Array.Empty<string>();
        }
    }

    private static async ValueTask VerifyIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var value = await integrity.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                    "ok",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The source integrity check failed.");
            }
        }

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The source foreign key check failed.");
        }
    }

    private static async ValueTask<P275VerificationBaseline> CaptureBaselineAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var keyTables = new List<P275KeyTableExpectation>();
        foreach (var (table, keys) in PreservationTables)
        {
            if (!await TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            keyTables.Add(
                await P275CandidateVerifier.CaptureKeyTableExpectationAsync(
                        connection,
                        table,
                        keys,
                        cancellationToken)
                    .ConfigureAwait(false));
        }

        return new P275VerificationBaseline(keyTables);
    }

    private static async ValueTask<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async ValueTask<int> ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Adapts the P2.75-01 backup result to the runner's capability type. An
/// artifact becomes a capability only when both its SQLite/hash validation and
/// its verified manifest have completed.
/// </summary>
public sealed class P275SafetyBackupProvider : IP275SafetyBackupProvider
{
    private readonly SafetyBackupService service;

    public P275SafetyBackupProvider(SafetyBackupService? service = null)
    {
        this.service = service ?? new SafetyBackupService();
    }

    public async ValueTask<P275VerifiedSafetyBackup> CreateVerifiedBackupAsync(
        P275SafetyBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParseExact(request.RunId, "D", out var runId) ||
            !string.Equals(runId.ToString("D"), request.RunId, StringComparison.Ordinal))
        {
            throw new P275MigrationOperationException(P275MigrationFailureCodes.ArgumentsInvalid);
        }

        if (!request.Source.Exists)
        {
            // There is no Active generation to copy on a first Portable/
            // UserData launch. Preserve the same explicit empty-source
            // capability used by the runner's isolated tests; migration still
            // occurs only on a Candidate and promotion remains atomic.
            return new P275VerifiedSafetyBackup(
                "empty-source-" + request.RunId,
                artifactPath: null,
                sourceIsEmpty: true,
                request.Source.AppliedMigrations,
                request.Source.Fingerprint,
                byteLength: 0,
                sha256: string.Empty);
        }

        var result = await service.CreateAsync(
                new SafetyBackupRequest(
                    request.Paths.DataRoot,
                    request.Paths.ProfileRoot,
                    request.Paths.ActiveDatabasePath,
                    request.ProfileScope,
                    request.Plan.ApprovedTargetMigrations,
                    runId,
                    DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsVerified || result.Manifest is null || result.Artifact is null ||
            result.ByteLength is not { } byteLength || result.Sha256 is null)
        {
            throw new P275MigrationOperationException(
                result.FailureCode ?? P275MigrationFailureCodes.BackupFailed);
        }

        var manifest = result.Manifest;
        if (!string.Equals(manifest.RunId, request.RunId, StringComparison.Ordinal) ||
            !string.Equals(manifest.ProfileScope, request.ProfileScope, StringComparison.Ordinal) ||
            !manifest.SourceSchema.SequenceEqual(request.Source.AppliedMigrations, StringComparer.Ordinal) ||
            !manifest.TargetSchema.SequenceEqual(request.Plan.ApprovedTargetMigrations, StringComparer.Ordinal) ||
            !string.Equals(manifest.Result, SafetyBackupContract.VerifiedResult, StringComparison.Ordinal) ||
            manifest.ByteLength != byteLength ||
            !string.Equals(manifest.Sha256, result.Sha256, StringComparison.Ordinal))
        {
            throw new P275MigrationOperationException(P275MigrationFailureCodes.BackupUnreadable);
        }

        var artifactPath = Path.Combine(request.Paths.BackupsDirectory, manifest.Artifact);
        return new P275VerifiedSafetyBackup(
            manifest.Artifact,
            artifactPath,
            sourceIsEmpty: false,
            manifest.SourceSchema,
            new P275SourceFingerprint(P275SourceFingerprintCodec.Encode(manifest.SourceFingerprint)),
            byteLength,
            manifest.Sha256);
    }
}

/// <summary>
/// Supplies one approved target schema allow-list from the compiled EF
/// migration assembly. The template database is in-memory and never the
/// profile's business database; preservation expectations come from the
/// source reader rather than from the post-migration Candidate.
/// </summary>
public sealed class P275MigrationVerificationExpectationsFactory
{
    public static async ValueTask<P275CandidateVerificationExpectations> CreateAsync(
        P275MigrationPlan plan,
        P275VerificationBaseline baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(baseline);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var options = ReminNoteDbContext.CreateOptions(connection);
        await using var context = new ReminNoteDbContext(options);
        await context.Database.MigrateAsync(plan.TargetMigrationId, cancellationToken)
            .ConfigureAwait(false);

        var schema = new List<P275SchemaObjectExpectation>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT name, type, sql
                FROM sqlite_master
                WHERE name NOT LIKE 'sqlite_%'
                  AND type IN ('table', 'index', 'trigger', 'view')
                ORDER BY type, name;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(2))
                {
                    throw new InvalidOperationException("The approved schema contains a null SQL definition.");
                }

                schema.Add(P275SchemaObjectExpectation.FromSql(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)));
            }
        }

        return new P275CandidateVerificationExpectations(
            plan.ApprovedTargetMigrations,
            new P275SchemaExpectation(schema),
            baseline.KeyTables,
            requireWal: true);
    }
}

/// <summary>
/// Bridges the runner request to the 03 verifier while preserving the source
/// key facts and allowing the same strict verifier to inspect Active only for
/// the already-ready/post-promote phase.
/// </summary>
public sealed class P275ProductionCandidateVerifier : IP275CandidateVerifier
{
    public async ValueTask<P275VerificationResult> VerifyAsync(
        P275VerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var paths = request.Paths ?? request.Candidate?.Paths;
        if (paths is null || !Guid.TryParseExact(request.RunId, "D", out var runId))
        {
            return P275VerificationResult.Invalid(P275MigrationFailureCodes.PathInvalid);
        }

        var layout = new P275ProfileLayout(paths.ProfileRoot);
        var expectations = await P275MigrationVerificationExpectationsFactory.CreateAsync(
                request.Plan,
                request.Baseline ?? P275VerificationBaseline.Empty,
                cancellationToken)
            .ConfigureAwait(false);
        // Candidate verification resolves the fixed staging path from the
        // run id. DatabasePath is reserved for the Active post-promote and
        // already-ready checks; passing a staging path here would make the
        // verifier's Active-path guard reject every normal migration.
        var databasePath = request.Phase is P275VerificationPhase.Candidate
            ? null
            : request.DatabasePath;
        var candidateRequest = new P275CandidateVerificationRequest(
            layout,
            runId,
            expectations,
            databasePath);
        var result = await P275CandidateVerifier.VerifyAsync(
                candidateRequest,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Passed
            ? P275VerificationResult.Valid()
            : P275VerificationResult.Invalid(
                result.FailureCode ?? P275MigrationFailureCodes.VerifyFailed);
    }
}

/// <summary>
/// Publishes runner snapshots as the bounded, atomic 03 marker. A previous
/// marker's start time and last-known-good schema are retained across updates.
/// </summary>
public sealed class P275ProductionMigrationStatePort : IP275MigrationStatePort
{
    private readonly P275MigrationStateStore store;

    public P275ProductionMigrationStatePort(P275ProfileLayout layout)
    {
        store = new P275MigrationStateStore(layout ?? throw new ArgumentNullException(nameof(layout)));
    }

    public async ValueTask RecordAsync(
        P275MigrationStateSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Guid.TryParseExact(snapshot.RunId, "D", out var runId) ||
            !string.Equals(runId.ToString("D"), snapshot.RunId, StringComparison.Ordinal))
        {
            throw new P275MigrationStatePersistenceException(P275MigrationFailureCodes.StateUnwritable);
        }

        var previous = await store.ReadAsync(snapshot.ProfileScope, cancellationToken)
            .ConfigureAwait(false);
        var startedAt = snapshot.StartedAtUtc ??
            (previous.IsUsable && previous.Marker!.RunId == runId
                ? previous.Marker.StartedAtUtc
                : DateTimeOffset.UtcNow);
        var updatedAt = snapshot.UpdatedAtUtc ?? DateTimeOffset.UtcNow;
        if (updatedAt < startedAt)
        {
            updatedAt = startedAt;
        }

        var marker = new P275MigrationStateMarker(
            snapshot.State,
            runId,
            snapshot.ProfileScope,
            snapshot.SourceSchema,
            snapshot.TargetSchema,
            snapshot.BackupArtifact,
            snapshot.BackupSha256,
            snapshot.CandidateArtifact,
            startedAt,
            updatedAt,
            snapshot.FailureCode,
            snapshot.Retryable,
            snapshot.NextAction,
            snapshot.State == P275MigrationState.Ready
                ? snapshot.TargetSchema
                : previous.IsUsable && previous.Marker!.RunId == runId
                ? previous.Marker.LastKnownGoodSchema
                : Array.Empty<string>(),
            snapshot.LastAgentInstanceId);
        await store.WriteAsync(marker, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<P275MigrationStateReadResult> ReadAsync(
        string expectedProfileScope,
        CancellationToken cancellationToken = default) =>
        store.ReadAsync(expectedProfileScope, cancellationToken);
}

/// <summary>
/// Stable source fingerprint codec shared by the source reader and the 01
/// manifest adapter. It includes the main file, migration history and all
/// supported SQLite sidecars without carrying paths or user data.
/// </summary>
public static class P275SourceFingerprintCodec
{
    public static string Encode(SafetyBackupSourceFingerprint fingerprint) =>
        string.Join(
            '|',
            "v1",
            $"length={fingerprint.ByteLength}",
            $"sha256={fingerprint.Sha256}",
            $"schema={fingerprint.SchemaHistorySha256}",
            $"wal={EncodeSidecar(fingerprint.Wal)}",
            $"shm={EncodeSidecar(fingerprint.Shm)}",
            $"journal={EncodeSidecar(fingerprint.Journal)}");

    public static async ValueTask<P275SourceFingerprint> CaptureAsync(
        string databasePath,
        IReadOnlyList<string> migrations,
        CancellationToken cancellationToken = default)
    {
        var main = await CaptureFileAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var wal = await CaptureSidecarAsync(databasePath + "-wal", cancellationToken).ConfigureAwait(false);
        var shm = await CaptureSidecarAsync(databasePath + "-shm", cancellationToken).ConfigureAwait(false);
        var journal = await CaptureSidecarAsync(databasePath + "-journal", cancellationToken).ConfigureAwait(false);
        var schemaHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', migrations))))
            .ToLowerInvariant();
        return new P275SourceFingerprint(Encode(new SafetyBackupSourceFingerprint(
            main.ByteLength,
            main.Sha256,
            schemaHash,
            wal,
            shm,
            journal)));
    }

    private static string EncodeSidecar(SafetyBackupSidecarFingerprint sidecar) =>
        string.Join(
            ':',
            sidecar.Present ? "1" : "0",
            sidecar.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sidecar.Sha256 ?? "-");

    private static async ValueTask<SafetyBackupSidecarFingerprint> CaptureSidecarAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
        {
            throw new IOException("A SQLite sidecar path is a directory.");
        }

        if (!File.Exists(path))
        {
            return new SafetyBackupSidecarFingerprint(false, 0, null);
        }

        var fingerprint = await CaptureFileAsync(path, cancellationToken).ConfigureAwait(false);
        return new SafetyBackupSidecarFingerprint(true, fingerprint.ByteLength, fingerprint.Sha256);
    }

    private static async ValueTask<FileFingerprint> CaptureFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!P275FileSafety.IsRegularFile(path))
        {
            throw new IOException("The fingerprint target is not a regular file.");
        }

        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 64 * 1024,
                Options = FileOptions.SequentialScan
            });
        var before = stream.Length;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var after = stream.Length;
        if (before != after)
        {
            throw new IOException("The fingerprint target changed while being read.");
        }

        return new FileFingerprint(after, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private readonly record struct FileFingerprint(long ByteLength, string Sha256);
}
