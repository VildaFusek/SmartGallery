using SmartGallery.Domain;

namespace SmartGallery.Application.Importing;

public sealed record MediaDate(DateTimeOffset? Value, string Source);

public interface IMediaDateReader
{
    Task<MediaDate> ReadAsync(string path, MediaKind kind, CancellationToken cancellationToken);
}
