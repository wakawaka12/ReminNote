using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.Data.Sqlite;
using System.Reflection;
using ReminNote.Infrastructure.Persistence.P25;
using ReminNote.Infrastructure.Persistence.Reminders;

namespace ReminNote.Infrastructure.Persistence;

/// <summary>
/// EF Core unit of work for the durable Task and P3 Reminder stores. Reminder
/// mappings are enabled for the formal migration/production options created by
/// this context; an options object without a migrations assembly remains the
/// legacy P2-only model used by the old isolated mapping fixture.
/// </summary>
public sealed class ReminNoteDbContext : DbContext
{
    public ReminNoteDbContext(DbContextOptions<ReminNoteDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Convenience constructor for tooling and small local composition roots.
    /// Production composition should prefer the options constructor.
    /// </summary>
    public ReminNoteDbContext(string connectionString)
        : this(CreateOptions(connectionString))
    {
    }

    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();

    public DbSet<TaskHistoryEntity> TaskHistory => Set<TaskHistoryEntity>();

    public DbSet<AppSettingsEntity> AppSettings => Set<AppSettingsEntity>();

    public DbSet<P25RevisionStateEntity> RevisionStates => Set<P25RevisionStateEntity>();

    public DbSet<P25ChangeJournalEntity> ChangeJournal => Set<P25ChangeJournalEntity>();

    public DbSet<P25CommandReceiptEntity> CommandReceipts => Set<P25CommandReceiptEntity>();

    public DbSet<ReminderRuleEntity> ReminderRules => Set<ReminderRuleEntity>();

    public DbSet<ReminderScheduleEntity> ReminderSchedules => Set<ReminderScheduleEntity>();

    public DbSet<ReminderInstanceEntity> ReminderInstances => Set<ReminderInstanceEntity>();

    public DbSet<ReminderDeliveryAttemptEntity> ReminderDeliveryAttempts => Set<ReminderDeliveryAttemptEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // The P2/P2.5 in-memory fixtures intentionally use a legacy model and
        // migration prefix. Include the formal model/cache dimension so an
        // earlier production-shaped context cannot leak its Reminder model
        // into a later bare-options context in the same test process.
        optionsBuilder
            .ReplaceService<IModelCacheKeyFactory, ReminNoteModelCacheKeyFactory>()
            .ReplaceService<IMigrationsAssembly, ReminNoteMigrationsAssembly>();

        var relationalOptions = optionsBuilder.Options.Extensions
            .OfType<RelationalOptionsExtension>()
            .SingleOrDefault();
        if (relationalOptions?.MigrationsAssemblyObject is null)
        {
            optionsBuilder.ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReminNoteDbContext).Assembly);

        // P3 Reminder mappings are formally part of the production/migration
        // model. Keep the intentionally bare P2 test options opt-out so the
        // pre-integration mapping fixture remains a valid regression guard.
        if (UsesFormalReminderModel(this))
        {
            modelBuilder.ApplyReminderConfigurations();
        }
        else
        {
            modelBuilder.Ignore<ReminderRuleEntity>();
            modelBuilder.Ignore<ReminderScheduleEntity>();
            modelBuilder.Ignore<ReminderInstanceEntity>();
            modelBuilder.Ignore<ReminderDeliveryAttemptEntity>();
        }
    }

    private static bool UsesFormalReminderModel(DbContext context)
    {
        var relationalOptions = RelationalOptionsExtension.Extract(
            context.GetService<IDbContextOptions>());
        return relationalOptions?.MigrationsAssemblyObject is not null;
    }

    private static bool IsInMemorySqlite(RelationalOptionsExtension? relationalOptions)
    {
        if (relationalOptions?.Connection is SqliteConnection connection)
        {
            return string.Equals(connection.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) ||
                connection.ConnectionString.Contains(
                    "Data Source=:memory:",
                    StringComparison.OrdinalIgnoreCase) ||
                connection.ConnectionString.Contains(
                    "Mode=Memory",
                    StringComparison.OrdinalIgnoreCase);
        }

        return relationalOptions?.ConnectionString?.Contains(
                "Data Source=:memory:",
                StringComparison.OrdinalIgnoreCase) == true ||
            relationalOptions?.ConnectionString?.Contains(
                "Mode=Memory",
                StringComparison.OrdinalIgnoreCase) == true;
    }

    private sealed class ReminNoteModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), UsesFormalReminderModel(context), designTime);
    }

#pragma warning disable EF1001 // EF's provider-facing migrations assembly is the required filtering seam.
    private sealed class ReminNoteMigrationsAssembly : MigrationsAssembly
    {
        public ReminNoteMigrationsAssembly(
            ICurrentDbContext currentContext,
            IDbContextOptions options,
            IMigrationsIdGenerator idGenerator,
            IDiagnosticsLogger<DbLoggerCategory.Migrations> logger)
            : base(currentContext, options, idGenerator, logger)
        {
            isInMemory = IsInMemorySqlite(
                RelationalOptionsExtension.Extract(options));
        }

        private readonly bool isInMemory;

        public override IReadOnlyDictionary<string, TypeInfo> Migrations
        {
            get
            {
                var migrations = base.Migrations;
                if (!isInMemory)
                {
                    return migrations;
                }

                return migrations
                    .Where(migration =>
                        string.CompareOrdinal(
                            migration.Key,
                            "20260831090000_P25StorageConsistency") <= 0)
                    .ToDictionary(
                        migration => migration.Key,
                        migration => migration.Value,
                        StringComparer.Ordinal);
            }
        }
    }
#pragma warning restore EF1001

    internal static DbContextOptions<ReminNoteDbContext> CreateOptions(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new DbContextOptionsBuilder<ReminNoteDbContext>();
        builder.UseSqlite(
            connectionString,
            sqlite => sqlite.MigrationsAssembly(typeof(ReminNoteDbContext).Assembly));
        return builder.Options;
    }

    public static DbContextOptions<ReminNoteDbContext> CreateOptions(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var builder = new DbContextOptionsBuilder<ReminNoteDbContext>();
        builder.UseSqlite(
            connection,
            sqlite => sqlite.MigrationsAssembly(typeof(ReminNoteDbContext).Assembly));
        return builder.Options;
    }
}
