using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;

namespace SmartGallery.ViewModels;

public sealed partial class GalleryViewModel
{
    private bool _isTrashOpen;
    private int _trashSelectionCount;
    private IReadOnlyList<TrashReviewRow> _trashItems = [];

    internal IAsyncRelayCommand OpenTrashCommand { get; }
    internal IAsyncRelayCommand<GalleryTrashRequest> ManageTrashCommand { get; }

    public bool IsTrashOpen
    {
        get => _isTrashOpen;
        private set
        {
            if (SetProperty(ref _isTrashOpen, value))
            {
                OnPropertyChanged(nameof(IsGalleryOpen));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(SelectionLabel));
                OnPropertyChanged(nameof(CanBrowse));
                OnPropertyChanged(nameof(CanChangeFilters));
                OnPropertyChanged(nameof(CanDelete));
                DeleteCommand.NotifyCanExecuteChanged();
                DeletePermanentlyCommand.NotifyCanExecuteChanged();
                DescribeSelectionCommand.NotifyCanExecuteChanged();
                NotifyGalleryAnalysisActions();
                NotifyTrashActions();
            }
        }
    }

    public bool IsGalleryOpen => !IsTrashOpen;
    public bool CanRestoreTrash => IsTrashOpen && CanNavigateLibrary && !IsBusy && _trashSelectionCount > 0;
    public bool CanEmptyTrash => IsTrashOpen && CanNavigateLibrary && !IsBusy && TrashItems.Count > 0;
    public string TrashSummary => TrashItems.Count == 0 ? "Koš galerie je prázdný." : $"Položek v koši: {TrashItems.Count}";
    public IReadOnlyList<TrashReviewRow> TrashItems
    {
        get => _trashItems;
        private set
        {
            if (SetProperty(ref _trashItems, value))
            {
                OnPropertyChanged(nameof(TrashSummary));
                NotifyTrashActions();
            }
        }
    }

    internal void SetTrashSelectionCount(int count)
    {
        _trashSelectionCount = count;
        OnPropertyChanged(nameof(CanRestoreTrash));
        if (IsTrashOpen)
        {
            OnPropertyChanged(nameof(SelectionLabel));
        }
    }

    private void NotifyTrashActions()
    {
        OpenTrashCommand.NotifyCanExecuteChanged();
        ManageTrashCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRestoreTrash));
        OnPropertyChanged(nameof(CanEmptyTrash));
    }

    internal Task<string> GetTrashReadPathAsync(GalleryTrashItem item, CancellationToken cancellationToken) =>
        _library is { } library && library.Id == _displayedLibraryId
            ? _galleryCatalog.GetTrashReadPathAsync(library, item, cancellationToken)
            : Task.FromException<string>(new IOException("Knihovna není dostupná."));

    private async Task LoadTrashItemsAsync(Library library, CancellationToken cancellationToken)
    {
        var items = await _galleryCatalog.GetTrashAsync(library, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        SetTrashSelectionCount(0);
        TrashItems = items.Select(item => new TrashReviewRow(item)).ToArray();
        StatusMessage = TrashSummary;
    }

    private async Task OpenTrashAsync(CancellationToken cancellationToken)
    {
        if (!CanNavigateLibrary || IsBusy || IsTrashOpen || _library is not { } library)
        {
            return;
        }
        IsBusy = true;
        try
        {
            StatusMessage = "Načítám koš galerie…";
            await LoadTrashItemsAsync(library, cancellationToken);
            IsTrashOpen = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Načtení koše galerie selhalo.");
            StatusMessage = $"Koš galerie se nepodařilo načíst: {exception.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task ManageTrashAsync(GalleryTrashRequest? request, CancellationToken cancellationToken)
    {
        if (!IsTrashOpen || IsBusy || _library is not { } library || request is not { Items.Count: > 0 })
        {
            return;
        }
        IsBusy = true;
        string? outcome = null;
        try
        {
            if (request.Empty)
            {
                StatusMessage = "Vysypávám koš galerie…";
                var result = await _galleryCatalog.EmptyTrashAsync(library, request.Items, cancellationToken);
                outcome = $"Trvale odstraněno z koše: {result.RemovedCount}/{request.Items.Count}." +
                    (result.Interruption is null ? "" : $" {result.Interruption}");
                await LoadTrashItemsAsync(library, CancellationToken.None);
            }
            else
            {
                StatusMessage = "Obnovuji vybrané položky z koše galerie…";
                await _galleryCatalog.RestoreTrashAsync(library, request.Items, cancellationToken);
                outcome = $"Obnoveno z koše galerie: {request.Items.Count}.";
                await RefreshAsync(CancellationToken.None);
                if (IsTrashOpen)
                {
                    await LoadTrashItemsAsync(library, CancellationToken.None);
                }
            }
            StatusMessage = outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Práce s košem byla zastavena.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Operace s košem galerie selhala.");
            if (outcome is not null)
            {
                SetTrashSelectionCount(0);
                TrashItems = [];
            }
            StatusMessage = outcome is null ? $"Práci s košem se nepodařilo dokončit: {exception.Message}"
                : $"{outcome} Zobrazení aktualizuj příkazem Aktualizovat.";
        }
        finally { IsBusy = false; }
    }
}

internal sealed record GalleryTrashRequest(bool Empty, IReadOnlyList<GalleryTrashItem> Items);

public sealed class TrashReviewRow(GalleryTrashItem item)
{
    public GalleryTrashItem Item => item;
    public string Name => Path.GetFileName(item.RelativePath);
    public string TrashedAtLabel => $"Přesunuto {item.TrashedAt.LocalDateTime:g}";
}
