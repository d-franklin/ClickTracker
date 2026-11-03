using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ClickTracker.Tests;

/// <summary>A host environment for tests that build a type by hand.</summary>
public sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";

    public string ApplicationName { get; set; } = "ClickTracker.Tests";

    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>A temporary directory that removes itself when the test ends.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = Directory.CreateTempSubdirectory("clicktracker-tests-").FullName;
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // The test does not depend on the cleanup succeeding.
        }
    }
}
