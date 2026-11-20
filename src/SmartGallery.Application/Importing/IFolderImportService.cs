namespace SmartGallery.Application.Importing;

public sealed record ImportOperationProgress(
    string Phase,
    int Discovered,
    int Imported,
    int Failed,
    int Skipped,
    long FileBytes,
    long FileTotalBytes,
    string? Message);

public sealed record FolderImportResult(int Imported, int Failed, int Skipped, int Uncataloged, bool IsComplete);

public interface IFolderImportService
{
    Task<FolderImportResult> ImportAsync(
        Guid libraryId,
        string sourceDirectory,
        IProgress<ImportOperationProgress> progress,
        CancellationToken cancellationToken);
}
