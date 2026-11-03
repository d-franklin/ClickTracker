namespace ClickTracker.Links;

/// <summary>Settings for the link definition file.</summary>
public sealed class LinksOptions
{
    /// <summary>Path to the JSON file that maps link IDs to target URLs.</summary>
    public string FilePath { get; set; } = "links.json";
}
