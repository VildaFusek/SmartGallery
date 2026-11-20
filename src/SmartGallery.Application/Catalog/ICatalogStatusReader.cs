namespace SmartGallery.Application.Catalog;

public interface ICatalogStatusReader
{
    Task<CatalogStatus> ReadAsync(CancellationToken cancellationToken);
}
