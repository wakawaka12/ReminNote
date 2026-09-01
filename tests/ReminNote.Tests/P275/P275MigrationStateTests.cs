using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReminNote.Core.Protocol;
using ReminNote.Infrastructure.Persistence.Backup;
using ReminNote.Infrastructure.Persistence.P275;

namespace ReminNote.Tests.P275;

public sealed class P275MigrationStateTests
{
    [Fact]
    public void EveryFrozenStateHasAnExplicitReadinessRelation()
    {
        foreach (var state in Enum.GetValues<P275MigrationState>())
        {
            var readiness = P275MigrationStatePolicy.Evaluate(state);
            if (state == P275MigrationState.Ready)
            {
                Assert.Equal(P275RecoveryStatusMode.Ready, readiness.Mode);
                Assert.True(readiness.Ready);
                Assert.True(readiness.Writable);
                Assert.False(readiness.RecoveryRequired);
            }
            else if (state is
                     P275MigrationState.BackupFailed or
                     P275MigrationState.MigrationFailed or
                     P275MigrationState.VerifyFailed or
                     P275MigrationState.PromotionUnknown or
                     P275MigrationState.RecoveryRequired or
                     P275MigrationState.RecoveryInProgress or
                     P275MigrationState.RecoveryFailed)
            {
                Assert.Equal(P275RecoveryStatusMode.RecoveryRequired, readiness.Mode);
                Assert.False(readiness.Ready);
                Assert.False(readiness.Writable);
                Assert.True(readiness.RecoveryRequired);
            }
            else
            {
                Assert.Equal(P275RecoveryStatusMode.ReadOnly, readiness.Mode);
                Assert.False(readiness.Ready);
                Assert.False(readiness.Writable);
                Assert.False(readiness.RecoveryRequired);
            }
        }

        var unknown = P275MigrationStatePolicy.Evaluate((P275MigrationState)999);
        Assert.Equal(P275RecoveryStatusMode.RecoveryRequired, unknown.Mode);
        Assert.True(unknown.RecoveryRequired);
        Assert.False(unknown.Ready);
        Assert.False(unknown.Writable);
    }

