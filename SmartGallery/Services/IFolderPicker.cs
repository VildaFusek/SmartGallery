namespace SmartGallery.Services;

public interface IFolderPicker
{
    Task<string?> PickAsync(CancellationToken cancellationToken);
}
