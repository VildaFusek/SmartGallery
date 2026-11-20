using Microsoft.EntityFrameworkCore;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;

namespace SmartGallery.Infrastructure.Persistence;

public sealed class SqliteLibraryCatalog(IDbContextFactory<CatalogDbContext> contextFactory) : ILibraryCatalog
{
    public Task<IReadOnlyList<Library>> GetLibrariesAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadCoreAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<Library>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Libraries
            .AsNoTracking()
            .OrderBy(library => library.Name)
            .ThenBy(library => library.Id)
            .ToListAsync(cancellationToken);
    }
}
