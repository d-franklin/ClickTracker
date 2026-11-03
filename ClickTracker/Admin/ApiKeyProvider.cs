using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ClickTracker.Admin;

/// <summary>
/// Resolves the admin API key and checks a presented key against it. The key comes from OpenBao
/// first, then the environment, then application configuration.
/// </summary>
public sealed class ApiKeyProvider
{
    /// <summary>The environment variable that holds the admin API key.</summary>
    public const string ApiKeyEnvironmentVariable = "CLICKTRACKER_ADMIN_API_KEY";

    private readonly OpenBaoSecretReader _openBao;
    private readonly IOptions<AdminOptions> _options;
    private readonly ILogger<ApiKeyProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _resolvedKey;
    private bool _resolved;

    public ApiKeyProvider(
        OpenBaoSecretReader openBao,
        IOptions<AdminOptions> options,
        ILogger<ApiKeyProvider> logger)
    {
        _openBao = openBao;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Checks a presented key. A key that does not match triggers one refresh, so a key that was
    /// rotated in OpenBao starts working without a restart.
    /// </summary>
    public async Task<bool> ValidateAsync(string? presented, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var key = await ResolveAsync(forceRefresh: false, cancellationToken);
        if (key is not null && FixedTimeEquals(key, presented))
        {
            return true;
        }

        var refreshed = await ResolveAsync(forceRefresh: true, cancellationToken);
        return refreshed is not null && FixedTimeEquals(refreshed, presented);
    }

    private async Task<string?> ResolveAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _resolved)
            {
                return _resolvedKey;
            }

            var fromOpenBao = await _openBao.ReadAdminApiKeyAsync(cancellationToken);
            var fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
            var fromConfiguration = _options.Value.ApiKey;

            var key = FirstNonEmpty(fromOpenBao, fromEnvironment, fromConfiguration);
            if (key is null)
            {
                _logger.LogWarning(
                    "No admin API key is configured. Set {EnvironmentVariable}, OpenBao, or Admin:ApiKey.",
                    ApiKeyEnvironmentVariable);
            }

            _resolvedKey = key;
            _resolved = true;
            return key;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

    private static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)));
}
