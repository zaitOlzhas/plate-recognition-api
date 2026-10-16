using LprWebhook.Api.Access;
using Microsoft.Extensions.Caching.Memory;

namespace LprWebhook.Api.Tests;

public sealed class LoginCodeStoreTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly LoginCodeStore _store;

    public LoginCodeStoreTests() => _store = new LoginCodeStore(_cache);

    public void Dispose() => _cache.Dispose();

    [Fact]
    public void Codes_are_six_digits_and_work_once()
    {
        var code = _store.Create(42);

        Assert.Matches("^[0-9]{6}$", code);
        Assert.Equal(42, _store.Redeem($" {code} "));
        Assert.Null(_store.Redeem(code));
    }

    [Fact]
    public void Too_many_wrong_codes_lock_out_every_code()
    {
        var code = _store.Create(42);
        var wrong = code == "000000" ? "000001" : "000000";

        for (var i = 0; i < LoginCodeStore.MaxFailures; i++)
            Assert.Null(_store.Redeem(wrong));

        Assert.Null(_store.Redeem(code));
    }
}
