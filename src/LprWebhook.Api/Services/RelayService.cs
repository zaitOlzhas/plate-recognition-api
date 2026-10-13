using System.Text.Json;
using LprWebhook.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LprWebhook.Api.Services;

public class RelayService : IRelayService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RelayService> _logger;
    private readonly TelegramBotOptions _options;

    public RelayService(
        HttpClient httpClient,
        IOptions<TelegramBotOptions> options,
        ILogger<RelayService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<RelayResult> TriggerRelayAsync(CancellationToken ct = default)
    {
        var url = _options.Relay.GetFullUrl();

        try
        {
            _logger.LogInformation("Triggering relay at {Url}", url);

            var response = await _httpClient.PostAsync(url, null, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            _logger.LogInformation(
                "Relay response: StatusCode={StatusCode}, Body={Body}",
                response.StatusCode, content);

            if (!response.IsSuccessStatusCode)
            {
                return new RelayResult(
                    false,
                    $"Relay responded with {response.StatusCode}",
                    (int)response.StatusCode);
            }

            // Parse expected response: {"status": "ok", "message": "out1_pulse"}
            try
            {
                var json = JsonDocument.Parse(content);
                var status = json.RootElement.GetProperty("status").GetString();
                var message = json.RootElement.GetProperty("message").GetString();

                if (status == "ok")
                {
                    return new RelayResult(true, message ?? "Gate triggered successfully", 200);
                }

                return new RelayResult(false, $"Unexpected status: {status}", 200);
            }
            catch (JsonException)
            {
                return new RelayResult(
                    false,
                    "Relay response was not in expected JSON format",
                    200);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to relay at {Url}", url);
            return new RelayResult(false, $"Connection error: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Relay request timed out");
            return new RelayResult(false, "Request timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error triggering relay");
            return new RelayResult(false, $"Error: {ex.Message}");
        }
    }
}
