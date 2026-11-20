using System.Runtime.InteropServices;
using SmartGallery.Application.Importing;
using SmartGallery.Domain;
using Windows.Storage;

namespace SmartGallery.Imaging;

public sealed class WindowsMediaDateReader : IMediaDateReader
{
    public async Task<MediaDate> ReadAsync(string path, MediaKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
            DateTimeOffset? date;
            string source;
            if (kind == MediaKind.Photo)
            {
                var properties = await file.Properties.GetImagePropertiesAsync().AsTask(cancellationToken);
                date = properties.DateTaken;
                source = "Windows ImageProperties.DateTaken";
            }
            else
            {
                var properties = await file.Properties.RetrievePropertiesAsync(["System.Media.DateEncoded"])
                    .AsTask(cancellationToken);
                date = properties.TryGetValue("System.Media.DateEncoded", out var value) &&
                    value is DateTimeOffset encoded ? encoded : null;
                source = "System.Media.DateEncoded (datum kódování, nemusí být původním datem záznamu)";
            }

            cancellationToken.ThrowIfCancellationRequested();
            return date is { Year: > 1601 }
                ? new MediaDate(date, source)
                : new MediaDate(null, "Datum v metadatech chybí.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         COMException or NotSupportedException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new MediaDate(null, $"Metadata nelze přečíst: {exception.Message}");
        }
    }
}
