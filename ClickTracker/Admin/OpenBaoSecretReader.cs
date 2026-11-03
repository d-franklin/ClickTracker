using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ClickTracker.Admin;

/// <summary>
/// Reads the admin API key from OpenBao. The secret is expected at
/// <c>{Address}/v1/{SecretPath}</c> with the field <c>adminApiKey</c> under <c>data.data</c>.
/// </summary>
public sealed class OpenBaoSecretReader
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<AdminOptions> _options;
    private readonly ILogger<OpenBaoSecretReader> _logger;

    public OpenBaoSecretReader(HttpClient httpClient, IOptions<AdminOptions> options, ILogger<OpenBaoSecretReader> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    /// <summary>Returns the admin API key, or null when OpenBao is not configured or cannot be reached.</summary>
    public async Task<string?> ReadAdminApiKeyAsync(CancellationToken cancellationToken)
    {
        var address = Environment.GetEnvironmentVariable("OPENBAO_ADDR") ?? _options.Value.OpenBao.Address;
        var token = Environment.GetEnvironmentVariable("OPENBAO_TOKEN");
        var secretPath = Environment.GetEnvironmentVariable("OPENBAO_SECRET_PATH") ?? _options.Value.OpenBao.SecretPath;

        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(secretPath))
        {
            return null;
        }

        var url = $"{address.TrimEnd('/')}/v1/{secretPath.TrimStart('/')}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Vault-Token", token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Could not read the admin API key from OpenBao at {Address}", address);
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "OpenBao returned {StatusCode} for {SecretPath}",
                    (int)response.StatusCode,
                    secretPath);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (document.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("data", out var secret)
                && secret.TryGetProperty("adminApiKey", out var key)
                && key.ValueKind == JsonValueKind.String)
            {
                return key.GetString();
            }

            _logger.LogWarning("The OpenBao secret {SecretPath} has no adminApiKey field", secretPath);
            return null;
        }
    }
}
