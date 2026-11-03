namespace ClickTracker.Admin;

/// <summary>Settings for the admin API and its API key.</summary>
public sealed class AdminOptions
{
    /// <summary>The admin API key, used when no other source supplies one.</summary>
    public string? ApiKey { get; set; }

    /// <summary>How to reach OpenBao for the admin API key.</summary>
    public OpenBaoOptions OpenBao { get; set; } = new();
}

/// <summary>Connection settings for the OpenBao secret store.</summary>
public sealed class OpenBaoOptions
{
    /// <summary>The OpenBao base address, for example <c>https://127.0.0.1:8200</c>.</summary>
    public string? Address { get; set; }

    /// <summary>The path of the secret holding the admin API key.</summary>
    public string? SecretPath { get; set; } = "secret/data/clicktracker";
}