    [Fact]
    public async Task MarkerRoundTripUsesBoundedAtomicReplacementAndDerivedReadiness()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        try
        {
            var layout = new P275ProfileLayout(root);
            var profileScope = CreateProfileScope(root);
            var store = new P275MigrationStateStore(layout);
            var first = CreateMarker(
                profileScope,
                P275MigrationState.MigrationRequired,
                failureCode: null,
                P275MigrationNextActions.Retry);
            await store.WriteAsync(first, cancellationToken);

            var second = CreateMarker(
                profileScope,
                P275MigrationState.VerifyFailed,
                P275MigrationFailureCodes.VerifyFailed,
                P275MigrationNextActions.RestoreBackup);
            await store.WriteAsync(second, cancellationToken);

            var read = await store.ReadAsync(profileScope, cancellationToken);
            Assert.Equal(P275MarkerReadStatus.Valid, read.Status);
            Assert.NotNull(read.Marker);
            Assert.Equal(P275MigrationState.VerifyFailed, read.Marker!.State);
            Assert.False(read.Marker.Ready);
            Assert.False(read.Marker.Writable);
            Assert.Equal(P275MigrationFailureCodes.VerifyFailed, read.Marker.FailureCode);

            var markerBytes = await File.ReadAllBytesAsync(layout.MigrationStatePath, cancellationToken);
            Assert.InRange(markerBytes.Length, 1, P275MigrationContract.MaxMarkerBytes);
            Assert.DoesNotContain(
                Encoding.UTF8.GetBytes(root),
                markerBytes);
            Assert.Empty(Directory.EnumerateFiles(layout.RuntimeDirectory, "*.tmp"));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task MissingCorruptAndOversizedMarkersBecomeUnavailableWithoutReadyFallback()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        try
        {
            var layout = new P275ProfileLayout(root);
            var profileScope = CreateProfileScope(root);
            var store = new P275MigrationStateStore(layout);
            var statusReader = new P275RecoveryStatusReader(layout);

            var missing = await store.ReadAsync(profileScope, cancellationToken);
            Assert.Equal(P275MarkerReadStatus.Missing, missing.Status);
            var missingStatus = await statusReader.ReadAsync(profileScope, cancellationToken);
            Assert.Equal(P275RecoveryStatusMode.Unavailable, missingStatus.Mode);
            Assert.False(missingStatus.Ready);
            Assert.False(missingStatus.Writable);
            Assert.Equal(P275MigrationFailureCodes.RecoveryRequired, missingStatus.FailureCode);

            Directory.CreateDirectory(layout.RuntimeDirectory);
            await File.WriteAllTextAsync(
                layout.MigrationStatePath,
                "{\"state\":\"READY\"}",
                cancellationToken);
            var corrupt = await store.ReadAsync(profileScope, cancellationToken);
            Assert.Equal(P275MarkerReadStatus.Invalid, corrupt.Status);
            var corruptStatus = await statusReader.ReadAsync(profileScope, cancellationToken);
            Assert.Equal(P275RecoveryStatusMode.Unavailable, corruptStatus.Mode);
            Assert.False(corruptStatus.Ready);

            await File.WriteAllBytesAsync(
                layout.MigrationStatePath,
                new byte[P275MigrationContract.MaxMarkerBytes + 1],
                cancellationToken);
            var oversized = await store.ReadAsync(profileScope, cancellationToken);
            Assert.Equal(P275MarkerReadStatus.Invalid, oversized.Status);
            var oversizedStatus = await statusReader.ReadAsync(profileScope, cancellationToken);
            Assert.Equal(P275RecoveryStatusMode.Unavailable, oversizedStatus.Mode);
            Assert.False(oversizedStatus.Writable);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task StatusReaderSeparatesReadOnlyRecoveryRequiredAndReadyModes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        try
        {
            var layout = new P275ProfileLayout(root);
            var profileScope = CreateProfileScope(root);
            var store = new P275MigrationStateStore(layout);

            await store.WriteAsync(CreateMarker(
                profileScope,
                P275MigrationState.MigrationInProgress,
                failureCode: null,
                P275MigrationNextActions.ViewStatus),
                cancellationToken);
            var inProgress = await new P275RecoveryStatusReader(layout).ReadAsync(
                profileScope,
                cancellationToken);
            Assert.Equal(P275RecoveryStatusMode.ReadOnly, inProgress.Mode);
            Assert.False(inProgress.Ready);
            Assert.False(inProgress.Writable);
            Assert.False(inProgress.RecoveryRequired);

            await store.WriteAsync(CreateMarker(
                profileScope,
                P275MigrationState.RecoveryFailed,
                P275MigrationFailureCodes.RestoreFailed,
                P275MigrationNextActions.RestoreBackup),
                cancellationToken);
            var failed = await new P275RecoveryStatusReader(layout).ReadAsync(
                profileScope,
                cancellationToken);
            Assert.Equal(P275RecoveryStatusMode.RecoveryRequired, failed.Mode);
            Assert.True(failed.RecoveryRequired);
            Assert.False(failed.Writable);

            await store.WriteAsync(CreateMarker(
                profileScope,
                P275MigrationState.Ready,
                failureCode: null,
                P275MigrationNextActions.None),
                cancellationToken);
            var ready = await new P275RecoveryStatusReader(layout).ReadAsync(
                profileScope,
                cancellationToken);
            Assert.Equal(P275RecoveryStatusMode.Ready, ready.Mode);
            Assert.True(ready.Ready);
            Assert.True(ready.Writable);
            Assert.False(ready.RecoveryRequired);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ReadyWithChangedBackupHashIsForcedToRecoveryRequired()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        try
        {
            var layout = new P275ProfileLayout(root);
            var profileScope = CreateProfileScope(root);
            Directory.CreateDirectory(layout.BackupsDirectory);
            var artifact = "migration-empty-target-20260831T000000000Z-00000000-0000-0000-0000-000000000000.sqlite";
            var artifactPath = layout.GetBackupPath(artifact);
            await File.WriteAllBytesAsync(artifactPath, [1, 2, 3], cancellationToken);

            var marker = CreateMarker(
                profileScope,
                P275MigrationState.Ready,
                failureCode: null,
                P275MigrationNextActions.None,
                backupArtifact: artifact,
                backupSha256: new string('0', 64));
            await new P275MigrationStateStore(layout).WriteAsync(marker, cancellationToken);

            var status = await new P275RecoveryStatusReader(layout).ReadAsync(
                profileScope,
                cancellationToken);
            Assert.Equal(P275RecoveryStatusMode.RecoveryRequired, status.Mode);
            Assert.Equal(P275BackupHashStatus.Mismatched, status.BackupHashStatus);
            Assert.False(status.Ready);
            Assert.False(status.Writable);
            Assert.Equal(P275MigrationFailureCodes.RecoveryBackupInvalid, status.FailureCode);
            Assert.Equal(P275MigrationNextActions.ViewStatus, status.NextAction);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ReadyWithMalformedBackupManifestIsForcedToRecoveryRequired()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTempRoot();
        try
        {
            var layout = new P275ProfileLayout(root);
            var profileScope = CreateProfileScope(root);
            Directory.CreateDirectory(layout.BackupsDirectory);
            var artifact = "migration-empty-target-20260831T000000000Z-00000000-0000-0000-0000-000000000000.sqlite";
            var artifactBytes = new byte[] { 1, 2, 3 };
            var artifactSha256 = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant();
            await File.WriteAllBytesAsync(
                layout.GetBackupPath(artifact),
                artifactBytes,
                cancellationToken);

            var marker = CreateMarker(
                profileScope,
                P275MigrationState.Ready,
                failureCode: null,
                P275MigrationNextActions.None,
                backupArtifact: artifact,
                backupSha256: artifactSha256);
            await new P275MigrationStateStore(layout).WriteAsync(marker, cancellationToken);

            var manifest = new SafetyBackupManifest(
                SafetyBackupContract.ContractVersion,
                marker.RunId.ToString("D"),
                marker.ProfileScope,
                marker.SourceSchema,
                marker.TargetSchema,
                "2026-09-01T00:00:00.0000000Z",
                artifact,
                artifactBytes.Length,
                artifactSha256,
                new SafetyBackupSourceFingerprint(
                    artifactBytes.Length,
                    artifactSha256,
                    new string('a', 64),
                    new SafetyBackupSidecarFingerprint(false, 0, null),
                    new SafetyBackupSidecarFingerprint(false, 0, null),
                    new SafetyBackupSidecarFingerprint(false, 0, null)),
                SafetyBackupContract.VerifiedResult);
            var manifestPath = layout.GetManifestPath(Path.ChangeExtension(artifact, ".json"));
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest),
                cancellationToken);

            var valid = await new P275RecoveryStatusReader(layout).ReadAsync(
                profileScope,
                cancellationToken);
            Assert.Equal(P275RecoveryStatusMode.Ready, valid.Mode);
            Assert.Equal(P275BackupManifestStatus.Matches, valid.ManifestStatus);

            var sidecarPath = layout.GetBackupPath(artifact) + "-wal";
            await File.WriteAllBytesAsync(sidecarPath, [9], cancellationToken);
            var sidecarStatus = await new P275RecoveryStatusReader(layout).ReadAsync(
                profileScope,
                cancellationToken);
            Assert.Equal(P275RecoveryStatusMode.RecoveryRequired, sidecarStatus.Mode);
            Assert.Equal(P275BackupManifestStatus.Unreadable, sidecarStatus.ManifestStatus);
            Assert.False(sidecarStatus.Ready);
            Assert.False(sidecarStatus.Writable);
            File.Delete(sidecarPath);

            var malformed = manifest with { CreatedAtUtc = "2026-09-01T00:00:00Z" };
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(malformed),
                cancellationToken);

            var status = await new P275RecoveryStatusReader(layout).ReadAsync(
                profileScope,
                cancellationToken);
            Assert.Equal(P275RecoveryStatusMode.RecoveryRequired, status.Mode);
            Assert.Equal(P275BackupManifestStatus.Unreadable, status.ManifestStatus);
            Assert.False(status.Ready);
            Assert.False(status.Writable);
            Assert.Equal(P275MigrationFailureCodes.RecoveryBackupInvalid, status.FailureCode);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static P275MigrationStateMarker CreateMarker(
        string profileScope,
        P275MigrationState state,
        string? failureCode,
        string nextAction,
        string? backupArtifact = null,
        string? backupSha256 = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            state,
            Guid.CreateVersion7(),
            profileScope,
            ["20260828025922_InitialTaskSchema"],
            [
                "20260828025922_InitialTaskSchema",
                "20260831090000_P25StorageConsistency"
            ],
            backupArtifact,
            backupSha256,
            "recovery/staging/00000000-0000-0000-0000-000000000001/candidate.sqlite",
            now,
            now,
            failureCode,
            retryable: failureCode is not null,
            nextAction,
            ["20260828025922_InitialTaskSchema"],
            Guid.CreateVersion7().ToString("D"));
    }

    private static string CreateProfileScope(string root)
    {
        var databasePath = Path.Combine(root, "reminnote.sqlite");
        return ProtocolProfileScope.Derive(
            "S-1-5-21-100-200-300-400",
            Path.GetFullPath(databasePath));
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "reminnote-p275-state-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
