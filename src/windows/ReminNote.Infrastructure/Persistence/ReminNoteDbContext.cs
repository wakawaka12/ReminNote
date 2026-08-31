using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using ReminNote.Infrastructure.Persistence.P25;

namespace ReminNote.Infrastructure.Persistence;

/// <summary>
/// EF Core unit of work for the durable Task store. The context intentionally
/// contains only Task state in P1; future Reminder/Anime/Sync tables must be
/// introduced by their own migrations and contracts.
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReminNoteDbContext).Assembly);
    }

    internal static DbContextOptions<ReminNoteDbContext> CreateOptions(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var builder = new DbContextOptionsBuilder<ReminNoteDbContext>();
        builder.UseSqlite(
            connectionString,
            sqlite => sqlite.MigrationsAssembly(typeof(ReminNoteDbContext).Assembly.GetName().Name));
        return builder.Options;
    }

    public static DbContextOptions<ReminNoteDbContext> CreateOptions(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var builder = new DbContextOptionsBuilder<ReminNoteDbContext>();
        builder.UseSqlite(
            connection,
            sqlite => sqlite.MigrationsAssembly(typeof(ReminNoteDbContext).Assembly.GetName().Name));
        return builder.Options;
    }
}
