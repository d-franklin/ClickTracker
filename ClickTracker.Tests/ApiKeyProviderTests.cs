using System.Net;
using System.Text;
using ClickTracker.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClickTracker.Tests;

public sealed class ApiKeyProviderTests
{
    private const string VaultKey = "vault-key";

    [Fact]
    public async Task It_validates_the_key_from_the_configuration()
    {
        using var environment = new EnvironmentScope();

        var provider = CreateProvider(new AdminOptions { ApiKey = "config-key" });

        Assert.True(await provider.ValidateAsync("config-key", TestContext.Current.CancellationToken));
        Assert.False(await provider.ValidateAsync("wrong", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task It_validates_the_key_from_the_environment()
    {
        using var environment = new EnvironmentScope();
        environment.Set(ApiKeyProvider.ApiKeyEnvironmentVariable, "env-key");

        var provider = CreateProvider(new AdminOptions());

        Assert.True(await provider.ValidateAsync("env-key", TestContext.Current.CancellationToken));
        Assert.False(await provider.ValidateAsync("config-key", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task It_validates_the_key_from_openbao()
    {
        using var environment = new EnvironmentScope();
        environment.Set("OPENBAO_TOKEN", "vault-token");

        var provider = CreateProvider(OpenBaoOptions(), _ => JsonResponse(VaultKey));

        Assert.True(await provider.ValidateAsync(VaultKey, CancellationToken.None));
        Assert.False(await provider.ValidateAsync("wrong", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task It_refreshes_openbao_when_the_presented_key_does_not_match()
    {
        using var environment = new EnvironmentScope();
        environment.Set("OPENBAO_TOKEN", "vault-token");

        var calls = 0;
        var provider = CreateProvider(OpenBaoOptions(), _ =>
        {
            calls++;
            return JsonResponse(calls == 1 ? "old-key" : "new-key");
        });

        Assert.True(await provider.ValidateAsync("new-key", TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task It_rejects_everything_when_no_key_is_configured()
    {
        using var environment = new EnvironmentScope();

        var provider = CreateProvider(new AdminOptions());

        Assert.False(await provider.ValidateAsync("anything", TestContext.Current.CancellationToken));
        Assert.False(await provider.ValidateAsync(null, TestContext.Current.CancellationToken));
    }

    private static AdminOptions OpenBaoOptions() => new()
    {
        OpenBao = new OpenBaoOptions
        {
            Address = "https://vault.example",
            SecretPath = "secret/data/clicktracker",
        },
    };

    private static ApiKeyProvider CreateProvider(
        AdminOptions options,
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new StubHandler(responder ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var reader = new OpenBaoSecretReader(
            new HttpClient(handler),
            Options.Create(options),
            NullLogger<OpenBaoSecretReader>.Instance);

        return new ApiKeyProvider(reader, Options.Create(options), NullLogger<ApiKeyProvider>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string key)
    {
        var json = $$"""{ "data": { "data": { "adminApiKey": "{{key}}" } } }""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }

    /// <summary>Sets environment variables for one test and puts them back afterwards.</summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

        public void Set(string name, string? value)
        {
            _original.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
