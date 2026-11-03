namespace ClickTracker.Clicks;

/// <summary>
/// Builds a click record from the request. The request is recycled once the response completes, so
/// everything the record needs is copied out of it here.
/// </summary>
public static class ClickCapture
{
    public static ClickRecord Capture(HttpContext context, string linkId, string targetUrl)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in context.Request.Headers)
        {
            headers[header.Key] = header.Value.Select(value => value!).ToArray();
        }

        return new ClickRecord(
            DateTimeOffset.UtcNow,
            linkId,
            targetUrl,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Method,
            context.Request.Scheme,
            context.Request.Host.Value ?? string.Empty,
            headers);
    }
}
