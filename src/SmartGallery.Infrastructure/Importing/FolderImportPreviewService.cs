using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartGallery.Application.Importing;
using SmartGallery.Domain;
using SmartGallery.Infrastructure.Persistence;

namespace SmartGallery.Infrastructure.Importing;

public sealed class FolderImportPreviewService(
    IDbContextFactory<CatalogDbContext> contextFactory, IMediaDateReader mediaDates)
    : IImportPreviewService
{
    private const int MaxItems = 500;
    private const int MaxEntries = 10_000;

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".mts", ".m2ts", ".webm", ".wmv", ".3gp", ".mpg", ".mpeg"
    };

    internal static MediaKind? GetMediaKind(string extension) =>
        PhotoExtensions.Contains(extension) ? MediaKind.Photo :
        VideoExtensions.Contains(extension) ? MediaKind.Video : null;

    public Task<ImportPreview> PreviewFolderAsync(
        Guid libraryId, string sourceDirectory, CancellationToken cancellationToken) =>
        PreviewFolderAsync(libraryId, sourceDirectory, null, cancellationToken);

    public Task<ImportPreview> PreviewFolderAsync(
        Guid libraryId, string sourceDirectory, IProgress<ImportAnalysisProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => PreviewCoreAsync(libraryId, sourceDirectory, progress, cancellationToken), cancellationToken);

    private async Task<ImportPreview> PreviewCoreAsync(
        Guid libraryId, string sourceDirectory, IProgress<ImportAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var library = await context.Libraries.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == libraryId, cancellationToken)
            ?? throw new InvalidOperationException("Knihovna již není v katalogu.");
        var items = new List<ImportPreviewItem>();
        var destinations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var (inspected, _, unsupported, warning) = await ScanAsync(
            library, sourceDirectory, previewOnly: true, item =>
            {
                if (item.DestinationPath.Length > 0)
                {
                    if (destinations.TryGetValue(item.DestinationPath, out var previousIndex))
                    {
                        items[previousIndex] = MarkDuplicate(items[previousIndex]);
                        item = MarkDuplicate(item);
                    }
                    else
                    {
                        destinations.Add(item.DestinationPath, items.Count);
                    }
                }
                items.Add(item);
            },
            (entries, media, others) =>
                progress?.Report(new ImportAnalysisProgress((int)entries, (int)media, (int)others)),
            cancellationToken);

        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(library.RootPath));
        // Detect a proposed file that would have to be another item's parent directory.
        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = items[index].DestinationPath;
            if (destination.Length == 0)
            {
                continue;
            }
            for (var parent = Path.GetDirectoryName(destination);
                 parent is not null && !string.Equals(parent, target, StringComparison.OrdinalIgnoreCase);
                 parent = Path.GetDirectoryName(parent))
            {
                if (destinations.TryGetValue(parent, out var parentIndex))
                {
                    MarkHierarchyConflict(items, index);
                    MarkHierarchyConflict(items, parentIndex);
                }
            }
        }

        return new ImportPreview(
            items.AsReadOnly(), (int)inspected, (int)unsupported, warning is null, warning,
            library.FolderLayout, library.FileNaming);
    }

    internal async Task<(long Inspected, long Media, long Unsupported, string? Warning)> ScanAsync(
        Library library, string sourceDirectory, bool previewOnly, Action<ImportPreviewItem> addItem,
        Action<long, long, long> report, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(library.FolderLayout) || !Enum.IsDefined(library.FileNaming))
        {
            throw new InvalidOperationException("Knihovna obsahuje neznámá pravidla importu.");
        }

        var source = NormalizeDirectory(sourceDirectory);
        var target = NormalizeDirectory(library.RootPath);
        RejectReparseAncestors(source);
        RejectReparseAncestors(target);
        if (IsWithin(source, target) || IsWithin(target, source))
        {
            throw new InvalidOperationException(
                "Zdroj a cílová knihovna nesmějí být totožné ani vzájemně vnořené.");
        }

        long inspected = 0;
        long media = 0;
        long unsupported = 0;
        string? warning = null;
        report(0, 0, 0);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        try
        {
            foreach (var (entryPath, attributes) in EnumerateSource(source, options, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (previewOnly && (inspected >= MaxEntries || media >= MaxItems))
                {
                    warning = $"Náhled je omezen na {MaxItems} mediálních souborů nebo {MaxEntries} položek. Tento limit skutečný import nemá.";
                    break;
                }

                inspected++;
                if (inspected % 25 == 0)
                {
                    report(inspected, media, unsupported);
                }
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    continue;
                }

                if (GetMediaKind(Path.GetExtension(entryPath)) is not { } kind)
                {
                    unsupported++;
                    continue;
                }

                var mediaDate = await mediaDates.ReadAsync(entryPath, kind, cancellationToken);
                addItem(BuildItem(library, source, target, entryPath, kind, mediaDate));
                media++;
            }
        }
        catch (Exception exception) when (IsFileAccessError(exception))
        {
            warning = $"Sken zdroje byl přerušen: {exception.Message}";
        }

        cancellationToken.ThrowIfCancellationRequested();
        report(inspected, media, unsupported);
        return (inspected, media, unsupported, warning);
    }

    private static IEnumerable<(string Path, FileAttributes Attributes)> EnumerateSource(
        string source, EnumerationOptions options, CancellationToken cancellationToken)
    {
        // Keep open only the directories on the current path, not all pending siblings.
        var pending = new Stack<IEnumerator<string>>();
        try
        {
            pending.Push(Directory.EnumerateFileSystemEntries(source, "*", options).GetEnumerator());
            while (pending.TryPeek(out var entries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entries.MoveNext())
                {
                    pending.Pop().Dispose();
                    continue;
                }
                var path = entries.Current;
                var attributes = File.GetAttributes(path);
                yield return (path, attributes);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory)
                {
                    pending.Push(Directory.EnumerateFileSystemEntries(path, "*", options).GetEnumerator());
                }
            }
        }
        finally
        {
            while (pending.TryPop(out var entries))
            {
                entries.Dispose();
            }
        }
    }

    private static ImportPreviewItem MarkDuplicate(ImportPreviewItem item) => item with
    {
        State = item.State == ImportPreviewState.Blocked ? ImportPreviewState.Blocked : ImportPreviewState.Conflict,
        Note = item.Note + " Stejný cíl navrhuje i jiný zdrojový soubor."
    };

    private static void MarkHierarchyConflict(List<ImportPreviewItem> items, int index)
    {
        var item = items[index];
        items[index] = item with
        {
            State = item.State == ImportPreviewState.Blocked ? ImportPreviewState.Blocked : ImportPreviewState.Conflict,
            Note = item.Note + " Navržený soubor koliduje s adresářem potřebným pro jinou položku."
        };
    }

    private static ImportPreviewItem BuildItem(
        Library library, string source, string target, string sourcePath, MediaKind kind, MediaDate mediaDate)
    {
        string destination = "";
        long? size = null;
        DateTimeOffset? modified = null;
        try
        {
            var info = new FileInfo(sourcePath);
            info.Refresh();
            if (!info.Exists)
            {
                throw new FileNotFoundException("Zdrojový soubor již neexistuje.", sourcePath);
            }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Zdroj je symbolický odkaz nebo reparse point.");
            }
            size = info.Length;
            modified = new DateTimeOffset(info.LastWriteTimeUtc);
            var (date, dateSource) = mediaDate.Value is not null
                ? mediaDate
                : GetFileDate(info, modified.Value, mediaDate.Source);

            var sourceRelative = Path.GetRelativePath(source, sourcePath);
            ValidateRelativePath(sourceRelative);
            var name = Path.GetFileName(sourceRelative);
            if (library.FileNaming == LibraryFileNaming.DatePrefix)
            {
                name = (date?.ToString("yyyyMMdd_", CultureInfo.InvariantCulture) ?? "bez-data_") + name;
            }
            var year = date?.ToString("yyyy", CultureInfo.InvariantCulture) ?? "BezData";
            var folder = kind == MediaKind.Video
                ? Path.Combine(year, "Video")
                : library.FolderLayout switch
                {
                    LibraryFolderLayout.YearMonth => date is null ? "BezData" :
                        Path.Combine(year, date.Value.ToString("MM", CultureInfo.InvariantCulture)),
                    LibraryFolderLayout.YearDay => date is null ? "BezData" :
                        Path.Combine(year, date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    LibraryFolderLayout.PreserveSourceFolders => Path.GetDirectoryName(sourceRelative) ?? "",
                    _ => throw new InvalidOperationException("Neznámá struktura fotografií.")
                };
            var relativeDestination = Path.Combine(folder, name);
            ValidateRelativePath(relativeDestination);
            destination = Path.GetFullPath(Path.Combine(target, relativeDestination));
            if (!IsWithin(destination, target))
            {
                throw new IOException("Navržený cíl je mimo knihovnu.");
            }

            var collision = InspectDestination(destination, target);
            return new ImportPreviewItem(
                sourcePath, destination, kind, size, modified,
                collision is null ? ImportPreviewState.Proposed : ImportPreviewState.Conflict,
                collision ?? dateSource)
            {
                OriginalDate = mediaDate.Value,
                OrganizationDate = date,
                DateSource = dateSource
            };
        }
        catch (Exception exception) when (IsFileAccessError(exception))
        {
            return new ImportPreviewItem(
                sourcePath, destination, kind, size, modified, ImportPreviewState.Blocked,
                $"Nelze bezpečně navrhnout cíl: {exception.Message}");
        }
    }

    private static MediaDate GetFileDate(FileInfo info, DateTimeOffset modified, string metadataNote)
    {
        if (modified.UtcDateTime.Year > 1601)
        {
            return new MediaDate(modified.ToLocalTime(), $"Čas poslední změny souboru (náhradní údaj). {metadataNote}");
        }
        var created = new DateTimeOffset(info.CreationTimeUtc);
        return created.UtcDateTime.Year > 1601
            ? new MediaDate(created.ToLocalTime(), $"Čas vytvoření souboru (náhradní údaj). {metadataNote}")
            : new MediaDate(null, $"Použije se BezData. {metadataNote}");
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Je vyžadována běžná absolutní cesta adresáře.");
        }
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException("Adresář neexistuje nebo není přístupný.");
        }
        return normalized;
    }

    private static void RejectReparseAncestors(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
        {
            if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Cesta nesmí procházet přes symbolické odkazy nebo junctions.");
            }
        }
    }

    private static bool IsWithin(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    internal static void ValidateRelativePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            throw new IOException("Cílová cesta musí být relativní.");
        }
        foreach (var component in path.Split(['\\', '/']))
        {
            var stem = component.Split('.')[0].TrimEnd(' ');
            var deviceName = stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                     stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 "123456789¹²³".Contains(stem[3]));
            if (component.Length is 0 or > 255 || component is "." or ".." ||
                component.EndsWith('.') || component.EndsWith(' ') ||
                component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || deviceName)
            {
                throw new IOException("Cesta obsahuje nepodporovaný nebo nebezpečný název.");
            }
        }
    }

    private static string? InspectDestination(string destination, string root)
    {
        for (var current = destination; ; current = Path.GetDirectoryName(current)!)
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return "Cíl nebo jeho nadřazená cesta obsahuje reparse point.";
                }
                if (string.Equals(current, destination, StringComparison.OrdinalIgnoreCase))
                {
                    return "Cíl již existuje. Nebude přepsán.";
                }
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    return "Cílovou strukturu blokuje existující soubor.";
                }
            }
            catch (FileNotFoundException) when (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) { }
            catch (DirectoryNotFoundException) when (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) { }

            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (string.IsNullOrEmpty(Path.GetDirectoryName(current)))
            {
                throw new IOException("Nelze ověřit, že cíl patří do knihovny.");
            }
        }
    }

    private static bool IsFileAccessError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
