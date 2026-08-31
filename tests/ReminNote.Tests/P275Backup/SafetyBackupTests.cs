using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ReminNote.Core.Protocol;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.Backup;
using TaskTimeType = ReminNote.Core.Tasks.TaskTimeType;

namespace ReminNote.Tests.P275Backup;

public sealed class SafetyBackupTests
{
    private static readonly DateTimeOffset FixedCreatedAtUtc = new(
        2026,
        8,
        31,
        12,
        34,
        56,
        789,
        TimeSpan.Zero);

    [Fact]
    public async Task CreatesVerifiedStandaloneBackupWithBoundedManifest()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var activeBefore = await ReadFileFingerprintAsync(fixture.DatabasePath);
        var result = await new SafetyBackupService().CreateAsync(
            fixture.CreateRequest(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsVerified, result.FailureCode);
        Assert.Null(result.FailureCode);
        Assert.NotNull(result.Artifact);
        Assert.NotNull(result.ManifestArtifact);
        Assert.NotNull(result.Manifest);
        Assert.Equal(SafetyBackupContract.VerifiedResult, result.Manifest!.Result);
        Assert.Equal(result.Artifact, result.Manifest.Artifact);
        Assert.Equal(result.ByteLength, result.Manifest.ByteLength);
        Assert.Equal(result.Sha256, result.Manifest.Sha256);
        Assert.Equal(SafetyBackupContract.ContractVersion, result.Manifest.ContractVersion);
        Assert.Equal(fixture.ProfileScope, result.Manifest.ProfileScope);
        Assert.Equal(fixture.SourceSchema, result.Manifest.SourceSchema);
        Assert.Equal(fixture.TargetSchema, result.Manifest.TargetSchema);
        Assert.Matches(
            "^migration-[A-Za-z0-9._-]+-[A-Za-z0-9._-]+-20260831T123456789Z-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\.sqlite$",
            result.Artifact);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, result.Artifact);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, result.Artifact);

        var artifactPath = Path.Combine(fixture.BackupDirectory, result.Artifact);
        var manifestPath = Path.Combine(fixture.BackupDirectory, result.ManifestArtifact);
        Assert.True(File.Exists(artifactPath));
        Assert.True(File.Exists(manifestPath));
        Assert.False(File.Exists(artifactPath + "-wal"));
        Assert.False(File.Exists(artifactPath + "-shm"));
        Assert.False(File.Exists(artifactPath + "-journal"));
        Assert.Empty(Directory.EnumerateFiles(fixture.BackupDirectory, "*.partial"));

        var manifestJson = await File.ReadAllTextAsync(
            manifestPath,
            TestContext.Current.CancellationToken);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(manifestJson) <=
            SafetyBackupContract.MaxManifestBytes);
        Assert.DoesNotContain(fixture.Root, manifestJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.DatabasePath, manifestJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.SecretTaskTitle, manifestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("token-value-that-must-not-appear", manifestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", manifestJson, StringComparison.OrdinalIgnoreCase);
        using (var document = JsonDocument.Parse(manifestJson))
        {
            Assert.Equal(
                SafetyBackupContract.VerifiedResult,
                document.RootElement.GetProperty("result").GetString());
            Assert.Equal(
                result.Artifact,
                document.RootElement.GetProperty("artifact").GetString());
            Assert.True(document.RootElement.GetProperty("sourceFingerprint").ValueKind == JsonValueKind.Object);
        }

        await AssertHealthyReadOnlyArtifactAsync(artifactPath);
        Assert.Equal(1L, await CountRowsAsync(artifactPath, "tasks"));
        var artifactFingerprint = await ReadFileFingerprintAsync(artifactPath);
        Assert.Equal(result.ByteLength, artifactFingerprint.ByteLength);
        Assert.Equal(result.Sha256, artifactFingerprint.Sha256);

        var activeAfter = await ReadFileFingerprintAsync(fixture.DatabasePath);
        Assert.Equal(activeBefore, activeAfter);
    }

    [Fact]
    public async Task SameRunIdCollisionDoesNotOverwriteExistingVerifiedBackup()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var runId = Guid.CreateVersion7();
        var request = fixture.CreateRequest(runId);
        var service = new SafetyBackupService();

        var first = await service.CreateAsync(request, TestContext.Current.CancellationToken);
        Assert.True(first.IsVerified, first.FailureCode);
        var artifactPath = Path.Combine(fixture.BackupDirectory, first.Artifact!);
        var manifestPath = Path.Combine(fixture.BackupDirectory, first.ManifestArtifact!);
        var artifactBefore = await File.ReadAllBytesAsync(
            artifactPath,
            TestContext.Current.CancellationToken);
        var manifestBefore = await File.ReadAllBytesAsync(
            manifestPath,
            TestContext.Current.CancellationToken);

        var second = await service.CreateAsync(request, TestContext.Current.CancellationToken);

        Assert.False(second.IsVerified);
        Assert.Equal(SafetyBackupFailureCodes.BackupFailed, second.FailureCode);
        Assert.Null(second.Artifact);
        Assert.Equal(
            artifactBefore,
            await File.ReadAllBytesAsync(artifactPath, TestContext.Current.CancellationToken));
        Assert.Equal(
            manifestBefore,
            await File.ReadAllBytesAsync(manifestPath, TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateFiles(fixture.BackupDirectory, "*.sqlite"));
        Assert.Single(Directory.EnumerateFiles(fixture.BackupDirectory, "*.json"));
    }

    [Fact]
    public async Task CapacityAndDatabaseDurabilityFailuresReturnStableFailureWithoutVerifiedArtifact()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var capacityFailure = await new SafetyBackupService(
                new SafetyBackupTestHooks(OverrideAvailableFreeBytes: _ => 0))
            .CreateAsync(
                fixture.CreateRequest(Guid.CreateVersion7()),
                TestContext.Current.CancellationToken);

        Assert.False(capacityFailure.IsVerified);
        Assert.Equal(SafetyBackupFailureCodes.BackupFailed, capacityFailure.FailureCode);
        Assert.Empty(Directory.EnumerateFiles(fixture.BackupDirectory, "migration-*.sqlite"));
        Assert.Empty(Directory.EnumerateFiles(fixture.BackupDirectory, "migration-*.json"));

        var durabilityFailure = await new SafetyBackupService(
                new SafetyBackupTestHooks(
                    BeforeDatabaseWriteThrough: static () => throw new IOException("test-only")))
            .CreateAsync(
                fixture.CreateRequest(Guid.CreateVersion7()),
                TestContext.Current.CancellationToken);

        Assert.False(durabilityFailure.IsVerified);
        Assert.Equal(SafetyBackupFailureCodes.BackupFailed, durabilityFailure.FailureCode);
        Assert.Empty(Directory.EnumerateFiles(fixture.BackupDirectory, "migration-*.sqlite"));
        Assert.Empty(Directory.EnumerateFiles(fixture.BackupDirectory, "migration-*.json"));
        Assert.NotEmpty(Directory.EnumerateFiles(fixture.BackupDirectory, "*.sqlite.partial"));
    }

    [Fact]
    public async Task ManifestDurabilityFailureLeavesDatabaseUnverified()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var result = await new SafetyBackupService(
                new SafetyBackupTestHooks(
                    BeforeManifestWriteThrough: static () => throw new IOException("test-only")))
            .CreateAsync(
                fixture.CreateRequest(Guid.CreateVersion7()),
                TestContext.Current.CancellationToken);

        Assert.False(result.IsVerified);
        Assert.Equal(SafetyBackupFailureCodes.BackupFailed, result.FailureCode);
        Assert.Empty(Directory.EnumerateFiles(fixture.BackupDirectory, "migration-*.json"));
        Assert.Single(Directory.EnumerateFiles(fixture.BackupDirectory, "migration-*.sqlite"));
        Assert.NotEmpty(Directory.EnumerateFiles(fixture.BackupDirectory, "*.json.partial"));
    }

    [Fact]
    public async Task ActiveDatabaseAndProfileBoundaryAreNeverUsedAsBackupTargets()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var backupDirectoryPath = Path.Combine(fixture.ProfileRoot, "backups");
        await File.WriteAllTextAsync(
            backupDirectoryPath,
            "not-a-directory",
            TestContext.Current.CancellationToken);

        var result = await new SafetyBackupService().CreateAsync(
            fixture.CreateRequest(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsVerified);
        Assert.Equal(SafetyBackupFailureCodes.PathInvalid, result.FailureCode);
        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.True(File.Exists(backupDirectoryPath));
        Assert.Empty(Directory.EnumerateFiles(fixture.ProfileRoot, "migration-*.sqlite", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SourceChangeAfterSnapshotIsReportedAndPartialCannotBeVerified()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var result = await new SafetyBackupService(
                new SafetyBackupTestHooks(
                    BeforeDatabaseWriteThrough: () => File.AppendAllText(
                        fixture.DatabasePath,
                        "source-changed-by-isolated-test")))
            .CreateAsync(
                fixture.CreateRequest(Guid.CreateVersion7()),
                TestContext.Current.CancellationToken);

        Assert.False(result.IsVerified);
        Assert.Equal(SafetyBackupFailureCodes.SourceChanged, result.FailureCode);
        Assert.Empty(Directory.EnumerateFiles(fixture.BackupDirectory, "migration-*.sqlite"));
        Assert.Empty(Directory.EnumerateFiles(fixture.BackupDirectory, "migration-*.json"));
        Assert.NotEmpty(Directory.EnumerateFiles(fixture.BackupDirectory, "*.sqlite.partial"));
    }

    [Fact]
    public async Task InvalidSourceReturnsSourceInvalidWithoutCreatingAUsableBackup()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.DatabasePath,
            "not sqlite",
            TestContext.Current.CancellationToken);

        var result = await new SafetyBackupService().CreateAsync(
            fixture.CreateRequest(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsVerified);
        Assert.Equal(SafetyBackupFailureCodes.SourceInvalid, result.FailureCode);
        Assert.Empty(Directory.EnumerateFiles(fixture.BackupDirectory, "migration-*.sqlite"));
        Assert.Empty(Directory.EnumerateFiles(fixture.BackupDirectory, "migration-*.json"));
    }

    private static async Task AssertHealthyReadOnlyArtifactAsync(string artifactPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = artifactPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA query_only = ON;");
        Assert.Equal(1L, await ExecuteScalarLongAsync(connection, "PRAGMA query_only;"));
        Assert.Equal(1L, await ExecuteScalarLongAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal("ok", await ExecuteScalarStringAsync(connection, "PRAGMA integrity_check;"));

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<long> CountRowsAsync(string databasePath, string tableName)
    {
        Assert.Equal("tasks", tableName);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false
        };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return await ExecuteScalarLongAsync(connection, "SELECT COUNT(*) FROM tasks;");
    }

    private static async Task<FileFingerprint> ReadFileFingerprintAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken);
        return new FileFingerprint(stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ExecuteScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ExecuteScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull
            ? null
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record FileFingerprint(long ByteLength, string Sha256);

    private sealed class BackupFixture : IAsyncDisposable
    {
        private BackupFixture(
            string root,
            string dataRoot,
            string profileRoot,
            string databasePath,
            string profileScope,
            string[] sourceSchema)
        {
            Root = root;
            DataRoot = dataRoot;
            ProfileRoot = profileRoot;
            DatabasePath = databasePath;
            ProfileScope = profileScope;
            SourceSchema = sourceSchema;
        }

        public string Root { get; }

        public string DataRoot { get; }

        public string ProfileRoot { get; }

        public string DatabasePath { get; }

        public string ProfileScope { get; }

        public string[] SourceSchema { get; }

        public string[] TargetSchema { get; } = ["20260901000000_P275Target"];

        public string BackupDirectory => Path.Combine(ProfileRoot, "backups");

        public string SecretTaskTitle { get; } = "BACKUP-SECRET-TASK-TITLE-DO-NOT-LEAK";

        public static async ValueTask<BackupFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ReminNote-P275-01-" + Guid.CreateVersion7().ToString("N"));
            var dataRoot = Path.Combine(root, "data");
            var profileRoot = dataRoot;
            var databasePath = Path.Combine(profileRoot, "reminnote.sqlite");
            Directory.CreateDirectory(profileRoot);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true,
                Pooling = false
            };

            try
            {
                string[] sourceSchema;
                await using (var connection = new SqliteConnection(builder.ConnectionString))
                {
                    await connection.OpenAsync(TestContext.Current.CancellationToken);
                    await using (var context = ReminNoteDatabase.CreateContext(connection))
                    {
                        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
                        var now = Instant.FromUtc(2026, 8, 31, 12, 0);
                        context.Tasks.Add(new TaskEntity
                        {
                            Id = Guid.CreateVersion7(),
                            Title = "BACKUP-SECRET-TASK-TITLE-DO-NOT-LEAK",
                            TimeType = TaskTimeType.ANYTIME,
                            LocalDate = new LocalDate(2026, 8, 31),
                            CreatedAt = now,
                            UpdatedAt = now
                        });
                        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
                    }

                    await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;");
                    sourceSchema = await ReadMigrationIdsAsync(connection);
                }

                var profileScope = ProtocolProfileScope.Derive(
                    "S-1-5-21-100-200-300-400",
                    Path.GetFullPath(databasePath));
                return new BackupFixture(
                    root,
                    dataRoot,
                    profileRoot,
                    databasePath,
                    profileScope,
                    sourceSchema);
            }
            catch
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                throw;
            }
        }

        public SafetyBackupRequest CreateRequest(Guid runId) => new(
            DataRoot,
            ProfileRoot,
            DatabasePath,
            ProfileScope,
            TargetSchema,
            runId,
            FixedCreatedAtUtc);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }

        private static async Task<string[]> ReadMigrationIdsAsync(SqliteConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            var values = new List<string>();
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                values.Add(reader.GetString(0));
            }

            return values.ToArray();
        }
    }
}
