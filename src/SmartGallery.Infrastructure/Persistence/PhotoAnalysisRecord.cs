namespace SmartGallery.Infrastructure.Persistence;

internal sealed class PhotoAnalysisRecord
{
    public Guid FileId { get; init; }
    public required string Caption { get; init; }
    public bool Truncated { get; init; }
    public string? SuggestedName { get; init; }
    public long AnalyzedAtUtcTicks { get; init; }
}
