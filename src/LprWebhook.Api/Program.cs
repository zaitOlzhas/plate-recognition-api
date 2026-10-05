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

var app = builder.Build();

await app.InitializeDatabaseAsync();

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
