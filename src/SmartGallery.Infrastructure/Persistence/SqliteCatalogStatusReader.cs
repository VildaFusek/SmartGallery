using Microsoft.EntityFrameworkCore;
using SmartGallery.Application.Catalog;

namespace SmartGallery.Infrastructure.Persistence;

public sealed class SqliteCatalogStatusReader(IDbContextFactory<CatalogDbContext> contextFactory)
    : ICatalogStatusReader
{
    // SQLite's async APIs can execute synchronously; keep this work off the UI thread.
    public Task<CatalogStatus> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadCoreAsync(cancellationToken), cancellationToken);

    private async Task<CatalogStatus> ReadCoreAsync(CancellationToken cancellationToken)
    {
        await CatalogFirstRun.EnsureExistsAsync(cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pendingMigrations.Any())
        {
            throw new InvalidOperationException(
                "Databáze vyžaduje aktualizaci. Proveď Update-Database podle docs/database.md.");
        }

        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var journalMode = result as string;
        if (string.IsNullOrWhiteSpace(journalMode))
        {
            throw new InvalidOperationException("Nelze zjistit režim SQLite journal.");
        }

        var libraryCount = await context.Libraries.LongCountAsync(cancellationToken);
        var hasAssets = await context.Assets.AnyAsync(cancellationToken);

        return new CatalogStatus(libraryCount, hasAssets, journalMode);
    }
}
