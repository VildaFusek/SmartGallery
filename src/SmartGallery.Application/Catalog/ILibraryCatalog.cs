using SmartGallery.Domain;

namespace SmartGallery.Application.Catalog;

/// <summary>
/// Read boundary for catalog persistence.
/// </summary>
public interface ILibraryCatalog
{
    Task<IReadOnlyList<Library>> GetLibrariesAsync(CancellationToken cancellationToken);
}
