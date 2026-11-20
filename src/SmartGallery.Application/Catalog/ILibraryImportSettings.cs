using SmartGallery.Domain;

namespace SmartGallery.Application.Catalog;

public interface ILibraryImportSettings
{
    Task SaveAsync(
        Guid libraryId,
        LibraryFolderLayout folderLayout,
        LibraryFileNaming fileNaming,
        CancellationToken cancellationToken);
}
