namespace ClickTracker.Clicks;

/// <summary>Settings for click capture and persistence.</summary>
public sealed class ClicksOptions
{
    /// <summary>Path to the SQLite database file.</summary>
    public string DatabasePath { get; set; } = "clicks.db";

    /// <summary>How many captured clicks may wait to be written before the oldest is dropped.</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>The largest number of clicks written in one batch.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>How long a batch waits for more clicks before it is written.</summary>
    public int BatchIntervalMs { get; set; } = 250;
}
