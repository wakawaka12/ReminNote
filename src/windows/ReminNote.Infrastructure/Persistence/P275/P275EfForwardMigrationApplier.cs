using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReminNote.Infrastructure.Persistence.P25;

namespace ReminNote.Infrastructure.Persistence.P275;

/// <summary>
/// EF Core adapter for the P2.75 forward-only seam. It validates the
/// Candidate's history and the binary's migration list before applying a
/// specific approved target. No overload without a target is used, and this
/// adapter never receives the Active DB path from the runner.
/// </summary>
public sealed class P275EfForwardMigrationApplier : IP275ForwardMigrationApplier
{
    private readonly bool ensureP25Profile;

    public P275EfForwardMigrationApplier(bool ensureP25Profile = true)
    {
        this.ensureP25Profile = ensureP25Profile;
    }

    public async ValueTask<P275CandidateApplyResult> ApplyAsync(
        P275Candidate candidate,
        P275MigrationPlan plan,
        string profileScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileScope);
        if (!File.Exists(candidate.Files.CandidateDatabasePath))
        {
            throw new FileNotFoundException("The Candidate database does not exist.");
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = candidate.Files.CandidateDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var options = ReminNoteDbContext.CreateOptions(connection);
        await using var context = new ReminNoteDbContext(options);
        var knownMigrations = context.Database.GetMigrations().ToArray();
        var isEmptyFile = new FileInfo(candidate.Files.CandidateDatabasePath).Length == 0;
        var appliedMigrations = isEmptyFile
            ? Array.Empty<string>()
            : (await context.Database
                    .GetAppliedMigrationsAsync(cancellationToken)
                    .ConfigureAwait(false))
                .ToArray();

        plan.ValidateKnownMigrations(knownMigrations);
        // The first Portable/UserData launch intentionally starts from an
        // empty Candidate file. A non-empty Candidate still has to carry the
        // exact approved source history; the runner's verified backup/length
        // check prevents an old non-empty Active from being replaced by an
        // empty Candidate.
        if (!isEmptyFile)
        {
            plan.ValidateSource(appliedMigrations);
        }

        if (!appliedMigrations.SequenceEqual(plan.ApprovedTargetMigrations, StringComparer.Ordinal))
        {
            // This is the only EF migration operation in the adapter. The
            // explicit target prevents a later, unapproved assembly migration
            // from being pulled into this attempt.
            await context.Database
                .MigrateAsync(plan.TargetMigrationId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (ensureP25Profile)
        {
            await P25StorageSchema.EnsureProfileAsync(
                    connection,
                    profileScope,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        return new P275CandidateApplyResult(plan.ApprovedTargetMigrations.ToArray());
    }
}
