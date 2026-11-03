using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ClickTracker.Tests;

public sealed class AdminApiTests
{
    private static readonly WebApplicationFactoryClientOptions NoRedirects = new() { AllowAutoRedirect = false };

    [Fact]
    public async Task The_admin_api_rejects_a_request_without_a_key()
    {
        using var app = new ClickTrackerApp();
        using var client = app.CreateClient(NoRedirects);

        using var response = await client.GetAsync("/admin/links", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_admin_api_rejects_a_request_with_the_wrong_key()
    {
        using var app = new ClickTrackerApp();
        using var client = app.CreateClient(NoRedirects);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/links");
        request.Headers.Add("X-Api-Key", "not-the-key");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_admin_api_lists_the_links()
    {
        using var app = new ClickTrackerApp();
        app.WriteLinks(("example", "https://example.com/target"));
        using var client = app.CreateClient(NoRedirects);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/links");
        request.Headers.Add("X-Api-Key", ClickTrackerApp.AdminApiKey);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var links = await response.Content.ReadFromJsonAsync<LinkDto[]>(TestContext.Current.CancellationToken);
        Assert.NotNull(links);
        var link = Assert.Single(links);
        Assert.Equal("example", link.Id);
        Assert.Equal("https://example.com/target", link.Url);
    }

    [Fact]
    public async Task The_admin_api_creates_a_link_and_writes_the_file()
    {
        using var app = new ClickTrackerApp();
        using var client = app.CreateClient(NoRedirects);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/admin/links/docs")
        {
            Content = JsonContent.Create(new { url = "https://docs.example.com" }),
        };
        request.Headers.Add("X-Api-Key", ClickTrackerApp.AdminApiKey);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("https://docs.example.com", await File.ReadAllTextAsync(app.LinksPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_admin_api_rejects_a_target_that_is_not_an_absolute_http_url()
    {
        using var app = new ClickTrackerApp();
        using var client = app.CreateClient(NoRedirects);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/admin/links/docs")
        {
            Content = JsonContent.Create(new { url = "not-a-url" }),
        };
        request.Headers.Add("X-Api-Key", ClickTrackerApp.AdminApiKey);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_admin_api_deletes_a_link()
    {
        using var app = new ClickTrackerApp();
        app.WriteLinks(("example", "https://example.com/target"));
        using var client = app.CreateClient(NoRedirects);

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/admin/links/example");
        request.Headers.Add("X-Api-Key", ClickTrackerApp.AdminApiKey);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain("example.com", await File.ReadAllTextAsync(app.LinksPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_admin_api_returns_not_found_when_deleting_an_unknown_link()
    {
        using var app = new ClickTrackerApp();
        using var client = app.CreateClient(NoRedirects);

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/admin/links/nothere");
        request.Headers.Add("X-Api-Key", ClickTrackerApp.AdminApiKey);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record LinkDto(string Id, string Url);
}
