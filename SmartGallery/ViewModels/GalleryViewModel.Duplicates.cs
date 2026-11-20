using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SmartGallery.Application.Catalog;

namespace SmartGallery.ViewModels;

public sealed partial class GalleryViewModel
{
    private readonly GalleryPhotoDuplicates _photoDuplicates;

    internal IAsyncRelayCommand<Func<PhotoDuplicateScan, Task<IReadOnlyList<PhotoDuplicateSelection>?>>>
        FindDuplicatesCommand { get; }

    public bool CanFindDuplicates => CanBrowse && !IsBusy && !_requiresGalleryRefresh && _photoCount > 1;

    private async Task FindDuplicatesAsync(Func<PhotoDuplicateScan, Task<IReadOnlyList<PhotoDuplicateSelection>?>>? review,
        CancellationToken cancellationToken)
    {
        if (!CanFindDuplicates || _library is not { } library || review is null)
        {
            return;
        }

        IsBusy = true;
        GalleryWorkProgress = 0;
        GalleryWorkActive = true;
        string? trashPath = null;
        try
        {
            StatusMessage = "Vyhledávám shodné fotografie v celé galerii…";
            var progress = new Progress<int>(value => GalleryWorkProgress = value);
            var scan = await _photoDuplicates.ScanAsync(library, _catalogFiles, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var skipped = scan.Skipped == 0 ? "" : $" · přeskočeno: {scan.Skipped}";
            StatusMessage = scan.Groups.Count == 0
                ? $"Mezi ověřenými fotografiemi nebyly nalezeny duplicity{skipped}."
                : $"Skupiny shodných fotografií: {scan.Groups.Count}{skipped}.";
            GalleryWorkActive = false;
            if (scan.Groups.Count == 0 || await review(scan) is not { Count: > 0 } selections)
            {
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();

            StatusMessage = "Znovu ověřuji obsah vybraných fotografií před přesunem…";
            GalleryWorkProgress = 0;
            GalleryWorkActive = true;
            trashPath = await _photoDuplicates.MoveToTrashAsync(library, selections, progress, cancellationToken);
            await RefreshAsync(CancellationToken.None);
            StatusMessage = $"Přesunuto do koše galerie: {selections.Sum(item => item.Group.Copies.Count - 1)} · {trashPath}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Práce s duplicitami byla zastavena.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Vyhledání nebo přesun duplicit selhal.");
            if (trashPath is not null)
            {
                _requiresGalleryRefresh = true;
            }
            StatusMessage = trashPath is null ? $"Práci s duplicitami se nepodařilo dokončit: {exception.Message}" :
                $"Kopie jsou v koši ({trashPath}), ale katalog se nepodařilo obnovit. Použij Soubor → Aktualizovat.";
        }
        finally
        {
            GalleryWorkActive = false;
            IsBusy = false;
        }
    }
}
