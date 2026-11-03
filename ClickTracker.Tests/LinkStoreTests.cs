using ClickTracker.Links;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClickTracker.Tests;

public sealed class LinkStoreTests
{
    private static LinkStore CreateStore(string path) =>
        new(
            Options.Create(new LinksOptions { FilePath = path }),
            new TestHostEnvironment(),
            NullLogger<LinkStore>.Instance);

    [Fact]
    public void Load_reads_the_links_from_the_file()
    {
        using var directory = new TempDirectory();
        var path = directory.File("links.json");
        File.WriteAllText(path, """{ "gh": "https://github.com/example", "docs": "https://docs.example.com" }""");

        using var store = CreateStore(path);
        store.Load();

        Assert.True(store.TryGetTarget("gh", out var target));
        Assert.Equal("https://github.com/example", target);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Load_starts_with_no_links_when_the_file_is_missing()
    {
        using var directory = new TempDirectory();
        using var store = CreateStore(directory.File("links.json"));

        store.Load();

        Assert.Empty(store.List());
    }

    [Fact]
    public void Load_throws_when_the_file_is_malformed()
    {
        using var directory = new TempDirectory();
        var path = directory.File("links.json");
        File.WriteAllText(path, "{ not json");

        using var store = CreateStore(path);

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => store.Load());
    }

    [Fact]
    public void Link_ids_are_matched_exactly()
    {
        using var directory = new TempDirectory();
        var path = directory.File("links.json");
        File.WriteAllText(path, """{ "GH": "https://github.com/example" }""");

        using var store = CreateStore(path);
        store.Load();

        Assert.False(store.TryGetTarget("gh", out _));
        Assert.True(store.TryGetTarget("GH", out _));
    }

    [Fact]
    public void Upsert_writes_the_file_and_updates_the_snapshot()
    {
        using var directory = new TempDirectory();
        var path = directory.File("links.json");
        File.WriteAllText(path, """{ "gh": "https://github.com/example" }""");

        using var store = CreateStore(path);
        store.Load();
        store.Upsert("docs", "https://docs.example.com");

        Assert.True(store.TryGetTarget("docs", out var target));
        Assert.Equal("https://docs.example.com", target);

        var written = File.ReadAllText(path);
        Assert.Contains("https://docs.example.com", written, StringComparison.Ordinal);
        Assert.Contains("https://github.com/example", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Upsert_rejects_a_target_that_is_not_an_absolute_http_url()
    {
        using var directory = new TempDirectory();
        using var store = CreateStore(directory.File("links.json"));
        store.Load();

        Assert.Throws<InvalidLinkException>(() => store.Upsert("docs", "not-a-url"));
        Assert.Throws<InvalidLinkException>(() => store.Upsert("docs", "ftp://example.com"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("with/slash")]
    [InlineData("with space")]
    [InlineData("admin")]
    [InlineData("AdminThing")]
    public void Upsert_rejects_an_id_that_cannot_be_used_as_a_path_segment(string id)
    {
        using var directory = new TempDirectory();
        using var store = CreateStore(directory.File("links.json"));
        store.Load();

        Assert.Throws<InvalidLinkException>(() => store.Upsert(id, "https://example.com"));
    }

    [Fact]
    public void Delete_removes_the_link_from_the_file_and_the_snapshot()
    {
        using var directory = new TempDirectory();
        var path = directory.File("links.json");
        File.WriteAllText(path, """{ "gh": "https://github.com/example", "docs": "https://docs.example.com" }""");

        using var store = CreateStore(path);
        store.Load();

        Assert.True(store.Delete("gh"));
        Assert.False(store.TryGetTarget("gh", out _));
        Assert.DoesNotContain("github.com", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_returns_false_for_an_unknown_id()
    {
        using var directory = new TempDirectory();
        using var store = CreateStore(directory.File("links.json"));
        store.Load();

        Assert.False(store.Delete("nothere"));
    }

    [Fact]
    public async Task The_watcher_reloads_when_the_file_changes()
    {
        using var directory = new TempDirectory();
        var path = directory.File("links.json");
        File.WriteAllText(path, """{ "one": "https://one.example" }""");

        using var store = CreateStore(path);
        store.Load();
        store.StartWatching();

        await Task.Delay(200, TestContext.Current.CancellationToken);
        File.WriteAllText(path, """{ "one": "https://one.example", "two": "https://two.example" }""");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (store.TryGetTarget("two", out _))
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The link file was not reloaded.");
    }
}
