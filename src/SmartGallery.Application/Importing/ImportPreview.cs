using SmartGallery.Domain;

namespace SmartGallery.Application.Importing;

public enum ImportPreviewState
{
    Proposed,
    Conflict,
    Blocked
}

public sealed record ImportPreviewItem(
    string SourcePath,
    string DestinationPath,
    MediaKind Kind,
    long? SizeBytes,
    DateTimeOffset? LastWriteTimeUtc,
    ImportPreviewState State,
    string Note)
{
    public DateTimeOffset? OriginalDate { get; init; }

    public DateTimeOffset? OrganizationDate { get; init; }

    public string DateSource { get; init; } = "";

    public string Description =>
        $"{(Kind == MediaKind.Photo ? "Fotografie" : "Video")} | {SizeBytes?.ToString() ?? "?"} B | " +
        $"čas změny UTC: {LastWriteTimeUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "?"}";
}

public sealed record ImportPreview(
    IReadOnlyList<ImportPreviewItem> Items,
    int InspectedEntries,
    int UnsupportedFiles,
    bool IsComplete,
    string? Warning,
    LibraryFolderLayout FolderLayout,
    LibraryFileNaming FileNaming);
