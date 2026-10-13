using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LprWebhook.Api.Data;

public class EventsDbContext(DbContextOptions<EventsDbContext> options) : DbContext(options)
{
    public DbSet<PlateEvent> PlateEvents => Set<PlateEvent>();
    public DbSet<BotCommandLog> BotCommands => Set<BotCommandLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var e = modelBuilder.Entity<PlateEvent>();
        e.ToTable("plate_events");
        e.HasKey(x => x.Id);
        e.Property(x => x.PlateText).IsRequired();
        e.Property(x => x.RawJson).IsRequired();
        // SQLite returns DateTimeKind.Unspecified; mark the receive time as UTC again on the way out.
        e.Property(x => x.ReceivedAtUtc)
            .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        e.HasIndex(x => x.ReceivedAtUtc);
        e.HasIndex(x => x.PlateText);
        e.HasIndex(x => x.CameraId);

        var c = modelBuilder.Entity<BotCommandLog>();
        c.ToTable("bot_commands");
        c.HasKey(x => x.Id);
        c.Property(x => x.Text).IsRequired();
        c.Property(x => x.Outcome).IsRequired();
        c.Property(x => x.ReceivedAtUtc)
            .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        c.Property(x => x.SentAtUtc)
            .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        c.HasIndex(x => x.ReceivedAtUtc);
        c.HasIndex(x => x.UserId);
        c.HasIndex(x => x.Command);
    }
}

public static class DatabaseSetup
{
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventsDbContext>();

        var dataSource = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // EnsureCreated() does nothing when the file already exists, so tables added later (bot_commands)
        // would never appear in an existing database. Run the model's create script with IF NOT EXISTS
        // instead: missing tables and indexes are created, existing ones are left alone.
        // New columns on existing tables are NOT added; that needs EF migrations or a fresh volume.
        var script = db.Database.GenerateCreateScript()
            .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ")
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ")
            .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ");
        await db.Database.ExecuteSqlRawAsync(script);
        app.Logger.LogInformation("SQLite database ready at {DataSource}", Path.GetFullPath(dataSource));
    }
}
