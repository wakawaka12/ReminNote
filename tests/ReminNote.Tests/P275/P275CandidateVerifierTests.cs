using Microsoft.Data.Sqlite;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Tests.P275;

public sealed class P275CandidateVerifierTests
{
    private const string MigrationId = "20260828025922_InitialTaskSchema";
    private const string TargetMigrationId = "20260831090000_P25StorageConsistency";

    [Fact]
    public async Task CandidateVerificationChecksIntegrityForeignKeysSchemaHistoryKeyFactsAndStableHash()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CandidateFixture.CreateAsync();
        try
        {
            var expectations = await fixture.CreateExpectationsAsync().ConfigureAwait(true);
            var result = await P275CandidateVerifier.VerifyAsync(
                    new P275CandidateVerificationRequest(
                        fixture.Layout,
                        fixture.RunId,
                        expectations),
                    cancellationToken)
                .ConfigureAwait(true);

            Assert.True(result.Passed, $"{result.FailureCode}; checks={result.Checks}");
            Assert.Null(result.FailureCode);
            Assert.Equal(
                P275ProfileLayout.GetCandidateArtifact(fixture.RunId),
                result.CandidateArtifact);
            Assert.NotNull(result.Sha256);
            Assert.True(result.Checks.StandaloneFile);
            Assert.True(result.Checks.IntegrityCheck);
            Assert.True(result.Checks.ForeignKeyCheck);
            Assert.True(result.Checks.Schema);
            Assert.True(result.Checks.History);
            Assert.True(result.Checks.KeyTables);
            Assert.True(result.Checks.Hash);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task UnauthorizedSchemaObjectFailsClosedWithoutOpeningActiveDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CandidateFixture.CreateAsync();
        try
        {
            var expectations = await fixture.CreateExpectationsAsync().ConfigureAwait(true);
            await fixture.ExecuteCandidateSqlAsync(
                    "CREATE TABLE unauthorized (id INTEGER NOT NULL PRIMARY KEY);")
                .ConfigureAwait(true);

            var result = await P275CandidateVerifier.VerifyAsync(
                    new P275CandidateVerificationRequest(
                        fixture.Layout,
                        fixture.RunId,
                        expectations),
                    cancellationToken)
                .ConfigureAwait(true);

            Assert.False(result.Passed);
            Assert.Equal(P275MigrationFailureCodes.VerifyFailed, result.FailureCode);
            Assert.True(File.Exists(fixture.CandidatePath));
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ForeignKeyViolationUsesStableForeignKeyFailureCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CandidateFixture.CreateAsync();
        try
        {
            var expectations = await fixture.CreateExpectationsAsync().ConfigureAwait(true);
            await fixture.ExecuteCandidateSqlAsync(
                    """
                    PRAGMA foreign_keys = OFF;
                    INSERT INTO child (id, parent_id) VALUES (2, 999);
                    PRAGMA wal_checkpoint(TRUNCATE);
                    """)
                .ConfigureAwait(true);

            var result = await P275CandidateVerifier.VerifyAsync(
                    new P275CandidateVerificationRequest(
                        fixture.Layout,
                        fixture.RunId,
                        expectations),
                    cancellationToken)
                .ConfigureAwait(true);

            Assert.False(result.Passed);
            Assert.Equal(P275MigrationFailureCodes.ForeignKeyFailed, result.FailureCode);
            Assert.True(result.Checks.IntegrityCheck);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task HistoryAndExpectedHashMismatchNeverBecomeReady()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await CandidateFixture.CreateAsync();
        try
        {
            var expectations = await fixture.CreateExpectationsAsync().ConfigureAwait(true);
            await fixture.ExecuteCandidateSqlAsync(
                    $"UPDATE \"__EFMigrationsHistory\" SET MigrationId = '{TargetMigrationId}-tampered' " +
                    $"WHERE MigrationId = '{TargetMigrationId}';")
                .ConfigureAwait(true);

            var historyResult = await P275CandidateVerifier.VerifyAsync(
                    new P275CandidateVerificationRequest(
                        fixture.Layout,
                        fixture.RunId,
                        expectations),
                    cancellationToken)
                .ConfigureAwait(true);
            Assert.False(historyResult.Passed);
            Assert.Equal(P275MigrationFailureCodes.HistoryFailed, historyResult.FailureCode);

            var hashMismatch = new P275CandidateVerificationExpectations(
                expectations.ExpectedMigrationHistory,
                expectations.ExpectedSchema,
                expectations.KeyTables,
                expectations.ExpectedByteLength,
                new string('f', 64),
                expectations.RequireWal);
            var hashResult = await P275CandidateVerifier.VerifyAsync(
                    new P275CandidateVerificationRequest(
                        fixture.Layout,
                        fixture.RunId,
                        hashMismatch),
                    cancellationToken)
                .ConfigureAwait(true);
            Assert.False(hashResult.Passed);
            Assert.Equal(P275MigrationFailureCodes.VerifyFailed, hashResult.FailureCode);
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(true);
        }
    }

    private sealed class CandidateFixture : IAsyncDisposable
    {
        private CandidateFixture(
            string root,
            P275ProfileLayout layout,
            Guid runId,
            string candidatePath)
        {
            Root = root;
            Layout = layout;
            RunId = runId;
            CandidatePath = candidatePath;
        }

        public string Root { get; }

        public P275ProfileLayout Layout { get; }

        public Guid RunId { get; }

        public string CandidatePath { get; }

        public static async Task<CandidateFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "reminnote-p275-candidate-" + Guid.CreateVersion7().ToString("N"));
            Directory.CreateDirectory(root);
            var layout = new P275ProfileLayout(root);
            var runId = Guid.CreateVersion7();
            var candidatePath = layout.GetCandidatePath(runId);
            Directory.CreateDirectory(Path.GetDirectoryName(candidatePath)!);

            try
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = candidatePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private,
                    ForeignKeys = true
                }.ToString();
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync().ConfigureAwait(false);
                await ExecuteAsync(
                        connection,
                        """
                        PRAGMA journal_mode = WAL;
                        PRAGMA foreign_keys = ON;
                        CREATE TABLE "__EFMigrationsHistory" (
                            "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                            "ProductVersion" TEXT NOT NULL
                        );
                        CREATE TABLE parent (
                            id INTEGER NOT NULL CONSTRAINT pk_parent PRIMARY KEY
                        );
                        CREATE TABLE child (
                            id INTEGER NOT NULL CONSTRAINT pk_child PRIMARY KEY,
                            parent_id INTEGER NOT NULL,
                            CONSTRAINT fk_child_parent FOREIGN KEY (parent_id)
                                REFERENCES parent (id)
                        );
                        CREATE INDEX ix_child_parent_id ON child (parent_id);
                        INSERT INTO "__EFMigrationsHistory" (MigrationId, ProductVersion)
                        VALUES
                            ('20260828025922_InitialTaskSchema', '10.0.11'),
                            ('20260831090000_P25StorageConsistency', '10.0.11');
                        INSERT INTO parent (id) VALUES (1);
                        INSERT INTO child (id, parent_id) VALUES (1, 1);
                        """)
                    .ConfigureAwait(false);
                await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);").ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
                SqliteConnection.ClearAllPools();
                RemoveCandidateSidecars(candidatePath);
                return new CandidateFixture(root, layout, runId, candidatePath);
            }
            catch
            {
                SqliteConnection.ClearAllPools();
                DeleteRoot(root);
                throw;
            }
        }

        public async Task<P275CandidateVerificationExpectations> CreateExpectationsAsync()
        {
            var schema = new List<P275SchemaObjectExpectation>();
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = CandidatePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true
            }.ToString();
            P275KeyTableExpectation parent;
            P275KeyTableExpectation child;
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);
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
                    await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        schema.Add(P275SchemaObjectExpectation.FromSql(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2)));
                    }
                }

                parent = await P275CandidateVerifier.CaptureKeyTableExpectationAsync(
                        connection,
                        "parent",
                        ["id"])
                    .ConfigureAwait(false);
                child = await P275CandidateVerifier.CaptureKeyTableExpectationAsync(
                        connection,
                        "child",
                        ["id"])
                    .ConfigureAwait(false);
            }

            SqliteConnection.ClearAllPools();
            RemoveCandidateSidecars(CandidatePath);
            return new(
                [MigrationId, TargetMigrationId],
                new P275SchemaExpectation(schema),
                [parent, child]);
        }

        public async Task ExecuteCandidateSqlAsync(string sql)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = CandidatePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(connection, sql).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);").ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            SqliteConnection.ClearAllPools();
            RemoveCandidateSidecars(CandidatePath);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            DeleteRoot(Root);
            return ValueTask.CompletedTask;
        }

        private static async Task ExecuteAsync(SqliteConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static void RemoveCandidateSidecars(string candidatePath)
        {
            foreach (var sidecar in new[]
                     {
                         candidatePath + "-wal",
                         candidatePath + "-shm",
                         candidatePath + "-journal"
                     })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }
        }

        private static void DeleteRoot(string root)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
