namespace SmartGallery.Domain;

public sealed record Asset(Guid Id, Guid LibraryId, DateTimeOffset ImportedAt);
