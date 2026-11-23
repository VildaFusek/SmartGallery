using Microsoft.EntityFrameworkCore;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using SmartGallery.Infrastructure.Importing;

namespace SmartGallery.Infrastructure.Persistence;

public sealed partial class SqliteGalleryCatalog
{
    private const string TrashDirectoryName = ".smartgallery-trash";

    public Task<IReadOnlyList<GalleryTrashItem>> StagePermanentDeletionAsync(Library library,
        IReadOnlyList<GalleryFile> files, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<GalleryTrashItem>>(async () =>
        {
            var selection = files.DistinctBy(item => item.Id).ToArray();
            if (selection.Any(item => !IsTrashItemPath(item.RelativePath) ||
                FolderImportPreviewService.GetMediaKind(Path.GetExtension(item.RelativePath)) != item.Kind))
            {
                throw new IOException("Výběr obsahuje neplatnou kopii galerie.");
            }
            var slot = await MoveToTrashCoreAsync(library, selection, cancellationToken, requireSnapshot: true);
            var batch = Path.GetFileName(slot);
            var trashedAt = DateTimeOffset.UtcNow;
            return selection.Select(item => new GalleryTrashItem(batch, item.RelativePath, item.Kind,
                item.SizeBytes, item.LastWriteTimeUtc, trashedAt)).ToArray();
        }, cancellationToken);

    public Task<IReadOnlyList<GalleryTrashItem>> GetTrashAsync(Library library, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<GalleryTrashItem>>(() =>
        {
            var items = new List<GalleryTrashItem>();
            InspectDirectoryChain(library.RootPath);
            var trashRoot = GetTrashRoot(library);
            try { InspectDirectoryChain(trashRoot); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                return items;
            }

            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            var slots = Directory.EnumerateDirectories(trashRoot, "*", options);
            var filesOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };
            foreach (var slot in slots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = Path.GetFileName(slot);
                if (!Guid.TryParseExact(batch, "N", out _))
                {
                    continue;
                }
                InspectDirectoryChain(slot);
                var trashedAt = new DateTimeOffset(Directory.GetCreationTimeUtc(slot));
                foreach (var path in Directory.EnumerateFiles(slot, "*", filesOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = Path.GetRelativePath(slot, path);
                    if (!IsTrashItemPath(relativePath) ||
                        FolderImportPreviewService.GetMediaKind(Path.GetExtension(relativePath)) is not { } kind)
                    {
                        continue;
                    }
                    FolderImportPreviewService.ValidateRelativePath(relativePath);
                    InspectFile(path);
                    var info = new FileInfo(path);
                    items.Add(new GalleryTrashItem(batch, relativePath, kind, info.Length,
                        new DateTimeOffset(info.LastWriteTimeUtc), trashedAt));
                }
            }
            return items.OrderByDescending(item => item.TrashedAt)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        }, cancellationToken);

    public Task<string> GetTrashReadPathAsync(Library library, GalleryTrashItem item, CancellationToken cancellationToken) =>
        Task.Run(() => RequireTrashCopy(library, item), cancellationToken);

    public Task RestoreTrashAsync(Library library, IReadOnlyList<GalleryTrashItem> items,
        CancellationToken cancellationToken) => Task.Run(async () =>
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var catalogPaths = await GetMutationPathsAsync(context, library, cancellationToken);
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var planned = new List<(GalleryTrashItem Item, string Source, string Destination)>(items.Count);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = RequireUncatalogedTrashCopy(library, item, catalogPaths);
                var destination = ResolvePath(library.RootPath, item.RelativePath);
                if (catalogPaths.Contains(item.RelativePath) || !destinations.Add(item.RelativePath) ||
                    File.Exists(destination) || Directory.Exists(destination))
                {
                    throw new IOException($"Cesta {item.RelativePath} je už obsazená nebo ji používá více vybraných kopií. Nic se nepřepsalo.");
                }
                var asset = new Asset(Guid.NewGuid(), library.Id, DateTimeOffset.UtcNow);
                context.Assets.Add(asset);
                context.AssetFiles.Add(new AssetFile(Guid.NewGuid(), asset.Id, item.RelativePath,
                    item.SizeBytes, item.LastWriteTimeUtc));
                planned.Add((item, source, destination));
            }
            await context.SaveChangesAsync(cancellationToken);

            var moved = 0;
            try
            {
                foreach (var move in planned)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureLibraryDirectory(Path.GetDirectoryName(move.Destination)!, library.RootPath);
                    RequireTrashCopy(library, move.Item);
                    File.Move(move.Source, move.Destination, overwrite: false);
                    moved++;
                }
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
            }
            catch (Exception operationError) when (moved > 0)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackError)
                {
                    throw new IOException("Obnova nedokončena; zkontroluj původní umístění obnovovaných souborů i koš.",
                        new AggregateException(operationError, rollbackError));
                }
                var errors = new List<Exception> { operationError };
                for (var i = moved - 1; i >= 0; i--)
                {
                    var move = planned[i];
                    try
                    {
                        RequireTrashStamp(move.Destination, move.Item);
                        InspectDirectoryChain(Path.GetDirectoryName(move.Source)!);
                        File.Move(move.Destination, move.Source, overwrite: false);
                    }
                    catch (Exception rollbackError)
                    {
                        errors.Add(rollbackError);
                    }
                }
                if (errors.Count > 1)
                {
                    throw new IOException("Některé soubory se nepodařilo vrátit do koše; zkontroluj jejich původní umístění.",
                        new AggregateException(errors));
                }
                throw;
            }
            PruneTrashDirectories(library, planned.Select(move => move.Source));
        }, cancellationToken);

    public Task<GalleryTrashEmptyResult> EmptyTrashAsync(Library library, IReadOnlyList<GalleryTrashItem> items,
        CancellationToken cancellationToken) => Task.Run(async () =>
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var catalogPaths = await GetMutationPathsAsync(context, library, cancellationToken);
            var planned = new List<(GalleryTrashItem Item, string Path)>(items.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = RequireUncatalogedTrashCopy(library, item, catalogPaths);
                if (seen.Add(path))
                {
                    planned.Add((item, path));
                }
            }

            var removed = 0;
            string? interruption = null;
            try
            {
                foreach (var (item, path) in planned)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireTrashCopy(library, item);
                    File.Delete(path);
                    removed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                interruption = "Vysypání bylo zastaveno.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                interruption = $"Vysypání bylo přerušeno: {error.Message}";
            }
            PruneTrashDirectories(library, planned.Take(removed).Select(move => move.Path));
            return new GalleryTrashEmptyResult(removed, interruption);
        }, cancellationToken);

    private static string GetTrashRoot(Library library) => Path.GetFullPath(Path.Combine(library.RootPath, TrashDirectoryName));

    private static bool IsTrashItemPath(string relativePath) => IsSafeRelativePath(relativePath) &&
        !string.Equals(relativePath.Split('\\', 2)[0], TrashDirectoryName, StringComparison.OrdinalIgnoreCase);

    private static string RequireTrashCopy(Library library, GalleryTrashItem item)
    {
        if (!Guid.TryParseExact(item.Batch, "N", out _) || !IsTrashItemPath(item.RelativePath) ||
            FolderImportPreviewService.GetMediaKind(Path.GetExtension(item.RelativePath)) != item.Kind)
        {
            throw new IOException("Položka nepatří do koše galerie.");
        }
        FolderImportPreviewService.ValidateRelativePath(item.RelativePath);
        var path = ResolvePath(Path.Combine(GetTrashRoot(library), item.Batch), item.RelativePath);
        RequireTrashStamp(path, item);
        return path;
    }

    private static void RequireTrashStamp(string path, GalleryTrashItem item)
    {
        InspectFile(path);
        var info = new FileInfo(path);
        if (info.Length != item.SizeBytes || info.LastWriteTimeUtc.Ticks != item.LastWriteTimeUtc.UtcTicks)
        {
            throw new IOException($"Soubor {item.RelativePath} se změnil. Otevři koš znovu.");
        }
    }

    private static string RequireUncatalogedTrashCopy(Library library, GalleryTrashItem item, HashSet<string> catalogPaths)
    {
        var path = RequireTrashCopy(library, item);
        if (catalogPaths.Contains(Path.GetRelativePath(library.RootPath, path)))
        {
            throw new IOException($"Soubor {item.RelativePath} je stále v katalogu a koš jej nezmění.");
        }
        return path;
    }

    private static async Task<HashSet<string>> GetMutationPathsAsync(
        CatalogDbContext context, Library library, CancellationToken cancellationToken)
    {
        var currentRoot = await context.Libraries.Where(item => item.Id == library.Id)
            .Select(item => item.RootPath).SingleOrDefaultAsync(cancellationToken);
        if (currentRoot != library.RootPath)
        {
            throw new IOException("Knihovna se změnila. Nejprve obnov stav galerie.");
        }
        var paths = await FilesForLibrary(context, library.Id).Select(file => file.RelativePath)
            .ToListAsync(cancellationToken);
        return paths.Select(path => path.Replace('/', '\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void PruneTrashDirectories(Library library, IEnumerable<string> processedPaths)
    {
        var trashPrefix = GetTrashRoot(library) + Path.DirectorySeparatorChar;
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in processedPaths)
        {
            for (var directory = Path.GetDirectoryName(path);
                 directory is not null && directory.StartsWith(trashPrefix, StringComparison.OrdinalIgnoreCase) &&
                 directories.Add(directory);
                 directory = Path.GetDirectoryName(directory))
            {
            }
        }
        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            try
            {
                InspectDirectoryChain(directory);
                Directory.Delete(directory, recursive: false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
