using SmartGallery.Domain;

namespace SmartGallery.Application.Catalog;

public interface IManagedLibraryRegistration
{
    Task<Library> CreateAsync(string rootPath, CancellationToken cancellationToken);
}
