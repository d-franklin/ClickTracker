namespace ClickTracker;

/// <summary>The proxies whose forwarded headers are trusted.</summary>
public sealed class ProxyOptions
{
    /// <summary>Networks whose forwarded headers are trusted, in CIDR notation.</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>Individual proxy addresses whose forwarded headers are trusted.</summary>
    public string[] KnownProxies { get; set; } = [];
}
