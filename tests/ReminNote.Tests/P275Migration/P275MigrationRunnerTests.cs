using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Tests.P275Migration;

public sealed class P275MigrationRunnerTests
{
    [Fact]
    public async Task LockWaitIsBoundedAndDoesNotTouchActiveOrCreateCandidate()
    {
        await using var fixture = await P275Fixture.CreateAsync();
        var lockProvider = new P275FileMigrationLockProvider();
        var heldLease = await lockProvider.AcquireAsync(
            new P275MigrationLockRequest(fixture.Paths, fixture.RunId, TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken);
        Assert.NotNull(heldLease);

        var backupProvider = new CountingBackupProvider(fixture.CreateBackup());
        var applier = new CountingApplier();
        var runner = fixture.CreateRunner(
            sourceReader: new SequenceSourceReader(fixture.Source),
            backupProvider,
            lockProvider,
            applier);

        var result = await runner.RunAsync(
            fixture.CreateRequest(TimeSpan.FromMilliseconds(150)),
            TestContext.Current.CancellationToken);

        await heldLease.DisposeAsync();
        Assert.False(result.Ready);
        Assert.Equal(P275MigrationFailureCodes.Locked, result.FailureCode);
        Assert.False(result.Writable);
        Assert.Equal(0, backupProvider.CallCount);
        Assert.Equal(0, applier.CallCount);
        Assert.Equal(
            "active",
            await File.ReadAllTextAsync(
                fixture.Paths.ActiveDatabasePath,
                TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(fixture.Paths.StagingDirectory + "\\" + fixture.RunId));
    }

    [Fact]
    public async Task ForwardMigrationUsesCandidateAndPromotesOnlyAfterVerification()
    {
        await using var fixture = await P275Fixture.CreateAsync();
        var applier = new CountingApplier();
        var verifier = new RecordingVerifier(fixture.Paths.ActiveDatabasePath);
        var runner = fixture.CreateRunner(
            sourceReader: new SequenceSourceReader(fixture.Source),
            backupProvider: new CountingBackupProvider(fixture.CreateBackup()),
            migrationApplier: applier,
            candidateVerifier: verifier,
            promoter: new P275AtomicFilePromoter());

        var result = await runner.RunAsync(
            fixture.CreateRequest(TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);

        Assert.True(result.Ready);
        Assert.True(result.Writable);
        Assert.True(result.Promoted);
        Assert.Null(result.FailureCode);
        Assert.Equal(1, applier.CallCount);
        Assert.Equal(
            "candidate-after-migration",
            await File.ReadAllTextAsync(
                fixture.Paths.ActiveDatabasePath,
                TestContext.Current.CancellationToken));
        Assert.NotNull(result.HistoryDirectoryPath);
        Assert.Equal(
            "active",
            await File.ReadAllTextAsync(
                Path.Combine(result.HistoryDirectoryPath!, "active.sqlite"),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            [P275VerificationPhase.Candidate, P275VerificationPhase.PostPromote],
            verifier.Phases);
        Assert.Equal(P275MigrationState.Ready, fixture.StatePort.Last!.State);
    }

    [Fact]
    public async Task SourceFingerprintChangeBlocksPromoteAndPreservesCandidateEvidence()
    {
        await using var fixture = await P275Fixture.CreateAsync();
        var sourceReader = new SequenceSourceReader(
            fixture.Source,
            new P275SourceInventory(
                exists: true,
                fixture.Source.AppliedMigrations,
                new P275SourceFingerprint("source-changed")));
        var promoter = new CountingPromoter();
        var runner = fixture.CreateRunner(
            sourceReader,
            new CountingBackupProvider(fixture.CreateBackup()),
            candidateVerifier: new RecordingVerifier(fixture.Paths.ActiveDatabasePath),
            promoter: promoter);

        var result = await runner.RunAsync(
            fixture.CreateRequest(TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);

        Assert.False(result.Ready);
        Assert.False(result.Writable);
        Assert.Equal(P275MigrationFailureCodes.SourceChanged, result.FailureCode);
        Assert.Equal(0, promoter.CallCount);
        Assert.Equal(
            "active",
            await File.ReadAllTextAsync(
                fixture.Paths.ActiveDatabasePath,
                TestContext.Current.CancellationToken));
        Assert.NotNull(result.CandidateDatabasePath);
        Assert.True(File.Exists(result.CandidateDatabasePath));
        Assert.StartsWith(
            fixture.Paths.FailedDirectory,
            result.CandidateDatabasePath!,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        Assert.Equal(P275MigrationState.RecoveryRequired, fixture.StatePort.Last!.State);
    }

    [Fact]
    public async Task CandidateApplyFailureIsFailClosedAndRetainsFailedCandidate()
    {
        await using var fixture = await P275Fixture.CreateAsync();
        var applier = new CountingApplier
        {
            Failure = new InvalidOperationException("test-only apply failure")
        };
        var promoter = new CountingPromoter();
        var runner = fixture.CreateRunner(
            new SequenceSourceReader(fixture.Source),
            new CountingBackupProvider(fixture.CreateBackup()),
            migrationApplier: applier,
            promoter: promoter);

        var result = await runner.RunAsync(
            fixture.CreateRequest(TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);

        Assert.False(result.Ready);
        Assert.Equal(P275MigrationFailureCodes.ApplyFailed, result.FailureCode);
        Assert.Equal(0, promoter.CallCount);
        Assert.Equal(
            "active",
            await File.ReadAllTextAsync(
                fixture.Paths.ActiveDatabasePath,
                TestContext.Current.CancellationToken));
        Assert.NotNull(result.CandidateDatabasePath);
        Assert.True(File.Exists(result.CandidateDatabasePath));
        Assert.StartsWith(
            fixture.Paths.FailedDirectory,
            result.CandidateDatabasePath!,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviousPromotionMarkerBecomesUnknownBeforeAnyNewAttempt()
    {
        await using var fixture = await P275Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        fixture.StatePort.Previous = P275MigrationStateReadResult.Valid(
            new P275MigrationStateMarker(
                P275MigrationState.PromotionInProgress,
                Guid.Parse(fixture.RunId),
                "p1-" + new string('a', 64),
                fixture.Source.AppliedMigrations,
                CreatePlan().ApprovedTargetMigrations,
                backupArtifact: null,
                backupSha256: null,
                candidateArtifact: null,
                startedAtUtc: now,
                updatedAtUtc: now,
                failureCode: null,
                retryable: false,
                nextAction: P275MigrationNextActions.None,
                lastKnownGoodSchema: fixture.Source.AppliedMigrations));
        var backupProvider = new CountingBackupProvider(fixture.CreateBackup());
        var applier = new CountingApplier();
        var runner = fixture.CreateRunner(
            new SequenceSourceReader(fixture.Source),
            backupProvider,
            migrationApplier: applier);

        var result = await runner.RunAsync(
            fixture.CreateRequest(TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);

        Assert.False(result.Ready);
        Assert.Equal(P275MigrationState.PromotionUnknown, result.State);
        Assert.Equal(P275MigrationFailureCodes.PromoteUnknown, result.FailureCode);
        Assert.Equal(0, backupProvider.CallCount);
        Assert.Equal(0, applier.CallCount);
        Assert.Equal(P275MigrationState.PromotionUnknown, fixture.StatePort.Last!.State);
        Assert.Equal(
            P275MigrationNextActions.RestoreBackup,
            fixture.StatePort.Last.NextAction);
    }

    [Fact]
    public async Task FailedAttemptRequiresExplicitRetryBeforeRunningAgain()
    {
        await using var fixture = await P275Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        fixture.StatePort.Previous = P275MigrationStateReadResult.Valid(
            new P275MigrationStateMarker(
                P275MigrationState.MigrationFailed,
                Guid.Parse(fixture.RunId),
                "p1-" + new string('a', 64),
                fixture.Source.AppliedMigrations,
                CreatePlan().ApprovedTargetMigrations,
                backupArtifact: null,
                backupSha256: null,
                candidateArtifact: null,
                startedAtUtc: now,
                updatedAtUtc: now,
                failureCode: P275MigrationFailureCodes.ApplyFailed,
                retryable: true,
                nextAction: P275MigrationNextActions.Retry,
                lastKnownGoodSchema: fixture.Source.AppliedMigrations));
        var backupProvider = new CountingBackupProvider(fixture.CreateBackup());
        var applier = new CountingApplier();
        var runner = fixture.CreateRunner(
            new SequenceSourceReader(fixture.Source),
            backupProvider,
            migrationApplier: applier,
            promoter: new P275AtomicFilePromoter());

        var blocked = await runner.RunAsync(
            fixture.CreateRequest(TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);

        Assert.False(blocked.Ready);
        Assert.Equal(P275MigrationState.MigrationFailed, blocked.State);
        Assert.Equal(P275MigrationFailureCodes.ApplyFailed, blocked.FailureCode);
        Assert.Equal(0, backupProvider.CallCount);
        Assert.Equal(0, applier.CallCount);

        var retried = await runner.RunAsync(
            fixture.CreateRequest(TimeSpan.FromSeconds(2)) with
            {
                RunId = Guid.NewGuid().ToString("D"),
                AllowRetry = true
            },
            TestContext.Current.CancellationToken);

        Assert.True(retried.Ready);
        Assert.True(retried.Writable);
        Assert.Equal(1, backupProvider.CallCount);
        Assert.Equal(1, applier.CallCount);
    }

    [Fact]
    public async Task RetryableSourceChangeFailureCanBeRetriedExplicitly()
    {
        await using var fixture = await P275Fixture.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        fixture.StatePort.Previous = P275MigrationStateReadResult.Valid(
            new P275MigrationStateMarker(
                P275MigrationState.RecoveryRequired,
                Guid.Parse(fixture.RunId),
                "p1-" + new string('a', 64),
                fixture.Source.AppliedMigrations,
                CreatePlan().ApprovedTargetMigrations,
                backupArtifact: null,
                backupSha256: null,
                candidateArtifact: null,
                startedAtUtc: now,
                updatedAtUtc: now,
                failureCode: P275MigrationFailureCodes.SourceChanged,
                retryable: true,
                nextAction: P275MigrationNextActions.Retry,
                lastKnownGoodSchema: fixture.Source.AppliedMigrations));
        var backupProvider = new CountingBackupProvider(fixture.CreateBackup());
        var applier = new CountingApplier();
        var runner = fixture.CreateRunner(
            new SequenceSourceReader(fixture.Source),
            backupProvider,
            migrationApplier: applier,
            promoter: new P275AtomicFilePromoter());

        var blocked = await runner.RunAsync(
            fixture.CreateRequest(TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);

        Assert.Equal(P275MigrationFailureCodes.SourceChanged, blocked.FailureCode);
        Assert.Equal(0, applier.CallCount);

        var retried = await runner.RunAsync(
            fixture.CreateRequest(TimeSpan.FromSeconds(2)) with
            {
                RunId = Guid.NewGuid().ToString("D"),
                AllowRetry = true
            },
            TestContext.Current.CancellationToken);

        Assert.True(retried.Ready);
        Assert.Equal(1, applier.CallCount);
    }

    [Fact]
    public async Task AlreadyAtTargetStillNeedsVerificationBeforeReady()
    {
        await using var fixture = await P275Fixture.CreateAsync();
        var verifier = new RecordingVerifier(fixture.Paths.ActiveDatabasePath);
        var backupProvider = new CountingBackupProvider(null);
        var runner = fixture.CreateRunner(
            new SequenceSourceReader(fixture.TargetSource),
            backupProvider,
            candidateVerifier: verifier,
            promoter: new CountingPromoter());

        var result = await runner.RunAsync(
            fixture.CreateRequest(TimeSpan.FromSeconds(2)) with
            {
                Plan = new P275MigrationPlan(
                    ["20260101_Initial"],
                    ["20260101_Initial", "20260201_Target"])
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Ready);
        Assert.True(result.Writable);
        Assert.False(result.Promoted);
        Assert.Equal(0, backupProvider.CallCount);
        Assert.Equal([P275VerificationPhase.AlreadyReady], verifier.Phases);
        Assert.Equal(P275MigrationState.Ready, fixture.StatePort.Last!.State);
    }

    [Fact]
    public async Task StartupGateNeverReportsWritableForNonReadyMigration()
    {
        await using var fixture = await P275Fixture.CreateAsync();
        var migration = P275MigrationResult.Failure(
            fixture.RunId,
            P275MigrationState.VerifyFailed,
            P275MigrationFailureCodes.VerifyFailed);
        var gate = new P275StartupGate(new FixedMigrationRunner(migration));

        var result = await gate.OpenAsync(
            fixture.CreateRequest(TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);

        Assert.False(result.Ready);
        Assert.False(result.Writable);
        Assert.Same(migration, result.Migration);
    }

    [Fact]
    public async Task AtomicPromoteCarriesActiveSidecarsIntoTheOriginalGeneration()
    {
        await using var fixture = await P275Fixture.CreateAsync();
        var candidateFiles = fixture.Paths.CreateCandidatePaths(fixture.RunId);
        Directory.CreateDirectory(candidateFiles.StagingDirectory);
        var backup = fixture.CreateBackup();
        var candidate = new P275Candidate(fixture.Paths, candidateFiles, backup);
        await File.WriteAllTextAsync(
            candidateFiles.CandidateDatabasePath,
            "candidate",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            fixture.Paths.ActiveDatabasePath + "-wal",
            "active-wal",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            fixture.Paths.ActiveDatabasePath + "-shm",
            "active-shm",
            TestContext.Current.CancellationToken);

        var promotion = await new P275AtomicFilePromoter().PromoteAsync(
            new P275PromotionRequest(
                fixture.Paths,
                candidate,
                fixture.Source,
                "p275-test-scope"),
            TestContext.Current.CancellationToken);

        Assert.Equal(P275PromotionOutcome.Succeeded, promotion.Outcome);
        Assert.Equal(
            "candidate",
            await File.ReadAllTextAsync(
                fixture.Paths.ActiveDatabasePath,
                TestContext.Current.CancellationToken));
        Assert.False(File.Exists(fixture.Paths.ActiveDatabasePath + "-wal"));
        Assert.False(File.Exists(fixture.Paths.ActiveDatabasePath + "-shm"));
        Assert.NotNull(promotion.HistoryDirectoryPath);
        Assert.Equal("active", await File.ReadAllTextAsync(
            Path.Combine(promotion.HistoryDirectoryPath!, "active.sqlite"),
            TestContext.Current.CancellationToken));
        Assert.Equal("active-wal", await File.ReadAllTextAsync(
            Path.Combine(promotion.HistoryDirectoryPath!, "active.sqlite-wal"),
            TestContext.Current.CancellationToken));
        Assert.Equal("active-shm", await File.ReadAllTextAsync(
            Path.Combine(promotion.HistoryDirectoryPath!, "active.sqlite-shm"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CandidateFinalizerReopensOnlyCandidateAndLeavesItSidecarFree()
    {
        await using var fixture = await P275Fixture.CreateAsync();
        var candidateFiles = fixture.Paths.CreateCandidatePaths(fixture.RunId);
        Directory.CreateDirectory(candidateFiles.StagingDirectory);
        var backup = fixture.CreateBackup();
        var candidate = new P275Candidate(fixture.Paths, candidateFiles, backup);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = candidateFiles.CandidateDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using (var journal = connection.CreateCommand())
            {
                journal.CommandText = "PRAGMA journal_mode = WAL;";
                await journal.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE candidate_data (value TEXT NOT NULL);";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO candidate_data(value) VALUES ('candidate');";
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        Assert.True(File.Exists(candidateFiles.CandidateDatabasePath), candidateFiles.CandidateDatabasePath);
        var candidateAttributes = new FileInfo(candidateFiles.CandidateDatabasePath).Attributes;
        Assert.False(
            (candidateAttributes & FileAttributes.ReparsePoint) != 0,
            candidateAttributes.ToString());

        var result = await new P275CandidateSidecarFinalizer().FinalizeAsync(
            candidate,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid, result.FailureCode);
        Assert.True(File.Exists(candidateFiles.CandidateDatabasePath));
        Assert.False(File.Exists(candidateFiles.CandidateDatabasePath + "-wal"));
        Assert.False(File.Exists(candidateFiles.CandidateDatabasePath + "-shm"));
        Assert.False(File.Exists(candidateFiles.CandidateDatabasePath + "-journal"));
    }

    private sealed class P275Fixture : IAsyncDisposable
    {
        private P275Fixture(string root)
        {
            Root = root;
            Paths = new P275ProfilePaths(root, "default");
            RunId = Guid.NewGuid().ToString("D");
            Source = new P275SourceInventory(
                exists: true,
                ["20260101_Initial"],
                new P275SourceFingerprint("source-v1"));
            TargetSource = new P275SourceInventory(
                exists: true,
                ["20260101_Initial", "20260201_Target"],
                new P275SourceFingerprint("source-v2"));
            StatePort = new RecordingStatePort();
        }

        public string Root { get; }

        public P275ProfilePaths Paths { get; }

        public string RunId { get; }

        public P275SourceInventory Source { get; }

        public P275SourceInventory TargetSource { get; }

        public RecordingStatePort StatePort { get; }

        public static ValueTask<P275Fixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ReminNote-P275-02-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new P275Fixture(root);
            Directory.CreateDirectory(fixture.Paths.ProfileRoot);
            File.WriteAllText(fixture.Paths.ActiveDatabasePath, "active");
            return ValueTask.FromResult(fixture);
        }

        public P275MigrationRequest CreateRequest(TimeSpan lockTimeout) =>
            new(Paths, "p275-test-scope", RunId, CreatePlan(), lockTimeout);

        public P275VerifiedSafetyBackup CreateBackup()
        {
            Directory.CreateDirectory(Paths.BackupsDirectory);
            var artifactId = "migration-initial-target-" + RunId + ".sqlite";
            var artifactPath = Path.Combine(Paths.BackupsDirectory, artifactId);
            var bytes = Encoding.UTF8.GetBytes("backup");
            File.WriteAllBytes(artifactPath, bytes);
            return new P275VerifiedSafetyBackup(
                artifactId,
                artifactPath,
                sourceIsEmpty: false,
                Source.AppliedMigrations,
                Source.Fingerprint,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        public P275MigrationRunner CreateRunner(
            IP275ActiveSourceReader sourceReader,
            IP275SafetyBackupProvider backupProvider,
            IP275MigrationLockProvider? lockProvider = null,
            IP275ForwardMigrationApplier? migrationApplier = null,
            IP275CandidateFinalizer? candidateFinalizer = null,
            IP275CandidateVerifier? candidateVerifier = null,
            IP275AtomicPromoter? promoter = null) =>
            new(
                sourceReader,
                backupProvider,
                new TestWriterQuiescence(),
                lockProvider ?? new P275FileMigrationLockProvider(),
                migrationApplier ?? new CountingApplier(),
                candidateFinalizer ?? new AlwaysValidFinalizer(),
                candidateVerifier ?? new AlwaysValidVerifier(),
                promoter ?? new CountingPromoter(),
                StatePort);

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A failed test must not turn cleanup into a destructive retry
                // against an unrelated path; the root is this fixture's UUID.
            }

            return ValueTask.CompletedTask;
        }
    }

    private static P275MigrationPlan CreatePlan() =>
        new(["20260101_Initial"], ["20260101_Initial", "20260201_Target"]);

    private sealed class SequenceSourceReader : IP275ActiveSourceReader
    {
        private readonly Queue<P275SourceInventory> values;

        public SequenceSourceReader(params P275SourceInventory[] values)
        {
            this.values = new Queue<P275SourceInventory>(values);
        }

        public ValueTask<P275SourceInventory> ReadAsync(
            P275ProfilePaths paths,
            string profileScope,
            CancellationToken cancellationToken = default)
        {
            _ = paths;
            _ = profileScope;
            cancellationToken.ThrowIfCancellationRequested();
            var value = values.Count > 1 ? values.Dequeue() : values.Peek();
            return ValueTask.FromResult(value);
        }
    }

    private sealed class CountingBackupProvider : IP275SafetyBackupProvider
    {
        private readonly P275VerifiedSafetyBackup? backup;

        public CountingBackupProvider(P275VerifiedSafetyBackup? backup)
        {
            this.backup = backup;
        }

        public int CallCount { get; private set; }

        public ValueTask<P275VerifiedSafetyBackup> CreateVerifiedBackupAsync(
            P275SafetyBackupRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return backup is null
                ? throw new InvalidOperationException("test backup provider was not expected to run")
                : ValueTask.FromResult(backup);
        }
    }

    private sealed class CountingApplier : IP275ForwardMigrationApplier
    {
        public int CallCount { get; private set; }

        public Exception? Failure { get; init; }

        public async ValueTask<P275CandidateApplyResult> ApplyAsync(
            P275Candidate candidate,
            P275MigrationPlan plan,
            string profileScope,
            CancellationToken cancellationToken = default)
        {
            _ = profileScope;
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (Failure is not null)
            {
                throw Failure;
            }

            await File.WriteAllTextAsync(
                    candidate.Files.CandidateDatabasePath,
                    "candidate-after-migration",
                    cancellationToken)
                .ConfigureAwait(false);
            return new P275CandidateApplyResult(plan.ApprovedTargetMigrations);
        }
    }

    private sealed class AlwaysValidFinalizer : IP275CandidateFinalizer
    {
        public ValueTask<P275CandidateFinalizationResult> FinalizeAsync(
            P275Candidate candidate,
            CancellationToken cancellationToken = default)
        {
            _ = candidate;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(P275CandidateFinalizationResult.Valid());
        }
    }

    private sealed class RecordingVerifier : IP275CandidateVerifier
    {
        private readonly string activePath;

        public RecordingVerifier(string activePath)
        {
            this.activePath = activePath;
        }

        public List<P275VerificationPhase> Phases { get; } = [];

        public async ValueTask<P275VerificationResult> VerifyAsync(
            P275VerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Phases.Add(request.Phase);
            if (request.Phase == P275VerificationPhase.Candidate)
            {
                Assert.Equal("active", await File.ReadAllTextAsync(activePath, cancellationToken));
            }

            return P275VerificationResult.Valid();
        }
    }

    private sealed class AlwaysValidVerifier : IP275CandidateVerifier
    {
        public ValueTask<P275VerificationResult> VerifyAsync(
            P275VerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(P275VerificationResult.Valid());
        }
    }

    private sealed class CountingPromoter : IP275AtomicPromoter
    {
        public int CallCount { get; private set; }

        public ValueTask<P275PromotionResult> PromoteAsync(
            P275PromotionRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(P275PromotionResult.ExplicitFailure());
        }
    }

    private sealed class TestWriterQuiescence : IP275WriterQuiescence
    {
        public ValueTask<IP275WriterQuiescenceLease> AcquireAsync(
            P275ProfilePaths paths,
            string runId,
            CancellationToken cancellationToken = default)
        {
            _ = paths;
            _ = runId;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IP275WriterQuiescenceLease>(new EmptyLease());
        }
    }

    private sealed class EmptyLease : IP275WriterQuiescenceLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingStatePort : IP275MigrationStatePort
    {
        public P275MigrationStateSnapshot? Last { get; private set; }

        public P275MigrationStateReadResult Previous { get; set; } =
            P275MigrationStateReadResult.Missing();

        public ValueTask RecordAsync(
            P275MigrationStateSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Last = snapshot;
            return ValueTask.CompletedTask;
        }

        public ValueTask<P275MigrationStateReadResult> ReadAsync(
            string expectedProfileScope,
            CancellationToken cancellationToken = default)
        {
            _ = expectedProfileScope;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Previous);
        }
    }

    private sealed class FixedMigrationRunner : IP275MigrationRunner
    {
        private readonly P275MigrationResult result;

        public FixedMigrationRunner(P275MigrationResult result)
        {
            this.result = result;
        }

        public ValueTask<P275MigrationResult> RunAsync(
            P275MigrationRequest request,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }
}
