using LprWebhook.Api.Access;
using LprWebhook.Api.Configuration;
using LprWebhook.Api.Data;
using LprWebhook.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types.Enums;

namespace LprWebhook.Api.Tests;

/// <summary>The Open gate / Deny button logic, without Telegram.</summary>
public sealed class PlateAlertHandlerTests : IDisposable
{
    private const long OwnerId = 1;
    private const long GateUserId = 2;
    private const long StrangerId = 3;

    private sealed class FakeRelay : IRelayService
    {
        public int Calls { get; private set; }
        public bool Succeeds { get; set; } = true;

        public Task<RelayResult> TriggerRelayAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Succeeds ? new RelayResult(true, "out1_pulse", 200) : new RelayResult(false, "Connection error", 0));
        }
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly EventsDbContext _db;
    private readonly FakeRelay _relay = new();
    private readonly PlateAlertHandler _handler;
    private readonly Guid _eventId = Guid.CreateVersion7();

    public PlateAlertHandlerTests()
    {
        _connection.Open();
        _db = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        var access = new AccessService(_db, Options.Create(new TelegramBotOptions { OwnerUserId = OwnerId }));
        _handler = new PlateAlertHandler(_db, access, _relay, NullLogger<PlateAlertHandler>.Instance);

        _db.BotUsers.AddRange(new BotUser { UserId = GateUserId, Username = "gate" }, new BotUser { UserId = StrangerId });
        _db.UserPermissions.Add(new UserPermission { UserId = GateUserId, Permission = BotPermissions.OpenGate });
        _db.PlateEvents.Add(new PlateEvent { Id = _eventId, ReceivedAtUtc = DateTime.UtcNow, PlateText = "123ABC02", RawJson = "{}" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<PlateDecision?> StoredDecisionAsync()
    {
        _db.ChangeTracker.Clear();
        return await _db.PlateDecisions.FindAsync(_eventId);
    }

    [Fact]
    public async Task Open_gate_pulses_the_relay_and_marks_the_event_granted()
    {
        var result = await _handler.DecideAsync(_eventId, PlateDecisionStatus.Granted, new Actor(GateUserId, "@gate"), default);

        Assert.True(result.IsOk);
        Assert.Equal(1, _relay.Calls);
        var d = await StoredDecisionAsync();
        Assert.Equal((PlateDecisionStatus.Granted, GateUserId, "@gate"), (d!.Status, d.DecidedByUserId, d.DecidedByName));
    }

    [Fact]
    public async Task Deny_marks_the_event_denied_without_touching_the_relay()
    {
        var result = await _handler.DecideAsync(_eventId, PlateDecisionStatus.Denied, new Actor(OwnerId, "@owner"), default);

        Assert.True(result.IsOk);
        Assert.Equal(0, _relay.Calls);
        Assert.Equal(PlateDecisionStatus.Denied, (await StoredDecisionAsync())!.Status);
    }

    [Fact]
    public async Task First_press_wins()
    {
        await _handler.DecideAsync(_eventId, PlateDecisionStatus.Denied, new Actor(OwnerId, "@owner"), default);
        var second = await _handler.DecideAsync(_eventId, PlateDecisionStatus.Granted, new Actor(GateUserId, "@gate"), default);

        Assert.Equal("failed", second.Outcome);
        Assert.Equal("Already denied by @owner.", second.Message);
        Assert.Equal(0, _relay.Calls);
        Assert.Equal(PlateDecisionStatus.Denied, (await StoredDecisionAsync())!.Status);
    }

    [Fact]
    public async Task Failed_relay_leaves_the_event_undecided_so_it_can_be_retried()
    {
        _relay.Succeeds = false;
        var result = await _handler.DecideAsync(_eventId, PlateDecisionStatus.Granted, new Actor(GateUserId, "@gate"), default);

        Assert.Equal("failed", result.Outcome);
        Assert.Null(await StoredDecisionAsync());

        _relay.Succeeds = true;
        Assert.True((await _handler.DecideAsync(_eventId, PlateDecisionStatus.Granted, new Actor(GateUserId, "@gate"), default)).IsOk);
    }

    [Fact]
    public async Task Users_without_open_gate_can_neither_open_nor_deny()
    {
        foreach (var status in new[] { PlateDecisionStatus.Granted, PlateDecisionStatus.Denied })
        {
            var result = await _handler.DecideAsync(_eventId, status, new Actor(StrangerId, null), default);
            Assert.Equal("denied", result.Outcome);
        }
        Assert.Equal(0, _relay.Calls);
        Assert.Null(await StoredDecisionAsync());
    }

    [Fact]
    public async Task Alerts_go_to_everyone_but_only_open_gate_users_get_buttons()
    {
        const long AdminId = 4;
        _db.BotUsers.Add(new BotUser { UserId = AdminId, Role = Roles.Admin });
        await _db.SaveChangesAsync();

        var access = new AccessService(_db, Options.Create(new TelegramBotOptions { OwnerUserId = OwnerId }));
        var recipients = await access.ListUsersWithPermissionAsync(BotPermissions.OpenGate);

        Assert.Equal(
            [(OwnerId, true), (GateUserId, true), (StrangerId, false), (AdminId, true)],
            recipients.OrderBy(r => r.UserId));
    }

    [Theory]
    [InlineData("open:0199e5b2c3d47e8f9a0b1c2d3e4f5a6b", "open")]
    [InlineData("deny:0199e5b2c3d47e8f9a0b1c2d3e4f5a6b", "deny")]
    public void Button_data_round_trips(string data, string action)
    {
        Assert.True(PlateAlertHandler.TryParse(data, out var a, out var id));
        Assert.Equal(action, a);
        Assert.Equal(Guid.Parse("0199e5b2c3d47e8f9a0b1c2d3e4f5a6b"), id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("open")]
    [InlineData("close:0199e5b2c3d47e8f9a0b1c2d3e4f5a6b")]
    [InlineData("open:not-a-guid")]
    public void Junk_button_data_is_rejected(string? data)
        => Assert.False(PlateAlertHandler.TryParse(data, out _, out _));

    [Fact]
    public void Alert_text_sets_the_plate_apart_then_shows_time_and_camera()
    {
        var alert = new PlateAlert(_eventId, "123ABC02", "Gate Entrance", new DateTime(2026, 10, 5, 14, 23, 7, 512), DateTime.UtcNow);
        var text = PlateAlertHandler.Format(alert);

        Assert.Equal("🚗 123ABC02\n\n2026-10-05 14:23:07\nCamera: Gate Entrance", text);
        var entities = PlateAlertHandler.PlateEntities(alert);
        Assert.Equal("🚗 123ABC02", Covered(MessageEntityType.Blockquote));
        Assert.Equal("123ABC02", Covered(MessageEntityType.Bold));

        string Covered(MessageEntityType type)
        {
            var e = Assert.Single(entities, x => x.Type == type);
            return text.Substring(e.Offset, e.Length);
        }
    }
}
