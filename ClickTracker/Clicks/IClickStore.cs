namespace ClickTracker.Clicks;

/// <summary>
/// The storage seam for click data. An OpenTelemetry or Grafana Alloy implementation replaces the
/// SQLite one without touching the request path.
/// </summary>
public interface IClickStore
{
    /// <summary>Appends a batch of clicks to the store.</summary>
    Task WriteAsync(IReadOnlyList<ClickRecord> records, CancellationToken cancellationToken);
}
