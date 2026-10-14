using System.Net;
using System.Net.Http.Json;
using LprWebhook.Api.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LprWebhook.Api.Tests;

/// <summary>The webhook end to end on a throwaway database: repeats of the same plate are answered but not stored.</summary>
public sealed class PlateWebhookTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lpr-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public PlateWebhookTests()
    {
        Directory.CreateDirectory(_dir);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(_dir, "test.db")}")
            .UseSetting("TelegramBot:Enabled", "false")
            .UseSetting("PlateEvents:DuplicateWindowSeconds", "30"));
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static StringContent Payload(string plate, string camera = "cam-001") => new(
        $$$"""{"event":"plate_recognized","timestamp":"2026-10-05T14:23:07.512","camera":{"name":"Gate","id":"{{{camera}}}"},"plate":{"text":"{{{plate}}}"}}""",
        System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Burst_of_the_same_plate_is_stored_once()
    {
        var client = _factory.CreateClient();

        var first = await client.PostAsync("/api/events/plate", Payload("123ABC02"));
        var firstBody = await first.Content.ReadFromJsonAsync<ReceivedResponse>();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("ok", firstBody!.Status);

        for (var i = 0; i < 5; i++)
        {
            var repeat = await client.PostAsync("/api/events/plate", Payload("123ABC02"));
            Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
            var body = await repeat.Content.ReadFromJsonAsync<ReceivedResponse>();
            Assert.Equal(("duplicate", firstBody.Id), (body!.Status, body.Id));
        }

        Assert.Equal("ok", (await (await client.PostAsync("/api/events/plate", Payload("999ZZZ01"))).Content.ReadFromJsonAsync<ReceivedResponse>())!.Status);
        Assert.Equal("ok", (await (await client.PostAsync("/api/events/plate", Payload("123ABC02", "cam-002"))).Content.ReadFromJsonAsync<ReceivedResponse>())!.Status);

        var list = await client.GetFromJsonAsync<PagedResponse<PlateEventSummary>>("/api/events");
        Assert.Equal(3, list!.Total);
        Assert.All(list.Items, e => Assert.Null(e.Decision));
    }
}
