using Microsoft.Data.Sqlite;

namespace ReminNote.Infrastructure.Persistence.P275;

/// <summary>
/// Finalizes a closed Candidate generation. SQLite is asked to checkpoint its
/// WAL while the Candidate is the only database being touched; once the
/// connection is closed, known sidecars are removed so they cannot be paired
/// with the next Active generation.
/// </summary>
public sealed class P275CandidateSidecarFinalizer : IP275CandidateFinalizer
{
    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    public async ValueTask<P275CandidateFinalizationResult> FinalizeAsync(
        P275Candidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!P275FileSafety.IsRegularFile(candidate.Files.CandidateDatabasePath))
        {
            return P275CandidateFinalizationResult.Invalid(P275MigrationFailureCodes.VerifyFailed);
        }

        try
        {
            var existingSidecars = EnumerateSidecars(candidate.Files.CandidateDatabasePath);
            if (existingSidecars.Any(sidecar => !P275FileSafety.IsRegularFile(sidecar)))
            {
                return P275CandidateFinalizationResult.Invalid(P275MigrationFailureCodes.VerifyFailed);
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = candidate.Files.CandidateDatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                ForeignKeys = true
            }.ToString();

            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await using var checkpointResult = await checkpoint
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await checkpointResult.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Consume SQLite's checkpoint result row before closing
                    // the Candidate connection.
                }
            }

            foreach (var sidecar in EnumerateSidecars(candidate.Files.CandidateDatabasePath))
            {
                if (!P275FileSafety.IsRegularFile(sidecar))
                {
                    return P275CandidateFinalizationResult.Invalid(P275MigrationFailureCodes.VerifyFailed);
                }

                File.Delete(sidecar);
                if (File.Exists(sidecar))
                {
                    return P275CandidateFinalizationResult.Invalid(P275MigrationFailureCodes.VerifyFailed);
                }
            }

            return P275CandidateFinalizationResult.Valid();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SqliteException or
            InvalidOperationException)
        {
            return P275CandidateFinalizationResult.Invalid(P275MigrationFailureCodes.VerifyFailed);
        }
    }

    internal static IReadOnlyList<string> EnumerateSidecars(string databasePath) =>
        SidecarSuffixes
            .Select(suffix => databasePath + suffix)
            .Where(P275FileSafety.PathExists)
            .ToArray();
}

/// <summary>
/// Promotes a closed, sidecar-free Candidate in the same profile volume. The
/// old Active main file is preserved by the platform atomic replace operation;
/// old Active sidecars are moved as part of the same generation record. A
/// failure after replace has started is deliberately reported as UNKNOWN.
/// </summary>
public sealed class P275AtomicFilePromoter : IP275AtomicPromoter
{
    public ValueTask<P275PromotionResult> PromoteAsync(
        P275PromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var paths = request.Paths;
        var files = request.Candidate.Files;
        if (!IsWithinProfile(files.CandidateDatabasePath, paths.ProfileRoot) ||
            !IsWithinProfile(paths.ActiveDatabasePath, paths.ProfileRoot) ||
            !P275FileSafety.IsRegularFile(files.CandidateDatabasePath) ||
            P275CandidateSidecarFinalizer.EnumerateSidecars(files.CandidateDatabasePath)
                .Any(sidecar => !P275FileSafety.IsRegularFile(sidecar)))
        {
            return ValueTask.FromResult(P275PromotionResult.ExplicitFailure());
        }

        try
        {
            Directory.CreateDirectory(files.HistoryDirectory);
            if (Directory.EnumerateFileSystemEntries(files.HistoryDirectory).Any())
            {
                return ValueTask.FromResult(P275PromotionResult.ExplicitFailure());
            }

            var activeExists = File.Exists(paths.ActiveDatabasePath);
            if (activeExists && !P275FileSafety.IsRegularFile(paths.ActiveDatabasePath))
            {
                return ValueTask.FromResult(P275PromotionResult.ExplicitFailure());
            }

            var activeSidecars = activeExists
                ? P275CandidateSidecarFinalizer.EnumerateSidecars(paths.ActiveDatabasePath)
                : Array.Empty<string>();
            if (activeSidecars.Any(sidecar => !P275FileSafety.IsRegularFile(sidecar)))
            {
                return ValueTask.FromResult(P275PromotionResult.ExplicitFailure());
            }

            if (!activeExists && activeSidecars.Count != 0)
            {
                return ValueTask.FromResult(P275PromotionResult.ExplicitFailure());
            }

            var movedSidecars = new List<(string HistoryPath, string ActivePath)>();
            var replaceStarted = false;
            try
            {
                foreach (var activeSidecar in activeSidecars)
                {
                    var historySidecar = Path.Combine(
                        files.HistoryDirectory,
                        Path.GetFileName(activeSidecar));
                    File.Move(activeSidecar, historySidecar);
                    movedSidecars.Add((historySidecar, activeSidecar));
                }

                if (activeExists)
                {
                    replaceStarted = true;
                    File.Replace(
                        files.CandidateDatabasePath,
                        paths.ActiveDatabasePath,
                        Path.Combine(files.HistoryDirectory, "active.sqlite"),
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(files.CandidateDatabasePath, paths.ActiveDatabasePath);
                }
            }
            catch
            {
                if (replaceStarted)
                {
                    return ValueTask.FromResult(P275PromotionResult.Unknown());
                }

                if (!TryRestoreSidecars(movedSidecars))
                {
                    return ValueTask.FromResult(P275PromotionResult.Unknown());
                }

                return ValueTask.FromResult(P275PromotionResult.ExplicitFailure());
            }

            if (!File.Exists(paths.ActiveDatabasePath) || File.Exists(files.CandidateDatabasePath))
            {
                return ValueTask.FromResult(P275PromotionResult.Unknown());
            }

            if (activeExists && !File.Exists(Path.Combine(files.HistoryDirectory, "active.sqlite")))
            {
                return ValueTask.FromResult(P275PromotionResult.Unknown());
            }

            return ValueTask.FromResult(P275PromotionResult.Succeeded(files.HistoryDirectory));
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            PlatformNotSupportedException)
        {
            return ValueTask.FromResult(P275PromotionResult.ExplicitFailure());
        }
    }

    private static bool TryRestoreSidecars(
        IEnumerable<(string HistoryPath, string ActivePath)> movedSidecars)
    {
        try
        {
            foreach (var (historyPath, activePath) in movedSidecars.Reverse())
            {
                if (File.Exists(historyPath))
                {
                    File.Move(historyPath, activePath);
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsWithinProfile(string path, string profileRoot)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(profileRoot);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return fullPath.StartsWith(
            fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar,
            comparison);
    }
}
