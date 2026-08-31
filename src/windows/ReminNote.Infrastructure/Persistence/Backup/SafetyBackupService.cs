using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.Data.Sqlite;

namespace ReminNote.Infrastructure.Persistence.Backup;

/// <summary>
/// Creates one profile-bound, independently readable SQLite safety backup.
/// This type owns only the P2.75-01 artifact boundary. It does not migrate,
/// stage, promote, restore, or write a runtime state marker.
/// </summary>
public sealed class SafetyBackupService
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false
    };

    private readonly SafetyBackupTestHooks? testHooks;

    public SafetyBackupService(SafetyBackupTestHooks? testHooks = null)
    {
        this.testHooks = testHooks;
    }

    /// <summary>
    /// Attempts one backup. Expected filesystem, SQLite, capacity and
    /// durability failures are returned as stable codes. Cancellation remains
    /// cancellable and is not turned into a false verified result.
    /// </summary>
    public async ValueTask<SafetyBackupResult> CreateAsync(
        SafetyBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SafetyBackupResult.Failed(SafetyBackupFailureCodes.ArgumentsInvalid);
        }

        var phase = BackupPhase.Inputs;
        try
        {
            var inputs = ValidateRequest(request);
            phase = BackupPhase.Paths;
            var paths = ResolvePaths(inputs);

            phase = BackupPhase.SourceInventory;
            var source = await ReadSourceInventoryAsync(
                    paths.ActiveDatabasePath,
                    SafetyBackupFailureCodes.SourceInvalid,
                    cancellationToken)
                .ConfigureAwait(false);

            phase = BackupPhase.OutputPlanning;
            var artifactNames = CreateArtifactNames(
                source.Schema,
                inputs.TargetSchema,
                inputs.CreatedAtUtc,
                inputs.RunId);
            EnsureOutputNamesAvailable(paths.BackupDirectory, artifactNames);

            phase = BackupPhase.Capacity;
            EnsureCapacity(paths.BackupDirectory, source.Fingerprint.ByteLength);

            var partialDatabasePath = Path.Combine(
                paths.BackupDirectory,
                artifactNames.PartialDatabaseName);
            var partialManifestPath = Path.Combine(
                paths.BackupDirectory,
                artifactNames.PartialManifestName);
            EnsureTemporaryNamesAvailable(partialDatabasePath, partialManifestPath);

            phase = BackupPhase.Snapshot;
            ReservePartialFile(partialDatabasePath);
            await SnapshotAsync(
                    paths.ActiveDatabasePath,
                    partialDatabasePath,
                    cancellationToken)
                .ConfigureAwait(false);

            phase = BackupPhase.Durability;
            FlushWriteThrough(partialDatabasePath, isManifest: false);

            phase = BackupPhase.Verification;
            var partialFingerprint = await VerifyArtifactAsync(
                    partialDatabasePath,
                    cancellationToken)
                .ConfigureAwait(false);

            phase = BackupPhase.SourceRecheck;
            var sourceAfter = await ReadSourceInventoryAsync(
                    paths.ActiveDatabasePath,
                    SafetyBackupFailureCodes.SourceChanged,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!source.Schema.SequenceEqual(sourceAfter.Schema, StringComparer.Ordinal) ||
                source.Fingerprint != sourceAfter.Fingerprint)
            {
                throw Failure(SafetyBackupFailureCodes.SourceChanged);
            }

            phase = BackupPhase.AtomicArtifactMove;
            var artifactPath = Path.Combine(paths.BackupDirectory, artifactNames.DatabaseName);
            EnsureFinalTargetAbsent(artifactPath);
            File.Move(partialDatabasePath, artifactPath);

            phase = BackupPhase.Verification;
            var finalFingerprint = await VerifyArtifactAsync(
                    artifactPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (partialFingerprint != finalFingerprint)
            {
                throw Failure(SafetyBackupFailureCodes.BackupUnreadable);
            }

            var manifest = new SafetyBackupManifest(
                ContractVersion: SafetyBackupContract.ContractVersion,
                RunId: inputs.RunId.ToString("D").ToLowerInvariant(),
                ProfileScope: inputs.ProfileScope,
                SourceSchema: source.Schema,
                TargetSchema: inputs.TargetSchema,
                CreatedAtUtc: FormatManifestTimestamp(inputs.CreatedAtUtc),
                Artifact: artifactNames.DatabaseName,
                ByteLength: finalFingerprint.ByteLength,
                Sha256: finalFingerprint.Sha256,
                SourceFingerprint: source.Fingerprint,
                Result: SafetyBackupContract.VerifiedResult);

            phase = BackupPhase.Manifest;
            var manifestPath = Path.Combine(paths.BackupDirectory, artifactNames.ManifestName);
            await WriteManifestAtomicallyAsync(
                    manifest,
                    manifestPath,
                    partialManifestPath,
                    cancellationToken)
                .ConfigureAwait(false);

            return SafetyBackupResult.Verified(manifest, artifactNames.ManifestName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SafetyBackupFailureException exception)
        {
            return SafetyBackupResult.Failed(exception.Code);
        }
        catch (Exception) when (phase is BackupPhase.Inputs)
        {
            return SafetyBackupResult.Failed(SafetyBackupFailureCodes.ArgumentsInvalid);
        }
        catch (Exception) when (phase is BackupPhase.Paths)
        {
            return SafetyBackupResult.Failed(SafetyBackupFailureCodes.PathInvalid);
        }
        catch (Exception) when (phase is BackupPhase.SourceInventory)
        {
            return SafetyBackupResult.Failed(SafetyBackupFailureCodes.SourceInvalid);
        }
        catch (Exception) when (phase is BackupPhase.SourceRecheck)
        {
            return SafetyBackupResult.Failed(SafetyBackupFailureCodes.SourceChanged);
        }
        catch (Exception) when (phase is BackupPhase.Verification)
        {
            return SafetyBackupResult.Failed(SafetyBackupFailureCodes.BackupUnreadable);
        }
        catch (Exception)
        {
            return SafetyBackupResult.Failed(SafetyBackupFailureCodes.BackupFailed);
        }
    }

    private static ValidatedRequest ValidateRequest(SafetyBackupRequest request)
    {
        if (request.RunId == Guid.Empty)
        {
            throw Failure(SafetyBackupFailureCodes.ArgumentsInvalid);
        }

        var dataRoot = CanonicalizeAbsoluteLocalPath(
            request.DataRoot,
            SafetyBackupFailureCodes.PathInvalid);
        var profileRoot = CanonicalizeAbsoluteLocalPath(
            request.ProfileRoot,
            SafetyBackupFailureCodes.PathInvalid);
        var activeDatabasePath = CanonicalizeAbsoluteLocalPath(
            request.ActiveDatabasePath,
            SafetyBackupFailureCodes.PathInvalid);

        ValidateBoundedToken(
            request.ProfileScope,
            SafetyBackupContract.MaxProfileScopeBytes,
            SafetyBackupFailureCodes.ArgumentsInvalid);
        var targetSchema = ValidateSchemaIds(
            request.TargetSchema,
            SafetyBackupFailureCodes.ArgumentsInvalid);
        if (!IsWithinRoot(profileRoot, dataRoot))
        {
            throw Failure(SafetyBackupFailureCodes.PathInvalid);
        }

        var expectedActiveDatabasePath = NormalizePath(
            Path.Combine(profileRoot, "reminnote.sqlite"));
        if (!PathsEqual(activeDatabasePath, expectedActiveDatabasePath))
        {
            throw Failure(SafetyBackupFailureCodes.PathInvalid);
        }

        var createdAtUtc = request.CreatedAtUtc.ToUniversalTime();
        return new ValidatedRequest(
            dataRoot,
            profileRoot,
            activeDatabasePath,
            request.ProfileScope,
            targetSchema,
            request.RunId,
            createdAtUtc);
    }

    private static ResolvedPaths ResolvePaths(ValidatedRequest request)
    {
        EnsureExistingDirectory(request.DataRoot, SafetyBackupFailureCodes.PathInvalid);
        EnsureExistingDirectory(request.ProfileRoot, SafetyBackupFailureCodes.PathInvalid);
        EnsureNoReparsePathBetween(request.DataRoot, request.ProfileRoot);

        if (File.Exists(request.ProfileRoot) ||
            !Directory.Exists(request.ProfileRoot))
        {
            throw Failure(SafetyBackupFailureCodes.PathInvalid);
        }

        EnsureSourcePathShape(request.ActiveDatabasePath);

        var backupDirectory = NormalizePath(Path.Combine(request.ProfileRoot, "backups"));
        if (!IsWithinRoot(backupDirectory, request.ProfileRoot))
        {
            throw Failure(SafetyBackupFailureCodes.PathInvalid);
        }

        try
        {
            if (File.Exists(backupDirectory))
            {
                throw Failure(SafetyBackupFailureCodes.PathInvalid);
            }

            Directory.CreateDirectory(backupDirectory);
            EnsureExistingDirectory(backupDirectory, SafetyBackupFailureCodes.PathInvalid);
            EnsureNoReparsePoint(backupDirectory, SafetyBackupFailureCodes.PathInvalid);
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(SafetyBackupFailureCodes.BackupFailed);
        }

        return new ResolvedPaths(
            request.DataRoot,
            request.ProfileRoot,
            request.ActiveDatabasePath,
            backupDirectory,
            request.ProfileScope,
            request.TargetSchema,
            request.RunId,
            request.CreatedAtUtc);
    }

    private static ArtifactNames CreateArtifactNames(
        IReadOnlyList<string> sourceSchema,
        IReadOnlyList<string> targetSchema,
        DateTimeOffset createdAtUtc,
        Guid runId)
    {
        var fromSchemaToken = CreateSchemaToken(sourceSchema);
        var toSchemaToken = CreateSchemaToken(targetSchema);
        var timestamp = createdAtUtc.ToString(
            "yyyyMMdd'T'HHmmssfff'Z'",
            CultureInfo.InvariantCulture);
        var runIdToken = runId.ToString("D").ToLowerInvariant();
        var stem = $"migration-{fromSchemaToken}-{toSchemaToken}-{timestamp}-{runIdToken}";
        var databaseName = stem + ".sqlite";
        var manifestName = stem + ".json";
        var partialDatabaseName = $".reminnote-backup-{runIdToken}.sqlite.partial";
        var partialManifestName = $".reminnote-backup-{runIdToken}.json.partial";

        if (!IsSafeFileName(databaseName) ||
            !IsSafeFileName(manifestName) ||
            !IsSafeFileName(partialDatabaseName) ||
            !IsSafeFileName(partialManifestName))
        {
            throw Failure(SafetyBackupFailureCodes.BackupFailed);
        }

        return new ArtifactNames(
            databaseName,
            manifestName,
            partialDatabaseName,
            partialManifestName);
    }

    private static string CreateSchemaToken(IReadOnlyList<string> schema)
    {
        if (schema.Count == 0)
        {
            return "empty";
        }

        // The manifest carries the complete ordered history. The file name
        // uses only the validated schema head so a normal Windows profile
        // path does not become unusably long as migrations accumulate.
        return schema[^1];
    }

    private static void EnsureOutputNamesAvailable(
        string backupDirectory,
        ArtifactNames artifactNames)
    {
        var databasePath = Path.Combine(backupDirectory, artifactNames.DatabaseName);
        var manifestPath = Path.Combine(backupDirectory, artifactNames.ManifestName);
        EnsureArtifactPathLength(databasePath);
        EnsureArtifactPathLength(manifestPath);
        EnsureFinalTargetAbsent(databasePath);
        EnsureFinalTargetAbsent(manifestPath);
    }

    private static void EnsureArtifactPathLength(string path)
    {
        if (Path.GetFullPath(path).Length > SafetyBackupContract.MaxSafeArtifactPathChars)
        {
            throw Failure(SafetyBackupFailureCodes.BackupFailed);
        }
    }

    private static void EnsureTemporaryNamesAvailable(
        string partialDatabasePath,
        string partialManifestPath)
    {
        EnsureFinalTargetAbsent(partialDatabasePath);
        EnsureFinalTargetAbsent(partialManifestPath);
    }

    private static void EnsureFinalTargetAbsent(string path)
    {
        EnsurePathAbsent(path, SafetyBackupFailureCodes.BackupFailed);
    }

    private static void EnsurePathAbsent(string path, string failureCode)
    {
        try
        {
            _ = File.GetAttributes(path);
            throw Failure(failureCode);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureCode);
        }
    }

    private void EnsureCapacity(string backupDirectory, long sourceByteLength)
    {
        if (sourceByteLength <= 0)
        {
            throw Failure(SafetyBackupFailureCodes.SourceInvalid);
        }

        long requiredBytes;
        try
        {
            requiredBytes = checked(
                sourceByteLength +
                SafetyBackupContract.MaxManifestBytes +
                SafetyBackupContract.FreeSpaceReserveBytes);
        }
        catch (OverflowException)
        {
            throw Failure(SafetyBackupFailureCodes.BackupFailed);
        }

        var driveRoot = Path.GetPathRoot(backupDirectory);
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            throw Failure(SafetyBackupFailureCodes.PathInvalid);
        }

        try
        {
            var availableBytes = new DriveInfo(driveRoot).AvailableFreeSpace;
            if (testHooks?.OverrideAvailableFreeBytes is { } overrideAvailableBytes)
            {
                availableBytes = overrideAvailableBytes(availableBytes);
            }

            if (availableBytes < requiredBytes)
            {
                throw Failure(SafetyBackupFailureCodes.BackupFailed);
            }
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(SafetyBackupFailureCodes.BackupFailed);
        }
    }

    private static void ReservePartialFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough | FileOptions.SequentialScan);
        }
        catch (Exception)
        {
            throw Failure(SafetyBackupFailureCodes.BackupFailed);
        }
    }

    private static async Task SnapshotAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var sourceBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true,
                Pooling = false
            };
            var destinationBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true,
                Pooling = false
            };

            await using (var source = new SqliteConnection(sourceBuilder.ConnectionString))
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteNonQueryAsync(
                        source,
                        "PRAGMA query_only = ON;",
                        cancellationToken)
                    .ConfigureAwait(false);

                await using (var destination = new SqliteConnection(destinationBuilder.ConnectionString))
                {
                    await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await ExecuteNonQueryAsync(
                            destination,
                            "PRAGMA journal_mode = DELETE;",
                            cancellationToken)
                        .ConfigureAwait(false);
                    await ExecuteNonQueryAsync(
                            destination,
                            "PRAGMA synchronous = FULL;",
                            cancellationToken)
                        .ConfigureAwait(false);

                    cancellationToken.ThrowIfCancellationRequested();
                    source.BackupDatabase(destination);

                    var journalMode = await ExecuteScalarStringAsync(
                            destination,
                            "PRAGMA journal_mode = DELETE;",
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(journalMode, "delete", StringComparison.OrdinalIgnoreCase))
                    {
                        throw Failure(SafetyBackupFailureCodes.BackupUnreadable);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(SafetyBackupFailureCodes.BackupFailed);
        }

        EnsureNoArtifactSidecars(destinationPath);
    }

    private void FlushWriteThrough(string path, bool isManifest)
    {
        try
        {
            if (isManifest)
            {
                testHooks?.BeforeManifestWriteThrough?.Invoke();
            }
            else
            {
                testHooks?.BeforeDatabaseWriteThrough?.Invoke();
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.WriteThrough | FileOptions.SequentialScan);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception)
        {
            throw Failure(SafetyBackupFailureCodes.BackupFailed);
        }
    }

    private static async Task<FileFingerprint> VerifyArtifactAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureRegularArtifactFile(path);
            EnsureNoArtifactSidecars(path);
            if (!await HasSqliteHeaderAsync(
                        path,
                        SafetyBackupFailureCodes.BackupUnreadable,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                throw Failure(SafetyBackupFailureCodes.BackupUnreadable);
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true,
                Pooling = false
            };
            await using (var connection = new SqliteConnection(builder.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await ConfigureReadOnlyConnectionAsync(
                        connection,
                        SafetyBackupFailureCodes.BackupUnreadable,
                        cancellationToken)
                    .ConfigureAwait(false);
                await VerifyIntegrityAsync(
                        connection,
                        SafetyBackupFailureCodes.IntegrityFailed,
                        SafetyBackupFailureCodes.ForeignKeyFailed,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            EnsureNoArtifactSidecars(path);
            var fingerprint = await FingerprintFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (fingerprint.ByteLength <= 0)
            {
                throw Failure(SafetyBackupFailureCodes.BackupUnreadable);
            }

            EnsureNoArtifactSidecars(path);
            return fingerprint;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(SafetyBackupFailureCodes.BackupUnreadable);
        }
    }

    private async Task WriteManifestAtomicallyAsync(
        SafetyBackupManifest manifest,
        string finalPath,
        string partialPath,
        CancellationToken cancellationToken)
    {
        byte[] json;
        try
        {
            json = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
        }
        catch (Exception)
        {
            throw Failure(SafetyBackupFailureCodes.BackupFailed);
        }

        if (json.Length == 0 || json.Length > SafetyBackupContract.MaxManifestBytes)
        {
            throw Failure(SafetyBackupFailureCodes.BackupFailed);
        }

        EnsureFinalTargetAbsent(finalPath);
        try
        {
            using (var stream = new FileStream(
                       partialPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       options: FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                stream.Write(json, 0, json.Length);
                cancellationToken.ThrowIfCancellationRequested();
                testHooks?.BeforeManifestWriteThrough?.Invoke();
                stream.Flush(flushToDisk: true);
            }

            var observed = await File.ReadAllBytesAsync(partialPath, cancellationToken)
                .ConfigureAwait(false);
            if (!observed.AsSpan().SequenceEqual(json))
            {
                throw Failure(SafetyBackupFailureCodes.BackupFailed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, finalPath);
            var observedFinal = await File.ReadAllBytesAsync(finalPath, cancellationToken)
                .ConfigureAwait(false);
            if (!observedFinal.AsSpan().SequenceEqual(json))
            {
                throw Failure(SafetyBackupFailureCodes.BackupFailed);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(SafetyBackupFailureCodes.BackupFailed);
        }
    }

    private static async Task<SourceInventory> ReadSourceInventoryAsync(
        string path,
        string failureCode,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureSourcePathShape(path);
            if (!await HasSqliteHeaderAsync(
                        path,
                        failureCode,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                throw Failure(failureCode);
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true,
                Pooling = false
            };
            await using var connection = new SqliteConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureReadOnlyConnectionAsync(
                    connection,
                    failureCode,
                    cancellationToken)
                .ConfigureAwait(false);
            var schema = await ReadMigrationHistoryAsync(connection, failureCode, cancellationToken)
                .ConfigureAwait(false);
            await VerifyIntegrityAsync(
                    connection,
                    failureCode,
                    failureCode,
                    cancellationToken)
                .ConfigureAwait(false);
            var fingerprint = await CaptureSourceFingerprintAsync(
                    path,
                    schema,
                    failureCode,
                    cancellationToken)
                .ConfigureAwait(false);
            return new SourceInventory(schema, fingerprint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureCode);
        }
    }

    private static async Task<string[]> ReadMigrationHistoryAsync(
        SqliteConnection connection,
        string failureCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var values = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0) || values.Count >= SafetyBackupContract.MaxSchemaEntries)
                {
                    throw Failure(failureCode);
                }

                var value = reader.GetString(0);
                ValidateMigrationId(value, failureCode);
                if (!seen.Add(value))
                {
                    throw Failure(failureCode);
                }

                values.Add(value);
            }

            return values.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureCode);
        }
    }

    private static async Task<SafetyBackupSourceFingerprint> CaptureSourceFingerprintAsync(
        string path,
        IReadOnlyList<string> schema,
        string failureCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var main = await FingerprintFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (main.ByteLength <= 0)
            {
                throw Failure(failureCode);
            }

            var schemaHistoryHash = HashSchemaHistory(schema);
            var wal = await CaptureSidecarFingerprintAsync(
                    path + "-wal",
                    failureCode,
                    cancellationToken)
                .ConfigureAwait(false);
            var shm = await CaptureSidecarFingerprintAsync(
                    path + "-shm",
                    failureCode,
                    cancellationToken)
                .ConfigureAwait(false);
            var journal = await CaptureSidecarFingerprintAsync(
                    path + "-journal",
                    failureCode,
                    cancellationToken)
                .ConfigureAwait(false);
            return new SafetyBackupSourceFingerprint(
                main.ByteLength,
                main.Sha256,
                schemaHistoryHash,
                wal,
                shm,
                journal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureCode);
        }
    }

    private static async Task<SafetyBackupSidecarFingerprint> CaptureSidecarFingerprintAsync(
        string path,
        string failureCode,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(path))
        {
            throw Failure(failureCode);
        }

        if (!File.Exists(path))
        {
            return new SafetyBackupSidecarFingerprint(false, 0, null);
        }

        try
        {
            EnsureNoReparsePoint(path, failureCode);
            var fingerprint = await FingerprintFileAsync(path, cancellationToken)
                .ConfigureAwait(false);
            return new SafetyBackupSidecarFingerprint(
                true,
                fingerprint.ByteLength,
                fingerprint.Sha256);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureCode);
        }
    }

    private static async Task ConfigureReadOnlyConnectionAsync(
        SqliteConnection connection,
        string failureCode,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
                connection,
                "PRAGMA foreign_keys = ON;",
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteNonQueryAsync(
                connection,
                "PRAGMA query_only = ON;",
                cancellationToken)
            .ConfigureAwait(false);

        var queryOnly = await ExecuteScalarLongAsync(
                connection,
                "PRAGMA query_only;",
                cancellationToken)
            .ConfigureAwait(false);
        var foreignKeys = await ExecuteScalarLongAsync(
                connection,
                "PRAGMA foreign_keys;",
                cancellationToken)
            .ConfigureAwait(false);
        if (queryOnly != 1 || foreignKeys != 1)
        {
            throw Failure(failureCode);
        }
    }

    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        string integrityFailureCode,
        string foreignKeyFailureCode,
        CancellationToken cancellationToken)
    {
        var integrity = await ExecuteScalarStringAsync(
                connection,
                "PRAGMA integrity_check;",
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw Failure(integrityFailureCode);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Failure(foreignKeyFailureCode);
        }
    }

    private static async Task<bool> HasSqliteHeaderAsync(
        string path,
        string failureCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                options: FileOptions.SequentialScan);
            if (stream.Length < SqliteHeader.Length)
            {
                return false;
            }

            var header = new byte[SqliteHeader.Length];
            var offset = 0;
            while (offset < header.Length)
            {
                var read = await stream.ReadAsync(
                        header.AsMemory(offset),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }

                offset += read;
            }

            return header.AsSpan().SequenceEqual(SqliteHeader);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureCode);
        }
    }

    private static async Task<FileFingerprint> FingerprintFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            var lengthBefore = stream.Length;
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var lengthAfter = stream.Length;
            if (lengthBefore != lengthAfter)
            {
                throw new IOException("The source file changed while it was being read.");
            }

            return new FileFingerprint(
                lengthAfter,
                Convert.ToHexString(hash).ToLowerInvariant());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new IOException("The file fingerprint could not be captured.");
        }
    }

    private static string HashSchemaHistory(IReadOnlyList<string> schema)
    {
        var value = string.Join('\n', schema);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static void EnsureSourcePathShape(string path)
    {
        if (Directory.Exists(path) || !File.Exists(path))
        {
            throw Failure(SafetyBackupFailureCodes.SourceInvalid);
        }

        EnsureNoReparsePoint(path, SafetyBackupFailureCodes.PathInvalid);
    }

    private static void EnsureRegularArtifactFile(string path)
    {
        if (Directory.Exists(path) || !File.Exists(path))
        {
            throw Failure(SafetyBackupFailureCodes.BackupUnreadable);
        }

        EnsureNoReparsePoint(path, SafetyBackupFailureCodes.BackupUnreadable);
    }

    private static void EnsureNoArtifactSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            EnsurePathAbsent(path + suffix, SafetyBackupFailureCodes.BackupUnreadable);
        }
    }

    private static void EnsureExistingDirectory(string path, string failureCode)
    {
        try
        {
            if (File.Exists(path) || !Directory.Exists(path))
            {
                throw Failure(failureCode);
            }

            EnsureNoReparsePoint(path, failureCode);
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureCode);
        }
    }

    private static void EnsureNoReparsePathBetween(string root, string child)
    {
        if (!IsWithinRoot(child, root))
        {
            throw Failure(SafetyBackupFailureCodes.PathInvalid);
        }

        var relative = Path.GetRelativePath(root, child);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var part in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            EnsureExistingDirectory(current, SafetyBackupFailureCodes.PathInvalid);
        }
    }

    private static void EnsureNoReparsePoint(string path, string failureCode)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw Failure(failureCode);
            }
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureCode);
        }
    }

    private static string CanonicalizeAbsoluteLocalPath(string path, string failureCode)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            IsUnc(path))
        {
            throw Failure(failureCode);
        }

        try
        {
            var fullPath = NormalizePath(Path.GetFullPath(path));
            if (fullPath.Length > 32_767)
            {
                throw Failure(failureCode);
            }

            return fullPath;
        }
        catch (SafetyBackupFailureException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(failureCode);
        }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && fullPath.Length > root.Length)
        {
            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return fullPath;
    }

    private static bool IsWithinRoot(string path, string root)
    {
        if (PathsEqual(path, root))
        {
            return true;
        }

        var separator = root.EndsWith(Path.DirectorySeparatorChar) ||
            root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(separator, GetPathComparison());
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, GetPathComparison());

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool IsUnc(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);

    private static string[] ValidateSchemaIds(
        IReadOnlyList<string> schema,
        string failureCode)
    {
        if (schema is null || schema.Count > SafetyBackupContract.MaxSchemaEntries)
        {
            throw Failure(failureCode);
        }

        var result = new string[schema.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < schema.Count; index++)
        {
            var value = schema[index];
            ValidateMigrationId(value, failureCode);
            if (!seen.Add(value))
            {
                throw Failure(failureCode);
            }

            result[index] = value;
        }

        return result;
    }

    private static void ValidateMigrationId(string value, string failureCode)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Encoding.UTF8.GetByteCount(value) > SafetyBackupContract.MaxMigrationIdBytes ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z') and
                not (>= 'A' and <= 'Z') and
                not (>= '0' and <= '9') and
                not ('_' or '-' or '.')))
        {
            throw Failure(failureCode);
        }
    }

    private static void ValidateBoundedToken(
        string value,
        int maximumBytes,
        string failureCode)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Encoding.UTF8.GetByteCount(value) > maximumBytes ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z') and
                not (>= 'A' and <= 'Z') and
                not (>= '0' and <= '9') and
                not ('_' or '-' or '.')))
        {
            throw Failure(failureCode);
        }
    }

    private static bool IsSafeFileName(string value) =>
        value.Length <= SafetyBackupContract.MaxSafeFileNameChars &&
        value.Length > 0 &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        value.IndexOf(Path.DirectorySeparatorChar) < 0 &&
        value.IndexOf(Path.AltDirectorySeparatorChar) < 0;

    private static string FormatManifestTimestamp(DateTimeOffset createdAtUtc) =>
        createdAtUtc.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture);

    private static async Task<int> ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ExecuteScalarStringAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static SafetyBackupFailureException Failure(string code) =>
        new(code);

    private enum BackupPhase
    {
        Inputs,
        Paths,
        SourceInventory,
        OutputPlanning,
        Capacity,
        Snapshot,
        Durability,
        Verification,
        SourceRecheck,
        AtomicArtifactMove,
        Manifest
    }

    private sealed record ValidatedRequest(
        string DataRoot,
        string ProfileRoot,
        string ActiveDatabasePath,
        string ProfileScope,
        string[] TargetSchema,
        Guid RunId,
        DateTimeOffset CreatedAtUtc);

    private sealed record ResolvedPaths(
        string DataRoot,
        string ProfileRoot,
        string ActiveDatabasePath,
        string BackupDirectory,
        string ProfileScope,
        string[] TargetSchema,
        Guid RunId,
        DateTimeOffset CreatedAtUtc);

    private sealed record ArtifactNames(
        string DatabaseName,
        string ManifestName,
        string PartialDatabaseName,
        string PartialManifestName);

    private sealed record SourceInventory(
        string[] Schema,
        SafetyBackupSourceFingerprint Fingerprint);

    private sealed record FileFingerprint(long ByteLength, string Sha256);

    private sealed class SafetyBackupFailureException : Exception
    {
        public SafetyBackupFailureException(string code)
            : base(code)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
