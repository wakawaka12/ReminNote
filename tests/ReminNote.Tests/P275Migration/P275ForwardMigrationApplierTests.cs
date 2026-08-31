using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Tests.P275Migration;

public sealed class P275ForwardMigrationApplierTests
{
    [Fact]
    public async Task AppliesOnlyApprovedTargetToCandidateAndLeavesActiveUntouched()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ReminNote-P275-02-applier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            var paths = new P275ProfilePaths(root, "default");
            paths.EnsureRuntimeDirectories();
            await File.WriteAllTextAsync(
                paths.ActiveDatabasePath,
                "active-sentinel",
                cancellationToken);

            var runId = Guid.NewGuid().ToString("D");
            var candidateFiles = paths.CreateCandidatePaths(runId);
            Directory.CreateDirectory(candidateFiles.StagingDirectory);
            var candidateConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = candidateFiles.CandidateDatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true
            }.ToString();

            await using (var connection = new SqliteConnection(candidateConnectionString))
            {
                await connection.OpenAsync(cancellationToken);
                await using var context = new ReminNoteDbContext(
                    ReminNoteDbContext.CreateOptions(connection));
                await context.Database.MigrateAsync(
                    "20260828130000_P2ContinuationDeleteBoundary",
                    cancellationToken);
            }

            var plan = new P275MigrationPlan(
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
            var backup = new P275VerifiedSafetyBackup(
                "empty-source",
                artifactPath: null,
                sourceIsEmpty: true,
                sourceMigrations: [],
                new P275SourceFingerprint("test-source"),
                byteLength: 0,
                sha256: string.Empty);
            var candidate = new P275Candidate(
                paths,
                candidateFiles,
                backup);

            var result = await new P275EfForwardMigrationApplier().ApplyAsync(
                candidate,
                plan,
                "p275-test-profile",
                cancellationToken);

            Assert.Equal(plan.ApprovedTargetMigrations, result.AppliedMigrations);
            Assert.Equal(
                "active-sentinel",
                await File.ReadAllTextAsync(paths.ActiveDatabasePath, cancellationToken));

            var verifyConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = candidateFiles.CandidateDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true
            }.ToString();
            await using var verifyConnection = new SqliteConnection(verifyConnectionString);
            await verifyConnection.OpenAsync(cancellationToken);
            await using var verifyContext = new ReminNoteDbContext(
                ReminNoteDbContext.CreateOptions(verifyConnection));
            Assert.Equal(
                plan.ApprovedTargetMigrations,
                await verifyContext.Database
                    .GetAppliedMigrationsAsync(cancellationToken));
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
}
