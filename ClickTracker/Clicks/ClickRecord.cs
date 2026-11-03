namespace ClickTracker.Clicks;

/// <summary>An immutable snapshot of one click.</summary>
/// <param name="OccurredAtUtc">When the request was received.</param>
/// <param name="LinkId">The short ID that was clicked.</param>
/// <param name="TargetUrl">The URL that was served.</param>
/// <param name="ClientAddress">The client address, or null when it is unknown.</param>
/// <param name="Method">The request method.</param>
/// <param name="Scheme">The request scheme.</param>
/// <param name="Host">The request host.</param>
/// <param name="Headers">Every request header, copied out of the request.</param>
public sealed record ClickRecord(
    DateTimeOffset OccurredAtUtc,
    string LinkId,
    string TargetUrl,
    string? ClientAddress,
    string Method,
    string Scheme,
    string Host,
    IReadOnlyDictionary<string, string[]> Headers);
