using SmartGallery.Domain;

namespace SmartGallery.Application.Catalog;

public sealed record GalleryFile(Guid Id, string RelativePath, MediaKind Kind)
{
    public string Name => Path.GetFileName(RelativePath);
    public string Folder => Path.GetDirectoryName(RelativePath) ?? "";
    public long SizeBytes { get; init; }
    public DateTimeOffset LastWriteTimeUtc { get; init; }
    public GalleryPhotoAnalysis? Analysis { get; init; }
    public bool HasPendingName => Analysis?.SuggestedName is { } name &&
        !string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
}

public sealed record GalleryPhotoAnalysis(string Caption, bool Truncated, string? SuggestedName, long AnalyzedAtUtcTicks);

public sealed record GalleryCatalogAudit(IReadOnlySet<Guid> HiddenFileIds, int RemovedCount, string? BackupPath, string? BackupError);

public interface IGalleryCatalog
{
    Task<IReadOnlyList<GalleryFile>> GetFilesAsync(Guid libraryId, CancellationToken cancellationToken);

    Task SavePhotoAnalysisAsync(Library library, GalleryFile file, GalleryPhotoAnalysis analysis,
        CancellationToken cancellationToken);

    Task<string> GetReadPathAsync(string root, GalleryFile file, CancellationToken cancellationToken);

    Task<GalleryCatalogAudit> AuditFilesAsync(CancellationToken cancellationToken);

    Task<string> AddConvertedVideoAsync(Library library, GalleryFile source, string temporaryVideo,
        CancellationToken cancellationToken);

    Task<string> ReplaceConvertedVideoAsync(Library library, GalleryFile source, string temporaryVideo,
        CancellationToken cancellationToken);

    Task<string> PublishConvertedPhotoAsync(Library library, GalleryFile source, string temporaryPhoto,
        bool replaceOriginal, CancellationToken cancellationToken);

    Task<string> MoveToTrashAsync(Library library, GalleryFile file, CancellationToken cancellationToken);

    Task<string> MoveToTrashAsync(Library library, IReadOnlyList<GalleryFile> files, CancellationToken cancellationToken);

    Task<IReadOnlyList<GalleryTrashItem>> StagePermanentDeletionAsync(Library library,
        IReadOnlyList<GalleryFile> files, CancellationToken cancellationToken);

    Task<string> MoveDuplicatesToTrashAsync(Library library, IReadOnlyList<GalleryFile> files,
        IReadOnlyList<GalleryFile> retained, CancellationToken cancellationToken);

    Task RenamePhotoAsync(Library library, GalleryFile file, string newName, CancellationToken cancellationToken);

    Task RenameFileAsync(Library library, GalleryFile file, string newName, CancellationToken cancellationToken);

    Task<IReadOnlyList<GalleryTrashItem>> GetTrashAsync(Library library, CancellationToken cancellationToken);

    Task<string> GetTrashReadPathAsync(Library library, GalleryTrashItem item, CancellationToken cancellationToken);

    Task RestoreTrashAsync(Library library, IReadOnlyList<GalleryTrashItem> items, CancellationToken cancellationToken);

    Task<GalleryTrashEmptyResult> EmptyTrashAsync(Library library, IReadOnlyList<GalleryTrashItem> items,
        CancellationToken cancellationToken);
}
