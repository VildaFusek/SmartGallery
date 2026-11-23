using Microsoft.Extensions.Logging;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;

namespace SmartGallery.ViewModels;

public sealed partial class GalleryViewModel
{
    private IReadOnlySet<Guid> _hiddenCatalogFiles = new HashSet<Guid>();
    private CancellationTokenSource? _catalogRepairRequest;
    private Task? _catalogRepair;
    private bool _catalogRepairEnabled;
    private bool _catalogAuditQueued;

    internal Task PendingCatalogRepair => _catalogRepair ?? Task.CompletedTask;

    internal void EnableCatalogRepair() => _catalogRepairEnabled = true;

    internal void CancelCatalogRepair()
    {
        _catalogRepairEnabled = false;
        _catalogAuditQueued = false;
        _catalogRepairRequest?.Cancel();
    }

    private void QueueCatalogRepair(Library library)
    {
        if (_catalogRepairEnabled && _library?.Id == library.Id && _displayedLibraryId == library.Id)
        {
            _catalogAuditQueued = true;
            TryStartCatalogRepair();
        }
    }

    private void TryStartCatalogRepair()
    {
        if (_catalogRepairEnabled && !IsBusy && _catalogRepair is null && _catalogAuditQueued)
        {
            _catalogRepair = RepairMissingFilesAsync();
        }
    }

    private async Task RepairMissingFilesAsync()
    {
        using var request = new CancellationTokenSource();
        _catalogRepairRequest = request;
        var token = request.Token;
        var ownsBusy = false;
        try
        {
            // Multiple thumbnails can report the same incident before the audit starts.
            await Task.Delay(200, token);
            if (IsBusy)
            {
                return;
            }
            _catalogAuditQueued = false;
            ownsBusy = true;
            IsBusy = true;
            var (refresh, message) = await AuditCatalogAsync(token);
            if (refresh)
            {
                await RefreshGalleryAsync(token, keepSortDates: true);
            }
            if (message is not null)
            {
                StatusMessage = message;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Obnovení galerie po opravě katalogu selhalo.");
            StatusMessage = "Zobrazení katalogu obnov přes Soubor → Aktualizovat.";
        }
        finally
        {
            if (ownsBusy)
            {
                IsBusy = false;
            }
            _catalogRepairRequest = null;
            _catalogRepair = null;
            TryStartCatalogRepair();
        }
    }

    private async Task<(bool Refresh, string? Message)> AuditCatalogAsync(CancellationToken token)
    {
        GalleryCatalogAudit audit;
        try
        {
            audit = await _galleryCatalog.AuditFilesAsync(token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Kontrola souborů celého katalogu selhala.");
            return (false, $"Katalog se nepodařilo zkontrolovat: {exception.Message}");
        }

        var changed = !_hiddenCatalogFiles.SetEquals(audit.HiddenFileIds) || audit.RemovedCount > 0;
        _hiddenCatalogFiles = audit.HiddenFileIds;
        foreach (var id in audit.HiddenFileIds)
        {
            _sortDates.TryRemove(id, out _);
        }
        var unavailable = audit.HiddenFileIds.Count - audit.RemovedCount;
        var message = audit.BackupError is { } error
            ? $"Katalog nebyl pročištěn, zálohu nelze vytvořit: {error}"
            : audit.RemovedCount > 0
                ? $"Katalog opraven: odstraněno {audit.RemovedCount} záznamů. Záloha: {audit.BackupPath}"
                : null;
        if (unavailable > 0)
        {
            var hidden = $"Dočasně skryté položky: {unavailable}. Jejich záznamy zůstaly v katalogu.";
            message = message is null ? hidden : $"{message} · {hidden}";
        }
        return (changed, message);
    }
}
