using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;

namespace SmartGallery.ViewModels;

public sealed partial class GalleryViewModel
{
    private readonly Dictionary<Guid, GalleryPhotoAnalysis> _liveAnalyses = [];
    private IReadOnlyList<GalleryFile> _pendingNameFiles = [];
    private bool _isAnalyzing;
    private bool _requiresGalleryRefresh;
    private bool _galleryWorkActive;
    private double _galleryWorkProgress;
    private int _photoCount;
    private int _analyzedPhotoCount;
    private int _pendingNameCount;

    public IAsyncRelayCommand AnalyzeGalleryCommand { get; }
    public IAsyncRelayCommand<IReadOnlyList<GalleryFile>> ApplyGalleryNamesCommand { get; }
    public bool CanApplyGalleryNames => CanBrowse && !IsBusy && !_requiresGalleryRefresh && _pendingNameFiles.Count > 0;
    public IReadOnlyList<GalleryFile> PendingNameFiles => _pendingNameFiles;
    public string AnalysisSummary => $"AI: {_analyzedPhotoCount}/{_photoCount} fotek · návrhy: {_pendingNameCount}";
    internal event Action<GalleryFile>? PhotoAnalyzed;

    public bool GalleryWorkActive
    {
        get => _galleryWorkActive;
        private set => SetProperty(ref _galleryWorkActive, value);
    }

    public double GalleryWorkProgress
    {
        get => _galleryWorkProgress;
        private set => SetProperty(ref _galleryWorkProgress, value);
    }

    internal GalleryPhotoAnalysis? GetPhotoAnalysis(GalleryFile file) =>
        _liveAnalyses.GetValueOrDefault(file.Id) ?? file.Analysis;

    private bool CanAnalyzeGallery() => CanBrowse && !IsBusy && !_requiresGalleryRefresh &&
        _readiness.CaptioningReady && _analyzedPhotoCount < _photoCount;

