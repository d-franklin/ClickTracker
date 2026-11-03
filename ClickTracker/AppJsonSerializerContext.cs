using System.Text.Json.Serialization;

namespace ClickTracker;

/// <summary>
/// Source-generated JSON metadata. Every type that crosses the HTTP boundary or is read from disk is
/// registered here, because reflection-based serialization is unavailable under Native AOT.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string[]>))]
[JsonSerializable(typeof(Admin.LinkDto))]
[JsonSerializable(typeof(Admin.LinkDto[]))]
[JsonSerializable(typeof(Admin.UpdateLinkRequest))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
