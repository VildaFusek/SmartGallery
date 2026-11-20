using System.Globalization;
using System.Text;
using SmartGallery.Application.Catalog;
using SmartGallery.Application.Importing;
using SmartGallery.Domain;

namespace SmartGallery.Application.AI;

public sealed class GalleryPhotoAnalyzer(IImageCaptionGenerator generator, IMediaDateReader mediaDates)
{
    public async Task<GalleryPhotoAnalysis> AnalyzeAsync(string path, GalleryFile photo, CancellationToken cancellationToken)
    {
        var (dateTaken, _) = await mediaDates.ReadAsync(path, MediaKind.Photo, cancellationToken).ConfigureAwait(false);
        var date = dateTaken ?? (photo.LastWriteTimeUtc.Year > 1601 ? photo.LastWriteTimeUtc.ToLocalTime() : (DateTimeOffset?)null);
        var prefix = (date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "bez-data") + "_";
        var caption = await generator.GenerateAsync(path, cancellationToken).ConfigureAwait(false);
        return new GalleryPhotoAnalysis(caption.Text, caption.Truncated,
            CreateSuggestedName(caption.Text, Path.GetExtension(photo.Name), prefix), DateTimeOffset.UtcNow.UtcTicks);
    }

    public static string? CreateSuggestedName(string caption, string extension, string prefix)
    {
        const int maxLength = 80;
        var name = new StringBuilder();
        var separator = false;
        foreach (var character in caption)
        {
            var lower = char.ToLowerInvariant(character);
            if (lower is not (>= 'a' and <= 'z' or >= '0' and <= '9'))
            {
                separator = name.Length > 0;
                continue;
            }
            if (name.Length + (separator ? 2 : 1) > maxLength)
            {
                break;
            }
            if (separator)
            {
                name.Append('-');
                separator = false;
            }
            name.Append(lower);
        }
        return name.Length == 0 ? null : $"{prefix}{name}{extension}";
    }
}
