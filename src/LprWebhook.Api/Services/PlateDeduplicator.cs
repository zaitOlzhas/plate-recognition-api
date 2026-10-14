using LprWebhook.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LprWebhook.Api.Services;

/// <summary>
/// Drops repeats of the same plate from the same camera (see <see cref="PlateEventOptions.DuplicateWindowSeconds"/>).
/// In memory only: after a restart the first sighting is stored again, which is harmless.
/// </summary>
public sealed class PlateDeduplicator(IOptions<PlateEventOptions> options, TimeProvider time)
{
    private readonly TimeSpan _window = TimeSpan.FromSeconds(Math.Max(options.Value.DuplicateWindowSeconds, 0));
    private readonly Dictionary<string, Sighting> _seen = new();
    private readonly Lock _lock = new();
    private DateTimeOffset _lastPrune;

    private sealed record Sighting(Guid EventId, DateTimeOffset LastSeen);

    /// <summary>"Gate Entrance" + "123 abc-02" -> key for "123ABC02" on that camera.</summary>
    public static string Key(string? camera, string plate)
        => $"{camera}\n{new string(plate.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray())}";

    /// <summary>
    /// True if <paramref name="eventId"/> should be stored. False for a repeat; <paramref name="originalId"/> is then
    /// the stored event it repeats. Check and update are one atomic step, so parallel requests can't both pass.
    /// </summary>
    public bool TryAccept(string key, Guid eventId, out Guid originalId)
    {
        originalId = eventId;
        if (_window == TimeSpan.Zero) return true;

        var now = time.GetUtcNow();
        lock (_lock)
        {
            Prune(now);
            if (_seen.TryGetValue(key, out var last) && now - last.LastSeen < _window)
            {
                originalId = last.EventId;
                _seen[key] = last with { LastSeen = now };
                return false;
            }
            _seen[key] = new Sighting(eventId, now);
            return true;
        }
    }

    /// <summary>Undoes <see cref="TryAccept"/> when the event couldn't be stored, so the camera's next try gets in.</summary>
    public void Forget(string key, Guid eventId)
    {
        lock (_lock)
        {
            if (_seen.TryGetValue(key, out var s) && s.EventId == eventId)
                _seen.Remove(key);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (now - _lastPrune < _window) return;
        _lastPrune = now;
        foreach (var (key, s) in _seen)
            if (now - s.LastSeen >= _window) _seen.Remove(key);
    }
}
