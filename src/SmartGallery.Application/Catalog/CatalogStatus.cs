namespace SmartGallery.Application.Catalog;

public sealed record CatalogStatus(long LibraryCount, bool HasAssets, string JournalMode);
