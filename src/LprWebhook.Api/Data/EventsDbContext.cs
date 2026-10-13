using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LprWebhook.Api.Data;

public class EventsDbContext(DbContextOptions<EventsDbContext> options) : DbContext(options)
{
    public DbSet<PlateEvent> PlateEvents => Set<PlateEvent>();
    public DbSet<BotCommandLog> BotCommands => Set<BotCommandLog>();
    public DbSet<BotUser> BotUsers => Set<BotUser>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<PermissionAudit> PermissionAudit => Set<PermissionAudit>();

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

        var u = modelBuilder.Entity<BotUser>();
        u.ToTable("bot_users");
        u.HasKey(x => x.UserId);
        // Telegram assigns the ID; without this EF would treat the long key as auto-increment.
        u.Property(x => x.UserId).ValueGeneratedNever();
        u.Property(x => x.Role).IsRequired();
        u.Property(x => x.CreatedAtUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        u.Property(x => x.LastSeenAtUtc).HasConversion(
            v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : null);
        u.HasIndex(x => x.Username);

        var p = modelBuilder.Entity<UserPermission>();
        p.ToTable("user_permissions");
        p.HasKey(x => new { x.UserId, x.Permission });
        p.Property(x => x.GrantedAtUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        var a = modelBuilder.Entity<PermissionAudit>();
        a.ToTable("permission_audit");
        a.HasKey(x => x.Id);
        a.Property(x => x.Action).IsRequired();
        a.Property(x => x.Source).IsRequired();
        a.Property(x => x.Result).IsRequired();
        a.Property(x => x.AtUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        a.HasIndex(x => x.AtUtc);
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
