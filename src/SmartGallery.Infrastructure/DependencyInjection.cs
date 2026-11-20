using Microsoft.Extensions.DependencyInjection;
using SmartGallery.Application.Catalog;
using SmartGallery.Application.Importing;
using SmartGallery.Infrastructure.Importing;
using SmartGallery.Infrastructure.Persistence;

namespace SmartGallery.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSmartGalleryCatalog(this IServiceCollection services)
    {
        services.AddDbContextFactory<CatalogDbContext>(
            options => CatalogDatabaseConfiguration.Configure(options, allowCreate: false));
        services.AddSingleton<ILibraryCatalog, SqliteLibraryCatalog>();
        services.AddSingleton<IGalleryCatalog, SqliteGalleryCatalog>();
        services.AddSingleton<ICatalogStatusReader, SqliteCatalogStatusReader>();
        services.AddSingleton<IManagedLibraryRegistration, SqliteManagedLibraryRegistration>();
        services.AddSingleton<ILibraryImportSettings, SqliteLibraryImportSettings>();
        services.AddSingleton<IImportPreviewService, FolderImportPreviewService>();
        services.AddSingleton<IFolderImportService, FolderImportService>();

        return services;
    }
}
