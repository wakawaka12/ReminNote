using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReminNote.Core.Protocol;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.Backup;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Tests.P275Migration;

public sealed class P275ProductionUpgradeTests
{
    private static readonly P275MigrationPlan Plan = new(
        [
            "20260828025922_InitialTaskSchema",
            "20260828120000_P2TaskLoop",
            "20260828130000_P2ContinuationDeleteBoundary"
        ],
        [
            "20260828025922_InitialTaskSchema",
            "20260828120000_P2TaskLoop",
            "20260828130000_P2ContinuationDeleteBoundary",
            "20260831090000_P25StorageConsistency"
        ]);

    [Fact]
    public async Task ProductionBackupProviderAcceptsRealP2Database()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "rn275-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            var databasePath = Path.Combine(root, "reminnote.sqlite");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true
            }.ToString();

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var context = new ReminNoteDbContext(
                    ReminNoteDbContext.CreateOptions(connection));
                await context.Database.MigrateAsync(
                    Plan.ApprovedSourceMigrations[^1],
                    cancellationToken);
            }

            SqliteConnection.ClearAllPools();
            var paths = new P275ProfilePaths(root, "default");
            paths.EnsureRuntimeDirectories();
            var profileScope = ProtocolProfileScope.Derive(
                "S-1-5-21-100-200-300-400",
                paths.ActiveDatabasePath);
            var source = await new P275ActiveSourceReader().ReadAsync(
                paths,
                profileScope,
                cancellationToken);
            var runId = Guid.CreateVersion7().ToString("D");

            var backup = await new P275SafetyBackupProvider().CreateVerifiedBackupAsync(
                new P275SafetyBackupRequest(
                    paths,
                    profileScope,
                    runId,
                    source,
                    Plan),
                cancellationToken);

            Assert.Equal(Plan.ApprovedSourceMigrations, source.AppliedMigrations);
            Assert.EndsWith($"{runId}.sqlite", backup.ArtifactId);
            Assert.True(File.Exists(backup.ArtifactPath));
            Assert.True(backup.ByteLength > 0);
            Assert.Equal(64, backup.Sha256.Length);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProductionRunnerBootstrapsMissingActiveDatabaseThroughCandidate()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "rn275-empty-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            var paths = new P275ProfilePaths(root, "default");
            paths.EnsureRuntimeDirectories();
            var profileScope = ProtocolProfileScope.Derive(
                "S-1-5-21-100-200-300-400",
                paths.ActiveDatabasePath);
            var plan = P275MigrationPlanCatalog.Current;

            var result = await CreateProductionRunner(paths)
                .RunAsync(
                    new P275MigrationRequest(
                        paths,
                        profileScope,
                        Guid.CreateVersion7().ToString("D"),
                        plan,
                        TimeSpan.FromSeconds(5)),
                    cancellationToken);

            Assert.True(result.Ready, result.FailureCode);
            Assert.True(result.Writable);
            Assert.True(result.Promoted);
            Assert.Equal(P275MigrationState.Ready, result.State);
            Assert.Null(result.FailureCode);
            Assert.Null(result.BackupArtifact);
            Assert.True(File.Exists(paths.ActiveDatabasePath));

            await using var connection = await OpenReadOnlyAsync(
                paths.ActiveDatabasePath,
                cancellationToken);
            Assert.Equal(
                plan.ApprovedTargetMigrations,
                await ReadAppliedMigrationsAsync(connection, cancellationToken));
            Assert.Equal(
                "ok",
                await ReadScalarStringAsync(
                    connection,
                    "PRAGMA integrity_check;",
                    cancellationToken));

            var state = await new P275ProductionMigrationStatePort(
                    new P275ProfileLayout(paths.ProfileRoot))
                .ReadAsync(profileScope, cancellationToken);
            Assert.True(state.IsUsable);
            Assert.Equal(P275MigrationState.Ready, state.Marker!.State);
            Assert.Empty(state.Marker.SourceSchema);
            Assert.Equal(plan.ApprovedTargetMigrations, state.Marker.TargetSchema);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProductionRunnerUpgradesP1ThroughP2ToP25AndIsRestartSafe()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "rn275-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            var taskId = await CreateP1ThenP2DatabaseAsync(root, cancellationToken);
            var paths = new P275ProfilePaths(root, "default");
            paths.EnsureRuntimeDirectories();
            var profileScope = ProtocolProfileScope.Derive(
                "S-1-5-21-100-200-300-400",
                paths.ActiveDatabasePath);
            var activeBefore = await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken);
            var runner = CreateProductionRunner(paths);
            var firstRunId = Guid.CreateVersion7().ToString("D");

            var first = await runner.RunAsync(
                new P275MigrationRequest(
                    paths,
                    profileScope,
                    firstRunId,
                    Plan,
                    TimeSpan.FromSeconds(5)),
                cancellationToken);

            Assert.True(first.Ready, first.FailureCode);
            Assert.True(first.Writable);
            Assert.True(first.Promoted);
            Assert.Equal(P275MigrationState.Ready, first.State);
            Assert.Null(first.FailureCode);
            Assert.NotNull(first.BackupArtifact);
            Assert.NotNull(first.HistoryDirectoryPath);
            Assert.True(File.Exists(Path.Combine(first.HistoryDirectoryPath!, "active.sqlite")));
            Assert.Equal(
                activeBefore,
                await FingerprintAsync(
                    Path.Combine(first.HistoryDirectoryPath!, "active.sqlite"),
                    cancellationToken));

            await using (var connection = await OpenReadOnlyAsync(paths.ActiveDatabasePath, cancellationToken))
            {
                Assert.Equal(
                    Plan.ApprovedTargetMigrations,
                    await ReadAppliedMigrationsAsync(connection, cancellationToken));
                Assert.Equal(
                    "P275-AUTOMATED-UPGRADE-SEED",
                    await ReadScalarStringAsync(
                        connection,
                        "SELECT title FROM tasks WHERE id = $id;",
                        cancellationToken,
                        ("$id", taskId.ToString("D"))));
                Assert.Equal(
                    "wal",
                    await ReadScalarStringAsync(
                        connection,
                        "PRAGMA journal_mode;",
                        cancellationToken));
                Assert.Equal(
                    "ok",
                    await ReadScalarStringAsync(
                        connection,
                        "PRAGMA integrity_check;",
                        cancellationToken));
            }

            var backupFilesBeforeRestart = Directory
                .EnumerateFiles(paths.BackupsDirectory, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(Path.GetFileName)
                .ToArray();
            var historyDirectoriesBeforeRestart = Directory
                .EnumerateDirectories(paths.HistoryDirectory, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var activeAfterFirst = await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken);

            var secondRunId = Guid.CreateVersion7().ToString("D");
            var second = await runner.RunAsync(
                new P275MigrationRequest(
                    paths,
                    profileScope,
                    secondRunId,
                    Plan,
                    TimeSpan.FromSeconds(5)),
                cancellationToken);

            Assert.True(second.Ready, second.FailureCode);
            Assert.True(second.Writable);
            Assert.False(second.Promoted);
            Assert.Equal(P275MigrationState.Ready, second.State);
            Assert.Null(second.FailureCode);
            Assert.Equal(activeAfterFirst, await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken));
            Assert.Equal(
                backupFilesBeforeRestart,
                Directory
                    .EnumerateFiles(paths.BackupsDirectory, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(Path.GetFileName)
                    .ToArray());
            Assert.Equal(
                historyDirectoriesBeforeRestart,
                Directory
                    .EnumerateDirectories(paths.HistoryDirectory, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray());

            var state = await new P275ProductionMigrationStatePort(
                    new P275ProfileLayout(paths.ProfileRoot))
                .ReadAsync(profileScope, cancellationToken);
            Assert.True(state.IsUsable);
            Assert.Equal(P275MigrationState.Ready, state.Marker!.State);
            Assert.Equal(secondRunId, state.Marker.RunId.ToString("D"));

            var historyBeforeRestore = Directory
                .EnumerateDirectories(paths.HistoryDirectory, "*", SearchOption.TopDirectoryOnly)
                .Count();
            var restore = await P275RecoveryService.RestoreAsync(
                new P275RecoveryRestoreRequest(
                    paths,
                    profileScope,
                    first.BackupArtifact!,
                    Plan,
                    TimeSpan.FromSeconds(5),
                    Guid.CreateVersion7().ToString("D")),
                cancellationToken);

            Assert.True(restore.Succeeded, restore.FailureCode);
            Assert.Null(restore.FailureCode);
            Assert.Equal(
                historyBeforeRestore + 1,
                Directory
                    .EnumerateDirectories(paths.HistoryDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Count());
            var restoredState = await new P275ProductionMigrationStatePort(
                    new P275ProfileLayout(paths.ProfileRoot))
                .ReadAsync(profileScope, cancellationToken);
            Assert.True(restoredState.IsUsable);
            Assert.Equal(P275MigrationState.Ready, restoredState.Marker!.State);
            Assert.Equal(firstRunId, restoredState.Marker.RunId.ToString("D"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProductionRunnerFailsClosedWhenBackupCapacityIsInsufficient()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "rn275-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            await CreateP1ThenP2DatabaseAsync(root, cancellationToken);
            var paths = new P275ProfilePaths(root, "default");
            paths.EnsureRuntimeDirectories();
            var profileScope = ProtocolProfileScope.Derive(
                "S-1-5-21-100-200-300-400",
                paths.ActiveDatabasePath);
            var activeBefore = await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken);
            var backupProvider = new P275SafetyBackupProvider(
                new SafetyBackupService(
                    new SafetyBackupTestHooks(OverrideAvailableFreeBytes: static _ => 0)));

            var result = await CreateProductionRunner(paths, backupProvider)
                .RunAsync(
                    new P275MigrationRequest(
                        paths,
                        profileScope,
                        Guid.CreateVersion7().ToString("D"),
                        Plan,
                        TimeSpan.FromSeconds(5)),
                    cancellationToken);

            Assert.False(result.Ready);
            Assert.False(result.Writable);
            Assert.Equal(P275MigrationState.BackupFailed, result.State);
            Assert.Equal(P275MigrationFailureCodes.BackupFailed, result.FailureCode);
            Assert.Equal(activeBefore, await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken));
            Assert.Empty(Directory.EnumerateFiles(paths.BackupsDirectory, "*.sqlite"));
            Assert.Empty(Directory.EnumerateFiles(paths.BackupsDirectory, "*.json"));

            var state = await new P275ProductionMigrationStatePort(
                    new P275ProfileLayout(paths.ProfileRoot))
                .ReadAsync(profileScope, cancellationToken);
            Assert.True(state.IsUsable);
            Assert.Equal(P275MigrationState.BackupFailed, state.Marker!.State);
            Assert.Equal(P275MigrationFailureCodes.BackupFailed, state.Marker.FailureCode);
            Assert.False(state.Marker.Ready);
            Assert.False(state.Marker.Writable);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProductionRunnerPreservesFailedCandidateWhenVerificationRejectsIt()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "rn275-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            await CreateP1ThenP2DatabaseAsync(root, cancellationToken);
            var paths = new P275ProfilePaths(root, "default");
            paths.EnsureRuntimeDirectories();
            var profileScope = ProtocolProfileScope.Derive(
                "S-1-5-21-100-200-300-400",
                paths.ActiveDatabasePath);
            var activeBefore = await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken);
            var runId = Guid.CreateVersion7().ToString("D");

            var result = await CreateProductionRunner(
                    paths,
                    candidateVerifier: new RejectingVerifier())
                .RunAsync(
                    new P275MigrationRequest(paths, profileScope, runId, Plan, TimeSpan.FromSeconds(5)),
                    cancellationToken);

            Assert.False(result.Ready);
            Assert.False(result.Writable);
            Assert.Equal(P275MigrationState.VerifyFailed, result.State);
            Assert.Equal(P275MigrationFailureCodes.VerifyFailed, result.FailureCode);
            Assert.Equal(activeBefore, await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken));
            Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "*.sqlite"));
            Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "*.json"));
            Assert.NotNull(result.CandidateDatabasePath);
            Assert.True(File.Exists(result.CandidateDatabasePath));
            Assert.Contains(
                Path.Combine("recovery", "failed", runId),
                result.CandidateDatabasePath!,
                StringComparison.OrdinalIgnoreCase);

            var state = await new P275ProductionMigrationStatePort(
                    new P275ProfileLayout(paths.ProfileRoot))
                .ReadAsync(profileScope, cancellationToken);
            Assert.True(state.IsUsable);
            Assert.Equal(P275MigrationState.VerifyFailed, state.Marker!.State);
            Assert.Equal(P275MigrationFailureCodes.VerifyFailed, state.Marker.FailureCode);
            Assert.False(state.Marker.Ready);
            Assert.False(state.Marker.Writable);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProductionRunnerTreatsPromotionUnknownAsNonWritableAndKeepsActive()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "rn275-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            await CreateP1ThenP2DatabaseAsync(root, cancellationToken);
            var paths = new P275ProfilePaths(root, "default");
            paths.EnsureRuntimeDirectories();
            var profileScope = ProtocolProfileScope.Derive(
                "S-1-5-21-100-200-300-400",
                paths.ActiveDatabasePath);
            var activeBefore = await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken);
            var runId = Guid.CreateVersion7().ToString("D");

            var result = await CreateProductionRunner(
                    paths,
                    promoter: new UnknownPromoter())
                .RunAsync(
                    new P275MigrationRequest(paths, profileScope, runId, Plan, TimeSpan.FromSeconds(5)),
                    cancellationToken);

            Assert.False(result.Ready);
            Assert.False(result.Writable);
            Assert.Equal(P275MigrationState.PromotionUnknown, result.State);
            Assert.Equal(P275MigrationFailureCodes.PromoteUnknown, result.FailureCode);
            Assert.Equal(activeBefore, await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken));
            Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "*.sqlite"));
            Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "*.json"));
            Assert.NotNull(result.CandidateDatabasePath);
            Assert.True(File.Exists(result.CandidateDatabasePath));

            var state = await new P275ProductionMigrationStatePort(
                    new P275ProfileLayout(paths.ProfileRoot))
                .ReadAsync(profileScope, cancellationToken);
            Assert.True(state.IsUsable);
            Assert.Equal(P275MigrationState.PromotionUnknown, state.Marker!.State);
            Assert.Equal(P275MigrationFailureCodes.PromoteUnknown, state.Marker.FailureCode);
            Assert.False(state.Marker.Ready);
            Assert.False(state.Marker.Writable);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProductionRunnerRejectsACompetingMigrationLockBeforeBackup()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "rn275-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            await CreateP1ThenP2DatabaseAsync(root, cancellationToken);
            var paths = new P275ProfilePaths(root, "default");
            paths.EnsureRuntimeDirectories();
            var profileScope = ProtocolProfileScope.Derive(
                "S-1-5-21-100-200-300-400",
                paths.ActiveDatabasePath);
            var activeBefore = await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken);
            var lockProvider = new P275FileMigrationLockProvider();
            var heldLease = await lockProvider.AcquireAsync(
                new P275MigrationLockRequest(
                    paths,
                    Guid.CreateVersion7().ToString("D"),
                    TimeSpan.FromSeconds(5)),
                cancellationToken);
            Assert.NotNull(heldLease);

            try
            {
                var result = await CreateProductionRunner(paths)
                    .RunAsync(
                        new P275MigrationRequest(
                            paths,
                            profileScope,
                            Guid.CreateVersion7().ToString("D"),
                            Plan,
                            TimeSpan.FromMilliseconds(150)),
                        cancellationToken);

                Assert.False(result.Ready);
                Assert.False(result.Writable);
                Assert.Equal(P275MigrationFailureCodes.Locked, result.FailureCode);
                Assert.Equal(activeBefore, await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken));
                Assert.Empty(Directory.EnumerateFiles(paths.BackupsDirectory, "*.sqlite"));
                Assert.Empty(Directory.EnumerateFiles(paths.BackupsDirectory, "*.json"));
            }
            finally
            {
                await heldLease.DisposeAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProductionRunnerKeepsCandidateWhenCheckpointIsBusy()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "rn275-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            await CreateP1ThenP2DatabaseAsync(root, cancellationToken);
            var paths = new P275ProfilePaths(root, "default");
            paths.EnsureRuntimeDirectories();
            var profileScope = ProtocolProfileScope.Derive(
                "S-1-5-21-100-200-300-400",
                paths.ActiveDatabasePath);
            var activeBefore = await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken);

            var result = await CreateProductionRunner(
                    paths,
                    candidateFinalizer: new BusyCandidateFinalizer())
                .RunAsync(
                    new P275MigrationRequest(
                        paths,
                        profileScope,
                        Guid.CreateVersion7().ToString("D"),
                        Plan,
                        TimeSpan.FromSeconds(5)),
                    cancellationToken);

            Assert.False(result.Ready);
            Assert.False(result.Writable);
            Assert.Equal(P275MigrationState.VerifyFailed, result.State);
            Assert.Equal(P275MigrationFailureCodes.VerifyFailed, result.FailureCode);
            Assert.Equal(activeBefore, await FingerprintAsync(paths.ActiveDatabasePath, cancellationToken));
            Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "*.sqlite"));
            Assert.Single(Directory.EnumerateFiles(paths.BackupsDirectory, "*.json"));
            Assert.NotNull(result.CandidateDatabasePath);
            Assert.True(File.Exists(result.CandidateDatabasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static P275MigrationRunner CreateProductionRunner(
        P275ProfilePaths paths,
        IP275SafetyBackupProvider? backupProvider = null,
        IP275CandidateFinalizer? candidateFinalizer = null,
        IP275CandidateVerifier? candidateVerifier = null,
        IP275AtomicPromoter? promoter = null) => new(
        new P275ActiveSourceReader(),
        backupProvider ?? new P275SafetyBackupProvider(),
        new P275FileWriterQuiescence(),
        new P275FileMigrationLockProvider(),
        new P275EfForwardMigrationApplier(),
        candidateFinalizer ?? new P275CandidateSidecarFinalizer(),
        candidateVerifier ?? new P275ProductionCandidateVerifier(),
        promoter ?? new P275AtomicFilePromoter(),
        new P275ProductionMigrationStatePort(new P275ProfileLayout(paths.ProfileRoot)));

    private static async Task<Guid> CreateP1ThenP2DatabaseAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(root, "reminnote.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        var taskId = Guid.CreateVersion7();

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var context = new ReminNoteDbContext(
                ReminNoteDbContext.CreateOptions(connection));
            await context.Database.MigrateAsync(
                Plan.ApprovedSourceMigrations[0],
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO tasks (id, title, time_type, local_date, result, " +
                "result_recorded_at, result_note, created_at, updated_at) " +
                "VALUES ($id, $title, 0, $localDate, NULL, NULL, NULL, $createdAt, $updatedAt);";
            command.Parameters.AddWithValue("$id", taskId.ToString("D"));
            command.Parameters.AddWithValue("$title", "P275-AUTOMATED-UPGRADE-SEED");
            command.Parameters.AddWithValue("$localDate", "2026-09-01");
            command.Parameters.AddWithValue("$createdAt", "2026-09-01T00:00:00.000000000Z");
            command.Parameters.AddWithValue("$updatedAt", "2026-09-01T00:00:00.000000000Z");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var context = new ReminNoteDbContext(
                ReminNoteDbContext.CreateOptions(connection));
            await context.Database.MigrateAsync(
                Plan.ApprovedSourceMigrations[^1],
                cancellationToken);
        }

        return taskId;
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<IReadOnlyList<string>> ReadAppliedMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT MigrationId FROM \"__EFMigrationsHistory\" ORDER BY rowid;";
        var migrations = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            migrations.Add(reader.GetString(0));
        }

        return migrations;
    }

    private static async Task<string?> ReadScalarStringAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull
            ? null
            : Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<FileFingerprint> FingerprintAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new FileFingerprint(stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private sealed record FileFingerprint(long Length, string Sha256);

    private sealed class RejectingVerifier : IP275CandidateVerifier
    {
        public ValueTask<P275VerificationResult> VerifyAsync(
            P275VerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                P275VerificationResult.Invalid(P275MigrationFailureCodes.VerifyFailed));
        }
    }

    private sealed class UnknownPromoter : IP275AtomicPromoter
    {
        public ValueTask<P275PromotionResult> PromoteAsync(
            P275PromotionRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(P275PromotionResult.Unknown());
        }
    }

    private sealed class BusyCandidateFinalizer : IP275CandidateFinalizer
    {
        public async ValueTask<P275CandidateFinalizationResult> FinalizeAsync(
            P275Candidate candidate,
            CancellationToken cancellationToken = default)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = candidate.Files.CandidateDatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true
            }.ToString());
            await using (connection)
            {
                await connection.OpenAsync(cancellationToken);
                await using (var begin = connection.CreateCommand())
                {
                    begin.CommandText = "BEGIN IMMEDIATE;";
                    await begin.ExecuteNonQueryAsync(cancellationToken);
                }

                try
                {
                    return await new P275CandidateSidecarFinalizer()
                        .FinalizeAsync(candidate, cancellationToken);
                }
                finally
                {
                    await using var rollback = connection.CreateCommand();
                    rollback.CommandText = "ROLLBACK;";
                    try
                    {
                        await rollback.ExecuteNonQueryAsync(CancellationToken.None);
                    }
                    catch (SqliteException)
                    {
                    }
                }
            }
        }
    }
}
