using Microsoft.EntityFrameworkCore;

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
}
