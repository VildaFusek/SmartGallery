using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;

namespace SmartGallery.Infrastructure.Persistence;

public sealed class SqliteManagedLibraryRegistration(IDbContextFactory<CatalogDbContext> contextFactory)
    : IManagedLibraryRegistration
{
    public Task<Library> CreateAsync(string rootPath, CancellationToken cancellationToken) =>
        Task.Run(() => CreateCoreAsync(rootPath, cancellationToken), cancellationToken);

    private async Task<Library> CreateCoreAsync(string rootPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("Cílová složka musí mít absolutní cestu.", nameof(rootPath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var directory = new DirectoryInfo(normalizedPath);
        if (!directory.Exists)
        {
            throw new InvalidOperationException("Cílová složka neexistuje nebo k ní není přístup.");
        }

        if (directory.Parent is null)
        {
            throw new InvalidOperationException("Kořen disku nebo sdílení nelze použít jako cílovou složku galerie.");
        }

        // Conservative policy: do not register a destination through junctions or symlinks.
        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Cílová cesta nesmí obsahovat symbolické odkazy ani junctions.");
            }
        }

        if (Directory.EnumerateFileSystemEntries(normalizedPath).Any())
        {
            throw new InvalidOperationException(
                "Zvol prázdnou cílovou složku. Zdrojové fotografie se budou vybírat až při importu.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Serialize registration across application instances; only one managed destination is supported now.
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var existing = await context.Libraries.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            if (string.Equals(existing.RootPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }

            throw new InvalidOperationException(
                "Cílová knihovna je již nastavena. Její změna zatím není podporována.");
        }

        var library = new Library(Guid.NewGuid(), directory.Name, normalizedPath, DateTimeOffset.UtcNow);
        context.Libraries.Add(library);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return library;
    }
}