    private void NotifyGalleryAnalysisActions()
    {
        AnalyzeGalleryCommand.NotifyCanExecuteChanged();
        ApplyGalleryNamesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanApplyGalleryNames));
        FindDuplicatesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanFindDuplicates));
    }

    private void SetAnalysisCounts(IReadOnlyList<GalleryFile> files)
    {
        _liveAnalyses.Clear();
        _requiresGalleryRefresh = false;
        _photoCount = 0;
        _analyzedPhotoCount = 0;
        var pending = new List<GalleryFile>();
        foreach (var file in files)
        {
            if (file.Kind != MediaKind.Photo)
            {
                continue;
            }
            _photoCount++;
            if (file.Analysis is not null)
            {
                _analyzedPhotoCount++;
                if (file.HasPendingName)
                {
                    pending.Add(file);
                }
            }
        }
        _pendingNameFiles = pending;
        _pendingNameCount = pending.Count;
        OnPropertyChanged(nameof(AnalysisSummary));
        NotifyGalleryAnalysisActions();
    }

    private async Task AnalyzeGalleryAsync(CancellationToken cancellationToken)
    {
        if (!CanAnalyzeGallery() || _library is not { } library)
        {
            return;
        }

        _isAnalyzing = true;
        IsBusy = true;
        GalleryWorkProgress = 0;
        GalleryWorkActive = true;
        var total = _photoCount - _analyzedPhotoCount;
        var saved = 0;
        var failed = 0;
        string? firstFailure = null;
        string? interruption = null;
        try
        {
            StatusMessage = $"Příprava analýzy · fotografií: {total}";
            var catalogFiles = _catalogFiles;
            var pendingNames = _pendingNameFiles;
            var (photos, reservedPaths) = await Task.Run(() =>
            {
                var photos = new List<GalleryFile>();
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in catalogFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    paths.Add(file.RelativePath);
                    if (file is { Kind: MediaKind.Photo, Analysis: null })
                    {
                        photos.Add(file);
                    }
                }
                foreach (var file in pendingNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    paths.Add(Path.Combine(file.Folder, file.Analysis!.SuggestedName!));
                }
                return (photos, paths);
            }, cancellationToken);
            var nextSuffixByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var photo in photos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StatusMessage = $"Analýza {saved + failed + 1}/{total} · uloženo: {saved} · chyby: {failed} · {photo.RelativePath}";
                GalleryPhotoAnalysis? analysis = null;
                try
                {
                    var path = await _galleryCatalog.GetReadPathAsync(library.RootPath, photo, cancellationToken);
                    analysis = await _photoAnalyzer.AnalyzeAsync(path, photo, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (analysis.SuggestedName is { } name &&
                        !string.Equals(name, photo.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        analysis = analysis with
                        {
                            SuggestedName = ReserveName(photo, name, reservedPaths, nextSuffixByPath)
                        };
                    }
                }
                catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    LogFailure(photo, exception);
                    analysis = null;
                }

                if (analysis is not null)
                {
                    try
                    {
                        await _galleryCatalog.SavePhotoAnalysisAsync(library, photo, analysis, cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        if (analysis.SuggestedName is { } name &&
                            !string.Equals(name, photo.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            reservedPaths.Remove(Path.Combine(photo.Folder, name));
                        }
                        LogFailure(photo, exception);
                        analysis = null;
                    }
                    if (analysis is not null)
                    {
                        saved++;
                        _liveAnalyses[photo.Id] = analysis;
                        _analyzedPhotoCount++;
                        if (analysis.SuggestedName is { } name &&
                            !string.Equals(name, photo.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            _pendingNameCount++;
                        }
                        OnPropertyChanged(nameof(AnalysisSummary));
                        PhotoAnalyzed?.Invoke(photo);
                    }
                }
                GalleryWorkProgress = (saved + failed) * 100.0 / total;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            interruption = "Analýza zastavena; hotové výsledky zůstaly uložené.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Analýza galerie selhala.");
            interruption = $"Analýza přerušena: {exception.Message}";
        }
        finally
        {
            var refreshError = saved > 0 ? await RefreshAfterGalleryWorkAsync() : "";
            StatusMessage = $"Analýza · uloženo: {saved}/{total} · chyby: {failed}" +
                (interruption is null ? "" : $" · {interruption}") +
                (firstFailure is null ? "" : $" · {firstFailure}") + refreshError;
            GalleryWorkActive = false;
            _isAnalyzing = false;
            IsBusy = false;
        }

        void LogFailure(GalleryFile file, Exception error)
        {
            failed++;
            firstFailure ??= $"{file.RelativePath}: {error.Message}";
            _logger.LogWarning(error, "Analýzu fotografie {File} se nepodařilo uložit nebo dokončit.", file.RelativePath);
        }
    }

    private static string ReserveName(GalleryFile photo, string name, HashSet<string> reservedPaths,
        Dictionary<string, int> nextSuffixByPath)
    {
        var folder = photo.Folder;
        var path = Path.Combine(folder, name);
        if (reservedPaths.Add(path))
        {
            return name;
        }
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var suffix = nextSuffixByPath.GetValueOrDefault(path, 2);
        string candidate;
        do
        {
            candidate = $"{stem}_{suffix++}{extension}";
        } while (!reservedPaths.Add(Path.Combine(folder, candidate)));
        nextSuffixByPath[path] = suffix;
        return candidate;
    }

    private async Task ApplyGalleryNamesAsync(IReadOnlyList<GalleryFile>? files, CancellationToken cancellationToken)
    {
        if (!CanApplyGalleryNames || _library is not { } library || files is null ||
            !ReferenceEquals(files, _pendingNameFiles))
        {
            return;
        }

        IsBusy = true;
        GalleryWorkProgress = 0;
        GalleryWorkActive = true;
        var renamed = 0;
        var skipped = 0;
        string? firstFailure = null;
        string? interruption = null;
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = file.Analysis!.SuggestedName!;
                StatusMessage = $"Přejmenování {renamed + skipped + 1}/{files.Count} · {file.RelativePath} → {name}";
                try
                {
                    await _galleryCatalog.RenamePhotoAsync(library, file, name, cancellationToken);
                    renamed++;
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException ||
                    exception is IOException and not { InnerException: AggregateException })
                {
                    skipped++;
                    firstFailure ??= $"{file.RelativePath}: {exception.Message}";
                    _logger.LogWarning(exception, "Přejmenování {File} bylo vynecháno.", file.RelativePath);
                }
                GalleryWorkProgress = (renamed + skipped) * 100.0 / files.Count;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            interruption = "Přejmenování zastaveno; hotové změny se nevracejí.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Použití uložených názvů selhalo.");
            interruption = $"Přejmenování přerušeno: {exception.Message}";
        }
        finally
        {
            var refreshError = await RefreshAfterGalleryWorkAsync();
            StatusMessage = $"Přejmenováno: {renamed}/{files.Count} · vynecháno: {skipped}" +
                (interruption is null ? "" : $" · {interruption}") +
                (firstFailure is null ? "" : $" · {firstFailure}") + refreshError;
            GalleryWorkActive = false;
            IsBusy = false;
        }
    }

    private async Task<string> RefreshAfterGalleryWorkAsync()
    {
        try
        {
            StatusMessage = "Obnovuji stav galerie z katalogu…";
            await RefreshGalleryAsync(CancellationToken.None);
            return "";
        }
        catch (Exception exception)
        {
            _requiresGalleryRefresh = true;
            _logger.LogError(exception, "Obnovení galerie po operaci s návrhy selhalo.");
            return " · Galerii obnov přes Soubor → Aktualizovat.";
        }
    }
}
