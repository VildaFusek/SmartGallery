using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;

namespace SmartGallery.Infrastructure.Persistence;

public sealed class SqliteLibraryImportSettings(IDbContextFactory<CatalogDbContext> contextFactory)
    : ILibraryImportSettings
{
    public Task SaveAsync(
        Guid libraryId,
        LibraryFolderLayout folderLayout,
        LibraryFileNaming fileNaming,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(folderLayout))
        {
            throw new ArgumentOutOfRangeException(nameof(folderLayout), "Neznámá struktura složek.");
        }
        if (!Enum.IsDefined(fileNaming))
        {
            throw new ArgumentOutOfRangeException(nameof(fileNaming), "Neznámé pravidlo pojmenování.");
        }

        return Task.Run(
            () => SaveCoreAsync(libraryId, folderLayout, fileNaming, cancellationToken),
            cancellationToken);
    }

    private async Task SaveCoreAsync(
        Guid libraryId,
        LibraryFolderLayout folderLayout,
        LibraryFileNaming fileNaming,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var library = await context.Libraries.SingleOrDefaultAsync(
            candidate => candidate.Id == libraryId, cancellationToken)
            ?? throw new InvalidOperationException("Knihovna již není v katalogu.");
        if (await context.Assets.AnyAsync(asset => asset.LibraryId == libraryId, cancellationToken))
        {
            throw new InvalidOperationException(
                "Pravidla knihovny s importovanými fotografiemi zatím nelze měnit.");
        }

        var entry = context.Entry(library);
        entry.Property(candidate => candidate.FolderLayout).CurrentValue = folderLayout;
        entry.Property(candidate => candidate.FileNaming).CurrentValue = fileNaming;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
