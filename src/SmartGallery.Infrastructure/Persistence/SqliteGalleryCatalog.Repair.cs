using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using SmartGallery.Infrastructure.Importing;

namespace SmartGallery.Infrastructure.Persistence;

public sealed partial class SqliteGalleryCatalog
{
    private enum Availability { Available, Missing, Unavailable }

    public Task<GalleryCatalogAudit> AuditFilesAsync(CancellationToken token) => Task.Run(async () =>
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token);
        var rows = from file in context.AssetFiles
                   join asset in context.Assets on file.AssetId equals asset.Id
                   join library in context.Libraries on asset.LibraryId equals library.Id
                   select new { File = file, library.RootPath };
        var snapshot = await rows.AsNoTracking().ToListAsync(token);
        var hidden = new HashSet<Guid>();
        var missing = new Dictionary<Guid, (AssetFile File, string RootPath)>();
        var directories = new Dictionary<string, Availability>(StringComparer.Ordinal);
        foreach (var row in snapshot)
        {
            token.ThrowIfCancellationRequested();
            var file = row.File;
            if (!IsSafeRelativePath(file.RelativePath) ||
                FolderImportPreviewService.GetMediaKind(Path.GetExtension(file.RelativePath)) is null)
            {
                continue;
            }
            var state = CheckCatalogFile(row.RootPath, file.RelativePath, directories);
            if (state != Availability.Available)
            {
                hidden.Add(file.Id);
            }
            if (state == Availability.Missing)
            {
                missing.Add(file.Id, (file, row.RootPath));
            }
        }
        if (missing.Count == 0)
        {
            return new GalleryCatalogAudit(hidden, 0, null, null);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(token);
        var candidates = new List<(AssetFile File, string RootPath)>();
        foreach (var ids in missing.Keys.Chunk(500))
        {
            foreach (var row in await rows.Where(row => ids.Contains(row.File.Id)).ToListAsync(token))
            {
                var before = missing[row.File.Id];
                if (row.RootPath == before.RootPath && row.File.AssetId == before.File.AssetId &&
                    row.File.RelativePath == before.File.RelativePath && row.File.SizeBytes == before.File.SizeBytes &&
                    row.File.LastWriteTimeUtc == before.File.LastWriteTimeUtc)
                {
                    candidates.Add((row.File, row.RootPath));
                }
                else
                {
                    hidden.Remove(row.File.Id);
                }
            }
        }
        if (candidates.Count == 0)
        {
            return new GalleryCatalogAudit(hidden, 0, null, null);
        }

        string backup;
        try
        {
            token.ThrowIfCancellationRequested();
            backup = BackupCatalog((SqliteConnection)context.Database.GetDbConnection());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            return new GalleryCatalogAudit(hidden, 0, null, exception.Message);
        }

        directories.Clear();
        var remove = new List<AssetFile>();
        foreach (var (file, root) in candidates)
        {
            token.ThrowIfCancellationRequested();
            switch (CheckCatalogFile(root, file.RelativePath, directories))
            {
                case Availability.Missing:
                    remove.Add(file);
                    break;
                case Availability.Available:
                    hidden.Remove(file.Id);
                    break;
            }
        }
        if (remove.Count > 0)
        {
            context.AssetFiles.RemoveRange(remove);
            await context.SaveChangesAsync(token);
            foreach (var ids in remove.Select(file => file.AssetId).Distinct().Chunk(500))
            {
                await context.Assets.Where(asset => ids.Contains(asset.Id) &&
                    !context.AssetFiles.Any(file => file.AssetId == asset.Id)).ExecuteDeleteAsync(token);
            }
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
        }
        return new GalleryCatalogAudit(hidden, remove.Count, backup, null);
    }, token);

    private static Availability CheckCatalogFile(string root, string relativePath, Dictionary<string, Availability> directories)
    {
        try
        {
            var path = ResolvePath(root, relativePath);
            var folder = Path.GetDirectoryName(path)!;
            var state = CheckAuditDirectory(folder, directories);
            if (state != Availability.Available)
            {
                return state;
            }
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                {
                    return Availability.Unavailable;
                }
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Availability.Available;
            }
            catch (FileNotFoundException) when (CanConfirmParent(folder))
            {
                return Availability.Missing;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Availability.Unavailable;
        }
    }

    private static Availability CheckAuditDirectory(string directory, Dictionary<string, Availability> cache)
    {
        if (cache.TryGetValue(directory, out var state))
        {
            return state;
        }
        var parent = Path.GetDirectoryName(directory);
        state = parent is null ? Availability.Available : CheckAuditDirectory(parent, cache);
        if (state == Availability.Available)
        {
            try
            {
                state = IsOnlineDirectory(directory) ? Availability.Available : Availability.Unavailable;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // A missing subdirectory is actionable only with a reachable, normal parent.
                state = parent is not null && CanConfirmParent(parent)
                    ? Availability.Missing : Availability.Unavailable;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                state = Availability.Unavailable;
            }
        }
        cache.Add(directory, state);
        return state;
    }

    private static bool IsOnlineDirectory(string path) =>
        (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline)) ==
        FileAttributes.Directory;

    private static bool CanConfirmParent(string path)
    {
        try
        {
            if (!IsOnlineDirectory(path))
            {
                return false;
            }
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            _ = entries.MoveNext();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string BackupCatalog(SqliteConnection database)
    {
        var path = Path.GetFullPath(database.DataSource);
        var parent = Path.GetDirectoryName(path)!;
        InspectDirectoryChain(parent);
        var directory = Path.Combine(parent, "CatalogBackups");
        Directory.CreateDirectory(directory);
        InspectDirectoryChain(directory);
        var backup = Path.Combine(directory, $"catalog-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        var temporary = backup + ".partial";
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = temporary, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }
            File.Move(temporary, backup, overwrite: false);
            return backup;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
