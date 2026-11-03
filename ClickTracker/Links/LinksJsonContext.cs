using System.Text.Json.Serialization;

namespace ClickTracker.Links;

/// <summary>
/// Source-generated JSON metadata for the link definition file. The file is written indented because
/// people edit it by hand as well as through the admin API.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class LinksJsonContext : JsonSerializerContext
{
}
