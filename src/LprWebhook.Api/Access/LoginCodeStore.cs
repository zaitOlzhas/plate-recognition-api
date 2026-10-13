using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace LprWebhook.Api.Access;

/// <summary>
/// Single-use web login codes handed out by the bot's /weblogin command. Kept in memory only:
/// a restart simply invalidates outstanding codes.
/// </summary>
public class LoginCodeStore(IMemoryCache cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const string Prefix = "weblogin:";

    public string Create(long userId)
    {
        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        cache.Set(Prefix + code, userId, Lifetime);
        return code;
    }

    /// <summary>Returns the user ID and forgets the code, or null if it's unknown, used or expired.</summary>
    public long? Redeem(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var key = Prefix + code.Trim();
        if (!cache.TryGetValue(key, out long userId)) return null;
        cache.Remove(key);
        return userId;
    }
}
