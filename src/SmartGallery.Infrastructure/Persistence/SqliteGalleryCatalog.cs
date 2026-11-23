using Microsoft.EntityFrameworkCore;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using SmartGallery.Infrastructure.Importing;

namespace SmartGallery.Infrastructure.Persistence;

public sealed partial class SqliteGalleryCatalog(IDbContextFactory<CatalogDbContext> contextFactory) : IGalleryCatalog
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private readonly IDbContextFactory<CatalogDbContext> _contextFactory = contextFactory;

    public Task<IReadOnlyList<GalleryFile>> GetFilesAsync(Guid libraryId, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<GalleryFile>>(async () =>
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var files = (from file in FilesForLibrary(context, libraryId)
                         join analysis in context.PhotoAnalyses on file.Id equals analysis.FileId into analyses
                         from analysis in analyses.DefaultIfEmpty()
                         select new { File = file, Analysis = analysis }).AsNoTracking();
            var result = new List<GalleryFile>();
            await foreach (var row in files.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = row.File;
                if (IsSafeRelativePath(file.RelativePath) &&
                    FolderImportPreviewService.GetMediaKind(Path.GetExtension(file.RelativePath)) is { } kind)
                {
                    result.Add(new GalleryFile(file.Id, file.RelativePath, kind)
                    {
                        SizeBytes = file.SizeBytes,
                        LastWriteTimeUtc = file.LastWriteTimeUtc,
                        Analysis = row.Analysis is { } saved && kind == MediaKind.Photo
                            ? new GalleryPhotoAnalysis(saved.Caption, saved.Truncated, saved.SuggestedName, saved.AnalyzedAtUtcTicks)
                            : null
                    });
                }
            }
            return result;
        }, cancellationToken);

    public Task SavePhotoAnalysisAsync(Library library, GalleryFile file, GalleryPhotoAnalysis analysis,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var currentRoot = await context.Libraries.Where(item => item.Id == library.Id)
                .Select(item => item.RootPath).SingleOrDefaultAsync(cancellationToken);
            var record = await FilesForLibrary(context, library.Id)
                .SingleOrDefaultAsync(item => item.Id == file.Id, cancellationToken);
            if (currentRoot != library.RootPath || record is null || record.RelativePath != file.RelativePath ||
                record.SizeBytes != file.SizeBytes || record.LastWriteTimeUtc != file.LastWriteTimeUtc ||
                FolderImportPreviewService.GetMediaKind(Path.GetExtension(record.RelativePath)) != MediaKind.Photo)
            {
                throw new IOException("Záznam fotografie se během analýzy změnil.");
            }

            RequireUnchanged(ResolvePath(library.RootPath, record.RelativePath), record);
            context.PhotoAnalyses.Add(new PhotoAnalysisRecord
            {
                FileId = record.Id,
                Caption = analysis.Caption,
                Truncated = analysis.Truncated,
                SuggestedName = analysis.SuggestedName,
                AnalyzedAtUtcTicks = analysis.AnalyzedAtUtcTicks
            });
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
        }, cancellationToken);

    public Task<string> GetReadPathAsync(string root, GalleryFile file, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var path = ResolvePath(root, file.RelativePath);
            InspectFile(path);
            return path;
        }, cancellationToken);

    public Task<string> MoveToTrashAsync(Library library, GalleryFile file, CancellationToken cancellationToken) =>
        Task.Run(async () => Path.Combine(
            await MoveToTrashCoreAsync(library, [file], cancellationToken), file.RelativePath), cancellationToken);

    public Task<string> MoveToTrashAsync(
        Library library, IReadOnlyList<GalleryFile> files, CancellationToken cancellationToken) =>
        Task.Run(() => MoveToTrashCoreAsync(library, files, cancellationToken), cancellationToken);

    public Task<string> MoveDuplicatesToTrashAsync(Library library, IReadOnlyList<GalleryFile> files,
        IReadOnlyList<GalleryFile> retained, CancellationToken cancellationToken) =>
        Task.Run(() => MoveToTrashCoreAsync(library, files, cancellationToken, retained), cancellationToken);

    public Task RenamePhotoAsync(Library library, GalleryFile file, string newName, CancellationToken cancellationToken) =>
        file.Kind == MediaKind.Photo
            ? RenameFileAsync(library, file, newName, cancellationToken)
            : Task.FromException(new IOException("Položka není fotografie."));

    public Task RenameFileAsync(Library library, GalleryFile file, string newName, CancellationToken cancellationToken) =>
        Task.Run(() => RenameFileCoreAsync(library, file, newName, cancellationToken), cancellationToken);

    private async Task RenameFileCoreAsync(
        Library library, GalleryFile file, string newName, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.RelativePath);
        if (Path.GetFileName(newName) != newName ||
            string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(newName)) ||
            FolderImportPreviewService.GetMediaKind(extension) != file.Kind ||
            !string.Equals(Path.GetExtension(newName), extension, StringComparison.Ordinal))
        {
            throw new IOException("Nový název musí zachovat příponu souboru a nesmí obsahovat složku.");
        }
        FolderImportPreviewService.ValidateRelativePath(newName);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var currentRoot = await context.Libraries.Where(item => item.Id == library.Id)
            .Select(item => item.RootPath).SingleOrDefaultAsync(cancellationToken);
        var files = FilesForLibrary(context, library.Id);
        var record = await files.SingleOrDefaultAsync(item => item.Id == file.Id, cancellationToken);
        if (currentRoot != library.RootPath || record is null || record.RelativePath != file.RelativePath ||
            record.SizeBytes != file.SizeBytes || record.LastWriteTimeUtc != file.LastWriteTimeUtc)
        {
            throw new InvalidOperationException("Záznam se změnil. Obnov stav galerie a zkus to znovu.");
        }

        var relativeDestination = Path.Combine(file.Folder, newName);
        if (string.Equals(record.RelativePath, relativeDestination, StringComparison.Ordinal))
        {
            return;
        }
        if (await files.AnyAsync(item => item.Id != file.Id &&
                EF.Functions.Collate(item.RelativePath, "NOCASE") == record.RelativePath, cancellationToken))
        {
            throw new IOException("Na původní soubor odkazuje více záznamů katalogu.");
        }
        if (await files.AnyAsync(item => item.Id != file.Id &&
                EF.Functions.Collate(item.RelativePath, "NOCASE") == relativeDestination, cancellationToken))
        {
            throw new IOException("Navržený název už v této složce používá jiná položka katalogu.");
        }

        var original = ResolvePath(library.RootPath, record.RelativePath);
        var destination = ResolvePath(library.RootPath, relativeDestination);
        RequireUnchanged(original, record);
        context.Entry(record).Property(item => item.RelativePath).CurrentValue = relativeDestination;
        await context.SaveChangesAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        RequireUnchanged(original, record);
        File.Move(original, destination, overwrite: false);
        try
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception operationError)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
                RequireUnchanged(destination, record);
                File.Move(destination, original, overwrite: false);
            }
            catch (Exception recoveryError)
            {
                throw new IOException($"Přejmenování se nepodařilo bezpečně dokončit. Zkontroluj kopii: {destination}",
                    new AggregateException(operationError, recoveryError));
            }
            throw;
        }
    }

    private async Task<string> MoveToTrashCoreAsync(
        Library library, IReadOnlyList<GalleryFile> files, CancellationToken cancellationToken,
        IReadOnlyList<GalleryFile>? retained = null, bool requireSnapshot = false)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var currentRoot = await context.Libraries.Where(item => item.Id == library.Id)
            .Select(item => item.RootPath).SingleOrDefaultAsync(cancellationToken);
        if (currentRoot != library.RootPath || files.Count == 0)
        {
            throw new InvalidOperationException("Záznam se změnil. Obnov stav galerie a zkus to znovu.");
        }

        var records = await FilesForLibrary(context, library.Id).AsNoTracking().ToListAsync(cancellationToken);
        var byId = records.ToDictionary(item => item.Id);
        var byPath = records.ToLookup(item => item.RelativePath.Replace('/', '\\'), StringComparer.OrdinalIgnoreCase);
        var retainedRecords = new List<(string Path, AssetFile Record)>();
        if (retained is not null)
        {
            var removalPaths = files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (retained.Count == 0)
            {
                throw new IOException("Přesun duplicit vyžaduje ověřenou kopii k ponechání.");
            }
            foreach (var file in retained)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (removalPaths.Contains(file.RelativePath))
                {
                    throw new IOException("Kopie k ponechání nesmí být mezi soubory k přesunu.");
                }
                var record = RequireMatch(file);
                var path = ResolvePath(library.RootPath, record.RelativePath);
                RequireUnchanged(path, record);
                retainedRecords.Add((path, record));
            }
        }

        var fileCounts = records.GroupBy(item => item.AssetId).ToDictionary(group => group.Key, group => group.Count());
        var trashRoot = GetTrashRoot(library);
        var slot = Path.Combine(trashRoot, Guid.NewGuid().ToString("N"));
        var planned = new List<TrashMove>(files.Count);
        var orphaned = new List<Guid>();
        foreach (var file in files.DistinctBy(item => item.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = RequireMatch(file);
            var original = ResolvePath(library.RootPath, record.RelativePath);
            RequireUnchanged(original, record);
            planned.Add(new TrashMove(record, original, ResolvePath(slot, record.RelativePath)));
            if (--fileCounts[record.AssetId] == 0)
            {
                orphaned.Add(record.AssetId);
            }
        }

        context.AssetFiles.RemoveRange(planned.Select(move => move.Record));
        if (orphaned.Count > 0)
        {
            var assets = await context.Assets.Where(item => orphaned.Contains(item.Id)).ToListAsync(cancellationToken);
            context.Assets.RemoveRange(assets);
        }
        await context.SaveChangesAsync(cancellationToken);

        var moved = 0;
        try
        {
            foreach (var move in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureLibraryDirectory(Path.GetDirectoryName(move.Destination)!, library.RootPath);
                RequireUnchanged(move.Original, move.Record);
                File.Move(move.Original, move.Destination, overwrite: false);
                moved++;
            }
            foreach (var (path, record) in retainedRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireUnchanged(path, record);
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
            catch (Exception recoveryError)
            {
                throw new IOException($"Zápis do katalogu selhal. Přesunuté kopie hledej v koši: {slot}",
                    new AggregateException(operationError, recoveryError));
            }
            var errors = new List<Exception> { operationError };
            for (var i = moved - 1; i >= 0; i--)
            {
                var move = planned[i];
                try
                {
                    RequireUnchanged(move.Destination, move.Record);
                    InspectDirectoryChain(Path.GetDirectoryName(move.Original)!);
                    File.Move(move.Destination, move.Original, overwrite: false);
                }
                catch (Exception recoveryError)
                {
                    errors.Add(recoveryError);
                }
            }
            if (errors.Count > 1)
            {
                throw new IOException($"Nepodařilo se vrátit všechny kopie. Zbylé hledej v koši: {slot}",
                    new AggregateException(errors));
            }
            throw;
        }
        return slot;

        AssetFile RequireMatch(GalleryFile file)
        {
            if (!byId.TryGetValue(file.Id, out var record) || record.RelativePath != file.RelativePath ||
                ((requireSnapshot || retained is not null) && (record.SizeBytes != file.SizeBytes ||
                    record.LastWriteTimeUtc != file.LastWriteTimeUtc)))
            {
                throw new InvalidOperationException("Záznam se změnil. Obnov stav galerie a zkus to znovu.");
            }
            if (byPath[record.RelativePath].Skip(1).Any())
            {
                throw new IOException($"Na soubor {file.RelativePath} odkazuje více záznamů katalogu.");
            }
            return record;
        }
    }

    private static void EnsureLibraryDirectory(string directory, string libraryRoot)
    {
        if (!Directory.Exists(directory) &&
            !string.Equals(directory, libraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            EnsureLibraryDirectory(Path.GetDirectoryName(directory)!, libraryRoot);
            Directory.CreateDirectory(directory);
        }
        InspectDirectoryChain(directory);
    }

    private sealed record TrashMove(AssetFile Record, string Original, string Destination);

    private static IQueryable<AssetFile> FilesForLibrary(CatalogDbContext context, Guid libraryId) =>
        from file in context.AssetFiles
        join asset in context.Assets on file.AssetId equals asset.Id
        where asset.LibraryId == libraryId
        select file;

    private static bool IsSafeRelativePath(string path) =>
        !Path.IsPathRooted(path) && !path.Contains('/') && !path.Contains(':') &&
        path.Split('\\').All(segment => segment.Length > 0 && segment[^1] is not ('.' or ' ') &&
            segment.IndexOfAny(InvalidFileNameChars) < 0);

    private static string ResolvePath(string root, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
        {
            throw new IOException("Soubor není uvnitř spravované knihovny.");
        }
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Path.EndsInDirectorySeparator(prefix))
        {
            prefix += Path.DirectorySeparatorChar;
        }
        var path = Path.GetFullPath(relativePath, prefix);
        if (!string.Equals(path, prefix + relativePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Soubor není uvnitř spravované knihovny.");
        }
        return path;
    }

    private static void RequireUnchanged(string path, AssetFile record)
    {
        InspectFile(path);
        var info = new FileInfo(path);
        if (info.Length != record.SizeBytes || info.LastWriteTimeUtc.Ticks != record.LastWriteTimeUtc.UtcTicks)
        {
            throw new IOException("Kopie se od importu změnila; aplikace ji nepřesune.");
        }
    }

    private static void InspectFile(string path)
    {
        InspectDirectoryChain(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("Položka není běžný soubor nebo je to odkaz.");
        }
    }

    private static void InspectDirectoryChain(string directory)
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            var attributes = File.GetAttributes(current.FullName);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Cesta knihovny obsahuje odkaz nebo blokující soubor.");
            }
        }
    }
}
