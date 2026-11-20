using SmartGallery.Domain;

namespace SmartGallery.Application.Catalog;

public sealed record GalleryTrashItem(string Batch, string RelativePath, MediaKind Kind,
    long SizeBytes, DateTimeOffset LastWriteTimeUtc, DateTimeOffset TrashedAt);

public sealed record GalleryTrashEmptyResult(int RemovedCount, string? Interruption);
