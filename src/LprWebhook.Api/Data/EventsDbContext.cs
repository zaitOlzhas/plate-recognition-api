using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LprWebhook.Api.Data;

public class EventsDbContext(DbContextOptions<EventsDbContext> options) : DbContext(options)
{
    public DbSet<PlateEvent> PlateEvents => Set<PlateEvent>();

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

        await db.Database.EnsureCreatedAsync();
        app.Logger.LogInformation("SQLite database ready at {DataSource}", Path.GetFullPath(dataSource));
    }
}
