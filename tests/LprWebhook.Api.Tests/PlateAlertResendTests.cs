using System.Net;
using System.Net.Http.Json;
using LprWebhook.Api.Access;
using LprWebhook.Api.Contracts;
using LprWebhook.Api.Data;
using LprWebhook.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LprWebhook.Api.Tests;

/// <summary>
/// "Resend Telegram alert" from the viewer. The bot is enabled without a token: its services (and the alert queue)
/// are registered but nothing polls Telegram, so the test can read what was queued.
/// </summary>
public sealed class PlateAlertResendTests : IDisposable
{
    private const long OwnerId = 1001;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lpr-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public PlateAlertResendTests()
    {
        Directory.CreateDirectory(_dir);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(_dir, "test.db")}")
            .UseSetting("TelegramBot:Enabled", "true")
            .UseSetting("TelegramBot:BotToken", "")
            .UseSetting("TelegramBot:OwnerUserId", OwnerId.ToString()));
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<HttpClient> SignedInClientAsync(long userId)
    {
        var client = NewClient();
        var code = _factory.Services.GetRequiredService<LoginCodeStore>().Create(userId);
        await client.GetAsync($"/login?code={Uri.EscapeDataString(code)}");
        return client;
    }

    private async Task<Guid> PostEventAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/events/plate", new StringContent(
            """{"timestamp":"2026-10-05T14:23:07","camera":{"name":"Gate","id":"cam-001"},"plate":{"text":"123ABC02"}}"""));
        return (await response.Content.ReadFromJsonAsync<ReceivedResponse>())!.Id;
    }

    private PlateAlertQueue Queue => _factory.Services.GetRequiredService<PlateAlertQueue>();

    private List<PlateAlert> DrainQueue()
    {
        var items = new List<PlateAlert>();
        while (Queue.Reader.TryRead(out var a)) items.Add(a);
        return items;
    }

    [Fact]
    public async Task Admin_can_resend_an_undecided_event_and_the_message_says_who_resent_it()
    {
        var client = await SignedInClientAsync(OwnerId);
        var id = await PostEventAsync(client);
        Assert.Single(DrainQueue()); // the original alert

        var response = await client.PostAsync($"/api/access/events/{id}/resend-alert", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resent = Assert.Single(DrainQueue());
        Assert.Equal(id, resent.EventId);
        Assert.Equal(OwnerId.ToString(), resent.ResentBy); // the owner never messaged the bot, so no @username
        Assert.EndsWith($"\n\nResent by {OwnerId}", PlateAlertHandler.Format(resent));
    }

    [Fact]
    public async Task Resend_needs_a_signed_in_admin()
    {
        var anonymous = NewClient();
        var id = await PostEventAsync(anonymous);
        DrainQueue();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/access/events/{id}/resend-alert", null)).StatusCode);
        // A non-admin's login code is refused, so they never get a cookie.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await (await SignedInClientAsync(2002)).PostAsync($"/api/access/events/{id}/resend-alert", null)).StatusCode);
        Assert.Empty(DrainQueue());
    }

    [Fact]
    public async Task Resending_a_decided_event_clears_the_decision_and_says_what_it_was()
    {
        var client = await SignedInClientAsync(OwnerId);
        var id = await PostEventAsync(client);
        DrainQueue();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
            db.PlateDecisions.Add(new PlateDecision
            {
                EventId = id, Status = PlateDecisionStatus.Denied, DecidedByUserId = OwnerId, DecidedByName = "@owner", DecidedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync($"/api/access/events/{id}/resend-alert", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("denied by @owner", (await response.Content.ReadFromJsonAsync<AccessActionResponse>())!.Message);
        var resent = Assert.Single(DrainQueue());
        Assert.Equal("denied by @owner", resent.PreviousDecision);
        Assert.EndsWith($"\n\nResent by {OwnerId} (was denied by @owner)", PlateAlertHandler.Format(resent));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EventsDbContext>();
            Assert.False(db.PlateDecisions.Any(d => d.EventId == id)); // open to a new decision
        }
    }

    [Fact]
    public async Task Unknown_events_are_not_resent()
    {
        var client = await SignedInClientAsync(OwnerId);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/access/events/{Guid.NewGuid()}/resend-alert", null)).StatusCode);
        Assert.Empty(DrainQueue());
    }
}
