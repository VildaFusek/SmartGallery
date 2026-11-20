namespace SmartGallery.Services;

public interface IImportDialogService
{
    Task AnalyzeFolderAsync(Guid libraryId, string sourceDirectory, CancellationToken cancellationToken);

    Task ImportFolderAsync(Guid libraryId, string sourceDirectory, CancellationToken cancellationToken);

    Task CancelAndWaitAsync();
}
