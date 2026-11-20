namespace SmartGallery.Domain;

public sealed record AssetFile(
    Guid Id,
    Guid AssetId,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc);
