namespace ClickTracker.Links;

/// <summary>The rules a link definition has to satisfy before it is stored.</summary>
public static class LinkValidation
{
    /// <summary>The path prefix the admin API owns, which cannot be used as a link ID.</summary>
    public const string ReservedIdPrefix = "admin";

    /// <summary>Checks that a link ID can be used as a path segment.</summary>
    public static bool IsValidId(string id) =>
        !string.IsNullOrEmpty(id)
        && !id.Contains('/')
        && !id.Any(char.IsWhiteSpace)
        && !id.StartsWith(ReservedIdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks that a target is an absolute HTTP or HTTPS URL.</summary>
    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
