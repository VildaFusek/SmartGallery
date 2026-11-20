namespace SmartGallery.Application.Importing;

public interface IImportPreviewService
{
    Task<ImportPreview> PreviewFolderAsync(
        Guid libraryId, string sourceDirectory, CancellationToken cancellationToken);

    Task<ImportPreview> PreviewFolderAsync(
        Guid libraryId,
        string sourceDirectory,
        IProgress<ImportAnalysisProgress>? progress,
        CancellationToken cancellationToken);
}
