using System.Net;
using System.Net.Http.Json;
using LprWebhook.Api.Access;
using LprWebhook.Api.Contracts;
using LprWebhook.Api.Data;
using LprWebhook.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LprWebhook.Api.Tests;

/// <summary>The "Open gate" button on the Access tab. The bot is enabled without a token and the relay is faked.</summary>
public sealed class WebOpenGateTests : IDisposable
{
    private const long OwnerId = 1001;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lpr-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRelay _relay = new();
    private readonly WebApplicationFactory<Program> _factory;

    private sealed class FakeRelay : IRelayService
    {
        public bool Succeeds { get; set; } = true;
        public int Calls { get; private set; }

        public Task<RelayResult> TriggerRelayAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Succeeds ? new RelayResult(true, "out1_pulse", 200) : new RelayResult(false, "Connection error"));
        }
    }

    public WebOpenGateTests()
    {
        Directory.CreateDirectory(_dir);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(_dir, "test.db")}")
            .UseSetting("TelegramBot:Enabled", "true")
            .UseSetting("TelegramBot:BotToken", "")
            .UseSetting("TelegramBot:OwnerUserId", OwnerId.ToString())
            .ConfigureTestServices(s => s.AddSingleton<IRelayService>(_relay)));
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

    private List<BotCommandLog> Logged()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<EventsDbContext>().BotCommands.ToList();
    }

    [Fact]
    public async Task Admin_opens_the_gate_and_it_is_logged_as_a_web_command()
    {
        var client = await SignedInClientAsync(OwnerId);

        var response = await client.PostAsync("/api/access/open-gate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("out1_pulse", (await response.Content.ReadFromJsonAsync<AccessActionResponse>())!.Message);
        Assert.Equal(1, _relay.Calls);
        var log = Assert.Single(Logged());
        Assert.Equal(("web", OwnerId, "/open_gate", "success", 200), (log.ChatType, log.UserId, log.Command, log.Outcome, log.RelayStatusCode));
    }

    [Fact]
    public async Task A_relay_failure_is_reported_and_logged()
    {
        _relay.Succeeds = false;
        var client = await SignedInClientAsync(OwnerId);

        var response = await client.PostAsync("/api/access/open-gate", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Connection error", (await response.Content.ReadFromJsonAsync<AccessActionResponse>())!.Message);
        Assert.Equal("failed", Assert.Single(Logged()).Outcome);
    }

    [Fact]
    public async Task Opening_the_gate_needs_a_signed_in_admin()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await NewClient().PostAsync("/api/access/open-gate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await (await SignedInClientAsync(2002)).PostAsync("/api/access/open-gate", null)).StatusCode);
        Assert.Equal(0, _relay.Calls);
        Assert.Empty(Logged());
    }
}
