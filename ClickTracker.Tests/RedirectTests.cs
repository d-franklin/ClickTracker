using System.Net;
using System.Text.Json;
using ClickTracker.Clicks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClickTracker.Tests;

public sealed class RedirectTests
{
    private static readonly WebApplicationFactoryClientOptions NoRedirects = new() { AllowAutoRedirect = false };

    [Fact]
    public async Task A_short_link_redirects_to_its_target()
    {
        using var app = new ClickTrackerApp();
        app.WriteLinks(("example", "https://example.com/target"));
        using var client = app.CreateClient(NoRedirects);

        using var response = await client.GetAsync("/example", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.com/target", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task An_unknown_link_returns_not_found()
    {
        using var app = new ClickTrackerApp();
        app.WriteLinks(("example", "https://example.com/target"));
        using var client = app.CreateClient(NoRedirects);

        using var response = await client.GetAsync("/nothere", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_head_request_is_redirected_but_not_recorded()
    {
        using var app = new ClickTrackerApp();
        app.WriteLinks(("example", "https://example.com/target"));
        using var client = app.CreateClient(NoRedirects);

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, "/example"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Empty(await ReadClicksAsync(app.DatabasePath));
    }

    [Fact]
    public async Task A_click_is_written_to_the_database()
    {
        using var app = new ClickTrackerApp();
        app.WriteLinks(("example", "https://example.com/target"));
        using var client = app.CreateClient(NoRedirects);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/example");
        request.Headers.Add("User-Agent", "clicktracker-test");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        var clicks = await WaitForClicksAsync(app.DatabasePath, 1);
        var click = Assert.Single(clicks);

        Assert.Equal("example", click.LinkId);
        Assert.Equal("https://example.com/target", click.TargetUrl);
        Assert.Equal("GET", click.Method);
        Assert.NotNull(click.HeadersJson);
        Assert.Contains("User-Agent", click.HeadersJson, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<List<ClickRow>> ReadClicksAsync(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return [];
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT link_id, target_url, method, headers_json FROM clicks;";

        var clicks = new List<ClickRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            clicks.Add(new ClickRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return clicks;
    }

    private static async Task<List<ClickRow>> WaitForClicksAsync(string databasePath, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        List<ClickRow> clicks = [];
        while (DateTime.UtcNow < deadline)
        {
            clicks = await ReadClicksAsync(databasePath);
            if (clicks.Count >= expected)
            {
                return clicks;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return clicks;
    }

    internal sealed record ClickRow(string LinkId, string TargetUrl, string Method, string HeadersJson);
}
