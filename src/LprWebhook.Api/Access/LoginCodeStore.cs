using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace LprWebhook.Api.Access;

/// <summary>
/// Single-use 6-digit web login codes handed out by the bot's /weblogin command. Kept in memory only:
/// a restart simply invalidates outstanding codes.
/// </summary>
public class LoginCodeStore(IMemoryCache cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Six digits are guessable, so after this many wrong codes within <see cref="Lifetime"/> every code is refused
    /// until the window ends. With a handful of live codes that keeps the odds of a lucky guess around 1 in 100,000.
    /// </summary>
    public const int MaxFailures = 10;

    private const string Prefix = "weblogin:";
    private const string FailuresKey = "weblogin-failures";
    private readonly Lock _lock = new();

    public string Create(long userId)
    {
        lock (_lock)
        {
            string code;
            do code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            while (cache.TryGetValue(Prefix + code, out _));
            cache.Set(Prefix + code, userId, Lifetime);
            return code;
        }
    }

    /// <summary>Returns the user ID and forgets the code, or null if it's unknown, used, expired or locked out.</summary>
    public long? Redeem(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        lock (_lock)
        {
            var failures = cache.Get<Counter>(FailuresKey);
            if (failures?.Count >= MaxFailures) return null;

            var key = Prefix + code.Trim();
            if (cache.TryGetValue(key, out long userId))
            {
                cache.Remove(key);
                return userId;
            }

            // The window starts at the first wrong code and isn't extended by later ones.
            if (failures is null) cache.Set(FailuresKey, new Counter { Count = 1 }, Lifetime);
            else failures.Count++;
            return null;
        }
    }

    private sealed class Counter
    {
        public int Count;
    }
}
