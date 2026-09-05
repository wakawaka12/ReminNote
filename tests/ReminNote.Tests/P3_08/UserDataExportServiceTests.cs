using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Export;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.Export;
using ReminNote.Infrastructure.Persistence.P25;

namespace ReminNote.Tests.P308;

public sealed class UserDataExportServiceTests
{
    [Fact]
    public void ExportEnvelopeRoundTripsFullP304DeliveryEventStream()
    {
        var timestamp = "2026-09-05T00:00:00.000000000Z";
        var eventValue = new UserDeliveryAttemptEventExport(
            Guid.CreateVersion7().ToString("D"),
            1,
            Guid.CreateVersion7().ToString("D"),
            Guid.CreateVersion7().ToString("D"),
            Guid.CreateVersion7().ToString("D"),
            Guid.CreateVersion7().ToString("D"),
            Guid.CreateVersion7().ToString("D"),
            "TOAST",
            Guid.CreateVersion7().ToString("D"),
            Guid.CreateVersion7().ToString("D"),
            Guid.CreateVersion7().ToString("D"),
            "TASK_START",
            "NORMAL",
            false,
            1,
            timestamp,
            timestamp,
            1,
            "DELIVERED",
            "DELIVERED",
            timestamp,
            timestamp,
            null,
            null,
            false,
            3,
            Guid.CreateVersion7().ToString("D"),
            Guid.CreateVersion7().ToString("D"));
        var document = new UserDataExportDocument(
            UserDataExportContract.Schema,
            UserDataExportContract.SchemaVersion,
            GlobalRevision: 3,
            Tasks: [],
            TaskHistory: [],
            AppSettings: null,
            ReminderExportDocument.Create([], [], []),
            DeliveryAttempts: [],
            NotificationPolicyJson: null,
            Checksum: null,
            DeliveryAttemptEvents: [eventValue]);

        var parsed = UserDataExportJson.Parse(UserDataExportJson.Serialize(document));
        var result = Assert.Single(parsed.DeliveryAttemptEvents!);
        Assert.Equal(eventValue, result);
    }

    [Fact]
    public async Task ProductionExportIsReadOnlyAndConfirmedRestoreBuildsSeparateCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReminNote-P308-export-" + Guid.NewGuid().ToString("N"));
        var candidateRoot = Path.Combine(Path.GetTempPath(), "ReminNote-P308-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(candidateRoot);
        var databasePath = Path.Combine(root, "reminnote.sqlite");
        const string profileScope = "p1-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var taskId = Guid.CreateVersion7();

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                ForeignKeys = true
            }.ToString();
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var context = ReminNoteDatabase.CreateContext(connection);
                await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
                await P25StorageSchema.EnsureProfileAsync(
                        connection,
                        profileScope,
                        requireWal: true,
                        TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                context.Tasks.Add(new TaskEntity
                {
                    Id = taskId,
                    Title = "导出隔离测试",
                    TimeType = ReminNote.Core.Tasks.TaskTimeType.ANYTIME,
                    LocalDate = new LocalDate(2026, 9, 5),
                    CreatedAt = Instant.FromUtc(2026, 9, 5, 0, 0),
                    UpdatedAt = Instant.FromUtc(2026, 9, 5, 0, 0),
                    SortOrder = 0
                });
                var settings = await context.AppSettings.SingleAsync(
                    value => value.Id == 1,
                    TestContext.Current.CancellationToken);
                settings.WorkdayBoundaryMinutes = 240;
                settings.UpdatedAt = Instant.FromUtc(2026, 9, 5, 0, 0);
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var beforeHash = await HashFileAsync(databasePath);
            var artifact = await UserDataExportService.CreateAsync(
                    databasePath,
                    root,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var artifactPath = Path.Combine(root, "exports", "user-data.json");
            await UserDataExportService.WriteArtifactAsync(
                    artifact,
                    artifactPath,
                    databasePath,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            var dryRun = await UserDataRestoreService.ValidateAndStageAsync(
                    new UserDataRestoreRequest(
                        artifactPath,
                        candidateRoot,
                        databasePath,
                        profileScope,
                        DryRun: true),
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(UserDataRestoreStatus.DryRun, dryRun.Status);
            Assert.Null(dryRun.StagedArtifactPath);

            var restored = await UserDataRestoreService.ValidateAndStageAsync(
                    new UserDataRestoreRequest(
                        artifactPath,
                        candidateRoot,
                        databasePath,
                        profileScope,
                        DryRun: false,
                        Confirmed: true,
                        ImportCandidate: true),
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(UserDataRestoreStatus.Imported, restored.Status);
            Assert.NotNull(restored.CandidateDatabasePath);
            Assert.True(File.Exists(restored.CandidateDatabasePath));
            Assert.Equal(artifact.Checksum, restored.SourceChecksum);

            var afterHash = await HashFileAsync(databasePath);
            Assert.Equal(beforeHash, afterHash);

            var candidateConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = restored.CandidateDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true
            }.ToString();
            await using var candidateConnection = new SqliteConnection(candidateConnectionString);
            await candidateConnection.OpenAsync(TestContext.Current.CancellationToken);
            await using var candidateContext = ReminNoteDatabase.CreateContext(candidateConnection);
            Assert.Equal(1, await candidateContext.Tasks.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal("导出隔离测试", (await candidateContext.Tasks.SingleAsync(TestContext.Current.CancellationToken)).Title);
            await candidateContext.DisposeAsync();
            await candidateConnection.DisposeAsync();
            SqliteConnection.ClearAllPools();

            var candidateDirectory = Path.GetDirectoryName(restored.CandidateDatabasePath!)!;
            var validation = await UserDataCandidateService.ValidateAsync(
                    candidateDirectory,
                    databasePath,
                    profileScope,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.True(validation.IsValid, validation.FailureCode);
            Assert.Equal(artifact.Checksum, validation.SourceChecksum);

            var promotion = await UserDataCandidateService.PromoteAsync(
                    new UserDataCandidatePromotionRequest(
                        candidateDirectory,
                        root,
                        "default",
                        profileScope,
                        databasePath,
                        Confirmed: true),
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(UserDataCandidateStatus.Promoted, promotion.Status);
            Assert.NotNull(promotion.BackupArtifact);
            Assert.NotNull(promotion.HistoryDirectoryPath);
            Assert.True(File.Exists(databasePath));

            var promotedConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true
            }.ToString();
            await using var promotedConnection = new SqliteConnection(promotedConnectionString);
            await promotedConnection.OpenAsync(TestContext.Current.CancellationToken);
            await using var promotedContext = ReminNoteDatabase.CreateContext(promotedConnection);
            Assert.Equal("导出隔离测试", (await promotedContext.Tasks.SingleAsync(TestContext.Current.CancellationToken)).Title);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(root);
            TryDelete(candidateRoot);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(
            stream,
            TestContext.Current.CancellationToken));
    }
}
