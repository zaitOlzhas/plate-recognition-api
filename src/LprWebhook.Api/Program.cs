using LprWebhook.Api;
using LprWebhook.Api.Data;
using LprWebhook.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

// Used by the Docker HEALTHCHECK: the runtime image has no curl/wget.
if (args.Contains("--healthcheck"))
    return await HealthProbe.RunAsync();

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=events.db";
builder.Services.AddDbContext<EventsDbContext>(o => o.UseSqlite(connectionString));
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

app.MapHealthChecks("/health");
app.MapEventEndpoints();

await app.RunAsync();
return 0;
