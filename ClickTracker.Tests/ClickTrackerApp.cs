using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ClickTracker.Tests;

/// <summary>Hosts the application against a temporary link file and database.</summary>
public sealed class ClickTrackerApp : WebApplicationFactory<Program>
{
    public const string AdminApiKey = "test-admin-key";

    public TempDirectory Directory { get; } = new();

    public string LinksPath => Directory.File("links.json");

    public string DatabasePath => Directory.File("clicks.db");

    public void WriteLinks(params (string Id, string Url)[] links)
    {
        var json = string.Join(",\n", links.Select(link => $"  \"{link.Id}\": \"{link.Url}\""));
        File.WriteAllText(LinksPath, $"{{\n{json}\n}}\n");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Links:FilePath"] = LinksPath,
                ["Clicks:DatabasePath"] = DatabasePath,
                ["Clicks:BatchIntervalMs"] = "10",
                ["Admin:ApiKey"] = AdminApiKey,
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            Directory.Dispose();
        }
    }
}
