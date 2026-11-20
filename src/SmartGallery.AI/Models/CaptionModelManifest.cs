using System.Text.Json.Serialization;

namespace SmartGallery.AI.Models;

internal sealed record CaptionModelManifest(
    int SchemaVersion,
    string ModelId,
    string Repository,
    string Revision,
    IReadOnlyList<CaptionModelFile> Files);

internal sealed record CaptionModelFile(string Path, long Bytes, string Sha256, string? Url = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CaptionModelManifest))]
[JsonSerializable(typeof(CaptionVerificationRecord))]
internal partial class CaptionModelManifestJsonContext : JsonSerializerContext
{
}
