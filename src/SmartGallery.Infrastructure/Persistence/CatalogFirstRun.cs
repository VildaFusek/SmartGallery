using Microsoft.EntityFrameworkCore;

namespace SmartGallery.Infrastructure.Persistence;

internal static class CatalogFirstRun
{
    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    internal static async Task EnsureExistsAsync(CancellationToken cancellationToken)
    {
        var path = CatalogDatabaseConfiguration.DatabasePath;
        if (File.Exists(path))
        {
            return;
        }
        RejectOrphanedSidecars(path);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new DbContextOptionsBuilder<CatalogDbContext>();
            CatalogDatabaseConfiguration.Configure(options, allowCreate: true, databasePath: temporaryPath, pooling: false);
            // Complete the schema off to the side and close SQLite before moving its main file.
            await using (var context = new CatalogDbContext(options.Options))
            {
                await context.Database.MigrateAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (HasSidecars(temporaryPath))
            {
                throw new IOException("Dočasný katalog SQLite se nepodařilo bezpečně uzavřít.");
            }
            if (File.Exists(path))
            {
                return;
            }
            RejectOrphanedSidecars(path);
            try
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another instance initialized the catalog first.
            }
        }
        finally
        {
            TryDelete(temporaryPath);
            foreach (var suffix in SidecarSuffixes)
            {
                TryDelete(temporaryPath + suffix);
            }
        }
    }

    private static bool HasSidecars(string path) =>
        SidecarSuffixes.Any(suffix => File.Exists(path + suffix));

    private static void RejectOrphanedSidecars(string path)
    {
        if (HasSidecars(path) && !File.Exists(path))
        {
            throw new IOException("Chybí soubor katalogu, ale zůstaly jeho pomocné soubory SQLite. Obnov katalog ze zálohy.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }
}
