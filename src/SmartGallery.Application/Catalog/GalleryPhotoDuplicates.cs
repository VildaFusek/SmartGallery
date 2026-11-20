using SmartGallery.Domain;

namespace SmartGallery.Application.Catalog;

public sealed record PhotoPixelFingerprint(uint Width, uint Height, string Sha256);

public interface IPhotoPixelFingerprintReader
{
    Task<PhotoPixelFingerprint> ReadAsync(Stream stream, CancellationToken cancellationToken);
}

public sealed record PhotoDuplicateGroup(PhotoPixelFingerprint Fingerprint, IReadOnlyList<GalleryFile> Copies);
public sealed record PhotoDuplicateScan(IReadOnlyList<PhotoDuplicateGroup> Groups, int Skipped);
public sealed record PhotoDuplicateSelection(PhotoDuplicateGroup Group, GalleryFile Keep);

public sealed class GalleryPhotoDuplicates(IGalleryCatalog catalog, IPhotoPixelFingerprintReader reader)
{
    public Task<PhotoDuplicateScan> ScanAsync(Library library, IReadOnlyList<GalleryFile> files,
        IProgress<int> progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var pathCounts = files.GroupBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var photos = files.Where(file => file.Kind == MediaKind.Photo).ToArray();
            var matches = new Dictionary<PhotoPixelFingerprint, List<GalleryFile>>();
            var skipped = 0;
            var percent = 0;
            for (var i = 0; i < photos.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var photo = photos[i];
                try
                {
                    if (pathCounts[photo.RelativePath] != 1)
                    {
                        skipped++;
                    }
                    else
                    {
                        var fingerprint = await ReadAsync(library, photo, cancellationToken);
                        if (!matches.TryGetValue(fingerprint, out var copies))
                        {
                            matches.Add(fingerprint, copies = []);
                        }
                        copies.Add(photo);
                    }
                }
                catch (Exception exception) when (IsUnreadable(exception))
                {
                    skipped++;
                }
                var currentPercent = (int)((i + 1L) * 100 / photos.Length);
                if (currentPercent != percent)
                {
                    progress.Report(percent = currentPercent);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var groups = matches.Where(match => match.Value.Count > 1)
                .Select(match => new PhotoDuplicateGroup(match.Key, match.Value
                    .OrderByDescending(file => file.Analysis is not null)
                    .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
                .OrderBy(group => group.Copies[0].RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new PhotoDuplicateScan(groups, skipped);
        }, cancellationToken);

    public Task<string> MoveToTrashAsync(Library library, IReadOnlyList<PhotoDuplicateSelection> selections,
        IProgress<int> progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var keep = new List<GalleryFile>(selections.Count);
            var remove = new List<GalleryFile>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var total = selections.Sum(selection => selection.Group.Copies.Count);
            var percent = 0;
            foreach (var selection in selections)
            {
                if (selection.Group.Copies.Count < 2 || !selection.Group.Copies.Contains(selection.Keep))
                {
                    throw new InvalidOperationException("Každá skupina musí mít jednu zvolenou kopii k ponechání.");
                }
                keep.Add(selection.Keep);
                foreach (var file in selection.Group.Copies)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (file.Kind != MediaKind.Photo || !paths.Add(file.RelativePath) ||
                            await ReadAsync(library, file, cancellationToken) != selection.Group.Fingerprint)
                        {
                            throw new IOException("Obsah už neodpovídá nalezené shodě.");
                        }
                    }
                    catch (Exception exception) when (IsUnreadable(exception))
                    {
                        throw new IOException($"Duplicity znovu vyhledej. Nelze ověřit soubor {file.RelativePath}: " +
                            exception.Message, exception);
                    }
                    if (file != selection.Keep)
                    {
                        remove.Add(file);
                    }
                    var currentPercent = (int)(paths.Count * 100L / total);
                    if (currentPercent != percent)
                    {
                        progress.Report(percent = currentPercent);
                    }
                }
            }
            return await catalog.MoveDuplicatesToTrashAsync(library, remove, keep, cancellationToken);
        }, cancellationToken);

    private async Task<PhotoPixelFingerprint> ReadAsync(Library library, GalleryFile file, CancellationToken token)
    {
        var path = await catalog.GetReadPathAsync(library.RootPath, file, token);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != file.SizeBytes ||
            File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks != file.LastWriteTimeUtc.UtcTicks)
        {
            throw new IOException("Kopie se od importu změnila.");
        }
        return await reader.ReadAsync(stream, token);
    }

    private static bool IsUnreadable(Exception error) =>
        error is InvalidDataException or IOException or UnauthorizedAccessException;
}
