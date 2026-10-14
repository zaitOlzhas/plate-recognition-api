using LprWebhook.Api.Configuration;
using LprWebhook.Api.Services;
using Microsoft.Extensions.Options;

namespace LprWebhook.Api.Tests;

public sealed class PlateDeduplicatorTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualClock _clock = new();

    private PlateDeduplicator Create(int windowSeconds = 30)
        => new(Options.Create(new PlateEventOptions { DuplicateWindowSeconds = windowSeconds }), _clock);

    [Fact]
    public void Repeats_within_the_window_are_dropped_and_point_at_the_first_event()
    {
        var dedup = Create();
        var key = PlateDeduplicator.Key("cam-1", "123ABC02");
        var first = Guid.NewGuid();

        Assert.True(dedup.TryAccept(key, first, out _));
        _clock.Now = _clock.Now.AddMilliseconds(200);
        Assert.False(dedup.TryAccept(key, Guid.NewGuid(), out var original));
        Assert.Equal(first, original);
    }

    [Fact]
    public void Each_repeat_restarts_the_window()
    {
        var dedup = Create(windowSeconds: 10);
        var key = PlateDeduplicator.Key("cam-1", "123ABC02");

        Assert.True(dedup.TryAccept(key, Guid.NewGuid(), out _));
        for (var i = 0; i < 5; i++)
        {
            _clock.Now = _clock.Now.AddSeconds(8); // 40 s in total, but never 10 s apart
            Assert.False(dedup.TryAccept(key, Guid.NewGuid(), out _));
        }

        _clock.Now = _clock.Now.AddSeconds(10);
        Assert.True(dedup.TryAccept(key, Guid.NewGuid(), out _));
    }

    [Fact]
    public void Other_plates_and_other_cameras_are_not_duplicates()
    {
        var dedup = Create();
        Assert.True(dedup.TryAccept(PlateDeduplicator.Key("cam-1", "AAA111"), Guid.NewGuid(), out _));
        Assert.True(dedup.TryAccept(PlateDeduplicator.Key("cam-1", "BBB222"), Guid.NewGuid(), out _));
        Assert.True(dedup.TryAccept(PlateDeduplicator.Key("cam-2", "AAA111"), Guid.NewGuid(), out _));
    }

    [Fact]
    public void Plate_spacing_and_case_do_not_matter()
        => Assert.Equal(PlateDeduplicator.Key("cam-1", "123ABC02"), PlateDeduplicator.Key("cam-1", " 123 abc-02 "));

    [Fact]
    public void Forget_lets_the_next_try_in()
    {
        var dedup = Create();
        var key = PlateDeduplicator.Key("cam-1", "123ABC02");
        var id = Guid.NewGuid();

        Assert.True(dedup.TryAccept(key, id, out _));
        dedup.Forget(key, id);
        Assert.True(dedup.TryAccept(key, Guid.NewGuid(), out _));
    }

    [Fact]
    public void Zero_window_turns_it_off()
    {
        var dedup = Create(windowSeconds: 0);
        var key = PlateDeduplicator.Key("cam-1", "123ABC02");
        Assert.True(dedup.TryAccept(key, Guid.NewGuid(), out _));
        Assert.True(dedup.TryAccept(key, Guid.NewGuid(), out _));
    }

    [Fact]
    public async Task Parallel_identical_requests_store_exactly_one()
    {
        var dedup = Create();
        var key = PlateDeduplicator.Key("cam-1", "123ABC02");
        var accepted = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => Task.Run(() => dedup.TryAccept(key, Guid.NewGuid(), out _))));
        Assert.Single(accepted, a => a);
    }
}
