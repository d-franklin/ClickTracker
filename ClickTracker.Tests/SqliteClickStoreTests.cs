using ClickTracker.Clicks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClickTracker.Tests;

public sealed class SqliteClickStoreTests
{
    private static ClickRecord Click() =>
        new(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            "abc",
            "https://example.com/page",
            "203.0.113.4",
            "GET",
            "https",
            "short.example.com",
            new Dictionary<string, string[]> { ["User-Agent"] = ["test-agent"] });

    [Fact]
    public async Task It_writes_the_clicks_to_the_database()
    {
        using var directory = new TempDirectory();
        var path = directory.File("clicks.db");
        var store = new SqliteClickStore(
            Options.Create(new ClicksOptions { DatabasePath = path }),
            new TestHostEnvironment());

        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.WriteAsync([Click()], TestContext.Current.CancellationToken);

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT occurred_at_utc, link_id, target_url, client_address, method, scheme, host, headers_json
            FROM clicks;
            """;

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal("2026-01-02T03:04:05.0000000+00:00", reader.GetString(0));
        Assert.Equal("abc", reader.GetString(1));
        Assert.Equal("https://example.com/page", reader.GetString(2));
        Assert.Equal("203.0.113.4", reader.GetString(3));
        Assert.Equal("GET", reader.GetString(4));
        Assert.Equal("https", reader.GetString(5));
        Assert.Equal("short.example.com", reader.GetString(6));
        Assert.Contains("test-agent", reader.GetString(7), StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_creates_the_table_if_it_is_missing()
    {
        using var directory = new TempDirectory();
        var path = directory.File("clicks.db");
        var store = new SqliteClickStore(
            Options.Create(new ClicksOptions { DatabasePath = path }),
            new TestHostEnvironment());

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'clicks';";

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }
}
