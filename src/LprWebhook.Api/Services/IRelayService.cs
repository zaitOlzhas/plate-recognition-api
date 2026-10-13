namespace LprWebhook.Api.Services;

public interface IRelayService
{
    Task<RelayResult> TriggerRelayAsync(CancellationToken ct = default);
}

public record RelayResult(bool Success, string Message, int StatusCode = 0);
