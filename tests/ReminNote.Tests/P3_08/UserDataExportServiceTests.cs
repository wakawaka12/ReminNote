using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ReminNote.Core.Protocol;
using ReminNote.Core.Reminders.Domain;
using ReminNote.Core.Reminders.Export;
using ReminNote.Infrastructure.Persistence;
using ReminNote.Infrastructure.Persistence.Export;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence.Reminders;

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

    [Fact]
    public async Task RestorePreservesSupersededReferenceToPendingScheduleAndDefersItExplicitly()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReminNote-P308-n02-source-" + Guid.NewGuid().ToString("N"));
        var candidateRoot = Path.Combine(Path.GetTempPath(), "ReminNote-P308-n02-candidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(candidateRoot);
        var databasePath = Path.Combine(root, "reminnote.sqlite");
        const string profileScope = "p1-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var taskId = Guid.CreateVersion7();
        var createdAt = Instant.FromUtc(2026, 9, 5, 0, 0);
        var triggerAt = Instant.FromUtc(2026, 9, 5, 1, 0);
        var originalId = Guid.CreateVersion7();
        var replacementId = Guid.CreateVersion7();

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                ForeignKeys = true,
                Pooling = false
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

                var rule = ReminderRule.CreateForTask(
                    taskId,
                    ReminderPurpose.TASK_START,
                    ReminderTiming.Relative(ReminderAnchor.TASK_TIME, 0),
                    ReminderPriority.NORMAL,
                    pinned: false,
                    RepeatPolicy.Disabled,
                    WakePolicy.DEFAULT,
                    enabled: true,
                    createdAt);
                var logicalId = LogicalReminderId.New();
                var original = ReminderSchedule.CreateFromRule(
                    rule,
                    logicalId,
                    scheduleRevision: 1,
                    triggerAt,
                    createdAt,
                    "Asia/Shanghai",
                    ReminderScheduleId.From(originalId));
                var replacement = ReminderSchedule.CreateFromRule(
                    rule,
                    logicalId,
                    scheduleRevision: 2,
                    triggerAt.Plus(Duration.FromMinutes(5)),
                    createdAt,
                    "Asia/Shanghai",
                    ReminderScheduleId.From(replacementId));
                original.Supersede(replacement.Id, ScheduleStateReason.RULE_REBUILT, triggerAt);

                context.Tasks.Add(new TaskEntity
                {
                    Id = taskId,
                    Title = "N02 自引用恢复测试",
                    TimeType = ReminNote.Core.Tasks.TaskTimeType.ANYTIME,
                    LocalDate = new LocalDate(2026, 9, 5),
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt,
                    SortOrder = 0
                });
                context.ReminderRules.Add(ReminderRuleEntity.FromDomain(rule));
                context.ReminderSchedules.AddRange(
                    ReminderScheduleEntity.FromDomain(original),
                    ReminderScheduleEntity.FromDomain(replacement));
                var revision = await context.RevisionStates.SingleAsync(
                    value => value.ProfileScope == profileScope,
                    TestContext.Current.CancellationToken);
                revision.CurrentRevision = 9;
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var artifact = await UserDataExportService.CreateAsync(
                    databasePath,
                    root,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var artifactPath = Path.Combine(root, "exports", "n02.json");
            await UserDataExportService.WriteArtifactAsync(
                    artifact,
                    artifactPath,
                    databasePath,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

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

            var candidateConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = restored.CandidateDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true,
                Pooling = false
            }.ToString();
            await using var candidateConnection = new SqliteConnection(candidateConnectionString);
            await candidateConnection.OpenAsync(TestContext.Current.CancellationToken);
            await using var candidateContext = ReminNoteDatabase.CreateContext(candidateConnection);
            var schedules = await candidateContext.ReminderSchedules
                .AsNoTracking()
                .OrderBy(value => value.ScheduleRevision)
                .ToArrayAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, schedules.Length);
            Assert.Equal(ScheduleState.SUPERSEDED, schedules[0].State);
            Assert.Equal(replacementId, schedules[0].ReplacementScheduleId);
            Assert.Equal(ScheduleState.CANCELLED, schedules[1].State);
            Assert.Equal(ScheduleStateReason.RECOVERY_OBSOLETE, schedules[1].TerminalReason);
            Assert.Equal(replacementId, schedules[1].Id);
            Assert.Equal(9, (await candidateContext.RevisionStates.SingleAsync(TestContext.Current.CancellationToken)).CurrentRevision);

            await using var foreignKeyCommand = candidateConnection.CreateCommand();
            foreignKeyCommand.CommandText = "PRAGMA foreign_key_check;";
            Assert.Null(await foreignKeyCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));

            var validation = await UserDataCandidateService.ValidateAsync(
                    Path.GetDirectoryName(restored.CandidateDatabasePath!)!,
                    databasePath,
                    profileScope,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.True(validation.IsValid, validation.FailureCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(root);
            TryDelete(candidateRoot);
        }
    }

    [Fact]
    public async Task CandidateValidationRejectsCountPreservingContentMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), "ReminNote-P308-n03-source-" + Guid.NewGuid().ToString("N"));
        var candidateRoot = Path.Combine(Path.GetTempPath(), "ReminNote-P308-n03-candidate-" + Guid.NewGuid().ToString("N"));
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
                ForeignKeys = true,
                Pooling = false
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
                    Title = "N03 原始标题",
                    TimeType = ReminNote.Core.Tasks.TaskTimeType.ANYTIME,
                    LocalDate = new LocalDate(2026, 9, 5),
                    CreatedAt = Instant.FromUtc(2026, 9, 5, 0, 0),
                    UpdatedAt = Instant.FromUtc(2026, 9, 5, 0, 0),
                    SortOrder = 0
                });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var artifact = await UserDataExportService.CreateAsync(
                    databasePath,
                    root,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var artifactPath = Path.Combine(root, "exports", "n03.json");
            await UserDataExportService.WriteArtifactAsync(
                    artifact,
                    artifactPath,
                    databasePath,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
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

            var mutationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = restored.CandidateDatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                ForeignKeys = true,
                Pooling = false
            }.ToString();
            await using (var mutationConnection = new SqliteConnection(mutationConnectionString))
            {
                await mutationConnection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = mutationConnection.CreateCommand();
                command.CommandText = "UPDATE tasks SET title = $title WHERE id = $id;";
                command.Parameters.AddWithValue("$title", "N03 被篡改标题");
                command.Parameters.AddWithValue("$id", taskId.ToString("D"));
                Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            }

            var validation = await UserDataCandidateService.ValidateAsync(
                    Path.GetDirectoryName(restored.CandidateDatabasePath!)!,
                    databasePath,
                    profileScope,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.False(validation.IsValid);
            Assert.Equal("candidate.content_mismatch", validation.FailureCode);
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
