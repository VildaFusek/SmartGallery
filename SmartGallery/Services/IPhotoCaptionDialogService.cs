using SmartGallery.Application.Catalog;

namespace SmartGallery.Services;

public sealed record SmartRenameProposal(GalleryFile File, string NewName);

public interface IPhotoCaptionDialogService
{
    Task PickAndDescribeAsync(CancellationToken cancellationToken);

    Task DescribeAsync(string path, CancellationToken cancellationToken);

    Task<string?> ConfirmSmartNameAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<SmartRenameProposal>?> ConfirmBatchSmartNamesAsync(
        IReadOnlyList<GalleryFile> photos, Func<GalleryFile, CancellationToken, Task<string>> getReadPath,
        CancellationToken cancellationToken);

    Task CancelAndWaitAsync();
}
