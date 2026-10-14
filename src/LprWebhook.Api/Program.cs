using LprWebhook.Api;
using LprWebhook.Api.Access;
using LprWebhook.Api.Configuration;
using LprWebhook.Api.Data;
using LprWebhook.Api.Endpoints;
using LprWebhook.Api.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// Used by the Docker HEALTHCHECK: the runtime image has no curl/wget.
if (args.Contains("--healthcheck"))
    return await HealthProbe.RunAsync();

var builder = WebApplication.CreateBuilder(args);

// Telegram Bot Configuration
builder.Services.Configure<TelegramBotOptions>(
    builder.Configuration.GetSection("TelegramBot"));

var telegramOptions = builder.Configuration
    .GetSection("TelegramBot")
    .Get<TelegramBotOptions>();

if (telegramOptions?.Enabled == true)
{
    builder.Services.AddHttpClient<IRelayService, RelayService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(10);
    });

    builder.Services.AddScoped<BotCommandHandler>();
    builder.Services.AddScoped<PlateAlertHandler>();
    builder.Services.AddSingleton<PlateAlertQueue>();
    builder.Services.AddHostedService<TelegramBotService>();
}

// Drops the repeats the camera sends while a car is in view.
builder.Services.Configure<PlateEventOptions>(builder.Configuration.GetSection("PlateEvents"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PlateDeduplicator>();

var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=events.db";
builder.Services.AddDbContext<EventsDbContext>(o => o.UseSqlite(connectionString));

// Access control: roles/permissions (AccessService) and the admin web sign-in (/weblogin code -> cookie).
builder.Services.AddScoped<AccessService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<LoginCodeStore>();
// Cookie encryption keys live next to the database (in Docker: the /data volume) so sign-ins survive restarts.
var dbDirectory = Path.GetDirectoryName(Path.GetFullPath(new SqliteConnectionStringBuilder(connectionString).DataSource))!;
builder.Services.AddDataProtection()
    .SetApplicationName("LprWebhook")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dbDirectory, "dp-keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "lpr_admin";
        o.Cookie.HttpOnly = true;
        // Strict: the cookie is never sent on cross-site requests, which is the CSRF protection for the admin API.
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        // API, not a site with a login page: answer 401/403 instead of redirecting.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();
builder.Services.AddHealthChecks().AddDbContextCheck<EventsDbContext>();
builder.Services.AddOpenApi();

// Off by default. Lets the events page (wwwroot/index.html) be opened from elsewhere,
// e.g. "null" for a file opened straight from disk, or "*" for any origin. GET only.
var corsOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    {
        if (corsOrigins.Contains("*")) p.AllowAnyOrigin();
        else p.WithOrigins(corsOrigins);
        p.WithMethods("GET");
    }));
}

var app = builder.Build();

await app.InitializeDatabaseAsync();

if (corsOrigins.Length > 0)
    app.UseCors();

// Events viewer at "/".
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Configuration.GetValue("Swagger:Enabled", true))
{
    app.MapOpenApi();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "LPR Webhook API v1");
        o.RoutePrefix = "swagger";
    });
}

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapEventEndpoints();
app.MapBotCommandEndpoints();
app.MapAuthEndpoints();
app.MapAccessEndpoints();

await app.RunAsync();
return 0;

// Lets WebApplicationFactory<Program> in the tests start the real app.
public partial class Program;
