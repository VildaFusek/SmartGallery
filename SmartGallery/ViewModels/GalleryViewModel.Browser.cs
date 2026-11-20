using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;

namespace SmartGallery.ViewModels;

public sealed partial class GalleryViewModel
{
    private readonly IGalleryCatalog _galleryCatalog;
    private Dictionary<string, List<GalleryFolder>> _folders = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GalleryTimelineGroup> _timeline = [];
    private IReadOnlyList<GalleryFile> _catalogFiles = [];
    private CancellationTokenSource? _filterRequest;
    private Guid? _displayedLibraryId;
    private string _currentFolder = "";
    private string _nameFilter = "";
    private int _mediaFilterIndex;
    private int _analysisFilterIndex;
    private GalleryFile? _selectedFile;
    private int _selectionCount;
    private IReadOnlyList<GalleryFile> _visibleFiles = [];
    private int[] _timelineGroupStarts = [];

    public IAsyncRelayCommand DescribeSelectionCommand { get; }
    public IAsyncRelayCommand<IReadOnlyList<GalleryFile>> DeleteCommand { get; }

    public string CurrentFolder => _currentFolder;
    public GalleryFolder? RootFolder { get; private set; }
    public bool HasDisplayedLibrary => RootFolder is not null;
    public bool CanNavigateLibrary => (!IsBusy || _isAnalyzing) && _library is { } library && library.Id == _displayedLibraryId;
    public bool CanBrowse => CanNavigateLibrary && !IsTrashOpen;
    public bool CanChangeFilters => CanBrowse && !IsBusy;
    public int SelectionCount => _selectionCount;
    public string SelectionLabel
    {
        get
        {
            var count = IsTrashOpen ? _trashSelectionCount : SelectionCount;
            return count == 0 ? "" : $"Označeno: {count}";
        }
    }
    public bool CanDelete => CanBrowse && !IsBusy && SelectionCount > 0;
    public bool CanShowDetails => CanBrowse && SelectionCount == 1;

    public string NameFilter
    {
        get => _nameFilter;
        set
        {
            if (SetProperty(ref _nameFilter, value))
            {
                _ = ApplyFilterAsync();
            }
        }
    }

    public int MediaFilterIndex
    {
        get => _mediaFilterIndex;
        set
        {
            if (value is >= 0 and <= 2 && SetProperty(ref _mediaFilterIndex, value))
            {
                _ = ApplyFilterAsync();
            }
        }
    }

    public int AnalysisFilterIndex
    {
        get => _analysisFilterIndex;
        set
        {
            if (value is >= 0 and <= 3 && SetProperty(ref _analysisFilterIndex, value))
            {
                _ = ApplyFilterAsync();
            }
        }
    }

    public IReadOnlyList<GalleryFile> VisibleFiles
    {
        get => _visibleFiles;
        private set => SetProperty(ref _visibleFiles, value);
    }

    public GalleryFile? SelectedFile => _selectedFile;
    public IReadOnlyList<GalleryTimelineGroup> TimelineGroups { get; private set; } = [];

    public void SetSelection(int count, GalleryFile? singleFile)
    {
        var countChanged = SetProperty(ref _selectionCount, count, nameof(SelectionCount));
        if (SetProperty(ref _selectedFile, singleFile, nameof(SelectedFile)) || countChanged)
        {
            DescribeSelectionCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(CanShowDetails));
            OnPropertyChanged(nameof(SelectionLabel));
        }
    }

    public IReadOnlyList<GalleryFolder> GetChildFolders(GalleryFolder folder) =>
        _folders.TryGetValue(folder.RelativePath, out var children) ? children : [];

    public void OpenFolder(GalleryFolder folder)
    {
        if (CanNavigateLibrary && _folders.ContainsKey(folder.RelativePath))
        {
            _currentFolder = folder.RelativePath;
            IsTrashOpen = false;
            OnPropertyChanged(nameof(CurrentFolder));
        }
    }

    public GalleryFile? GetTimelineAnchor() =>
        TimelineGroups.FirstOrDefault(group => _currentFolder.Length == 0 ||
            group.YearKey == _currentFolder || group.SectionPath == _currentFolder)?[0];

    internal GalleryTimelineGroup? GetTimelineGroupAt(int index)
    {
        if (index < 0 || index >= VisibleFiles.Count)
        {
            return null;
        }

        var groupIndex = Array.BinarySearch(_timelineGroupStarts, index);
        return TimelineGroups[groupIndex >= 0 ? groupIndex : ~groupIndex - 1];
    }

    internal void TrackTimelineSection(GalleryTimelineGroup group) =>
        SetProperty(ref _currentFolder, group.SectionPath, nameof(CurrentFolder));

    internal void CancelTimelineFilter() => _filterRequest?.Cancel();

    public Task<string> GetGalleryReadPathAsync(GalleryFile file, CancellationToken cancellationToken) =>
        _library is { } library && library.Id == _displayedLibraryId
            ? _galleryCatalog.GetReadPathAsync(library.RootPath, file, cancellationToken)
            : Task.FromException<string>(new IOException("Knihovna není dostupná."));

    private async Task RefreshGalleryAsync(CancellationToken cancellationToken)
    {
        var library = _library;
        if (library is null)
        {
            await SetGalleryAsync(null, [], [], cancellationToken);
            return;
        }

        var files = await _galleryCatalog.GetFilesAsync(library.Id, cancellationToken);
        var timeline = await Task.Run(() => GalleryTimelineGroup.Build(files, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await SetGalleryAsync(library.Id, timeline, files, cancellationToken);
    }

    private async Task SetGalleryAsync(Guid? libraryId, IReadOnlyList<GalleryTimelineGroup> timeline,
        IReadOnlyList<GalleryFile> catalogFiles, CancellationToken cancellationToken)
    {
        CancelTimelineFilter();
        var (groups, files) = await FilterTimelineAsync(timeline, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CancelTimelineFilter();
        if (_displayedLibraryId != libraryId)
        {
            _currentFolder = "";
            IsTrashOpen = false;
            TrashItems = [];
        }
        _displayedLibraryId = libraryId;
        _timeline = timeline;
        _catalogFiles = catalogFiles;
        SetAnalysisCounts(catalogFiles);
        PublishTimeline(groups, files);
        OnPropertyChanged(nameof(CanNavigateLibrary));
        OnPropertyChanged(nameof(CanBrowse));
        OnPropertyChanged(nameof(CanChangeFilters));
    }

    private Task<(IReadOnlyList<GalleryTimelineGroup> Groups, IReadOnlyList<GalleryFile> Files)> FilterTimelineAsync(
        IReadOnlyList<GalleryTimelineGroup> timeline, CancellationToken token)
    {
        var term = NameFilter.Trim();
        MediaKind? kind = MediaFilterIndex switch
        {
            1 => MediaKind.Photo,
            2 => MediaKind.Video,
            _ => null
        };
        var analysisFilter = (GalleryAnalysisFilter)AnalysisFilterIndex;
        return Task.Run(() => GalleryTimelineGroup.Filter(timeline, term, kind, analysisFilter, token), token);
    }

    private async Task ApplyFilterAsync()
    {
        CancelTimelineFilter();
        using var request = new CancellationTokenSource();
        _filterRequest = request;
        var token = request.Token;
        try
        {
            await Task.Delay(180, token);
            var (groups, files) = await FilterTimelineAsync(_timeline, token);
            token.ThrowIfCancellationRequested();
            PublishTimeline(groups, files);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Filtrování časové osy selhalo.");
            StatusMessage = "Filtr časové osy se nepodařilo použít.";
        }
        finally
        {
            if (ReferenceEquals(_filterRequest, request))
            {
                _filterRequest = null;
            }
        }
    }

    private void PublishTimeline(IReadOnlyList<GalleryTimelineGroup> groups, IReadOnlyList<GalleryFile> files)
    {
        var years = new List<GalleryFolder>();
        _folders = new(StringComparer.OrdinalIgnoreCase) { [""] = years };
        var starts = new int[groups.Count];
        var groupIndex = 0;
        var fileIndex = 0;
        foreach (var year in groups.GroupBy(group => group.YearKey))
        {
            var sections = new List<GalleryFolder>();
            var yearPhotos = 0;
            var yearVideos = 0;
            foreach (var group in year)
            {
                starts[groupIndex++] = fileIndex;
                fileIndex += group.Count;
                // Každá sekce časové osy obsahuje jen jeden druh médií.
                var photos = group[0].Kind == MediaKind.Photo ? group.Count : 0;
                var videos = group.Count - photos;
                sections.Add(new GalleryFolder(group.SectionLabel, group.SectionPath)
                    { PhotoCount = photos, VideoCount = videos });
                _folders.Add(group.SectionPath, []);
                yearPhotos += photos;
                yearVideos += videos;
            }
            _folders.Add(year.Key, sections);
            years.Add(new GalleryFolder(year.First().YearLabel, year.Key)
                { PhotoCount = yearPhotos, VideoCount = yearVideos });
        }
        while (_currentFolder.Length > 0 && !_folders.ContainsKey(_currentFolder))
        {
            _currentFolder = Path.GetDirectoryName(_currentFolder) ?? "";
        }

        SetSelection(0, null);
        _timelineGroupStarts = starts;
        TimelineGroups = groups;
        VisibleFiles = files;
        OnPropertyChanged(nameof(TimelineGroups));
        RootFolder = _displayedLibraryId is null ? null : new GalleryFolder(_library?.Name ?? "Knihovna", "")
            { PhotoCount = _photoCount, VideoCount = _catalogFiles.Count - _photoCount };
        OnPropertyChanged(nameof(RootFolder));
        OnPropertyChanged(nameof(HasDisplayedLibrary));
        EmptyMessage = _displayedLibraryId is null ? "Nastav cílovou složku spravované knihovny." :
            files.Count > 0 ? $"Zobrazeno: {files.Count} / {_catalogFiles.Count}" :
            _catalogFiles.Count > 0 ? "Filtru neodpovídá žádná položka." :
            "Zatím bez importovaných fotografií a videí.";
    }

    private async Task DescribeSelectionAsync(CancellationToken cancellationToken)
    {
        if (SelectedFile is not { Kind: MediaKind.Photo } file)
        {
            return;
        }
        IsBusy = true;
        try
        {
            var path = await GetGalleryReadPathAsync(file, cancellationToken);
            await _captionDialog.DescribeAsync(path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Otevření popisku fotografie z galerie selhalo.");
            StatusMessage = $"Popisek se nepodařilo otevřít: {exception.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task DeleteAsync(IReadOnlyList<GalleryFile>? files, CancellationToken cancellationToken)
    {
        if (!CanDelete || _library is not { } library || files is null || files.Count != SelectionCount)
        {
            return;
        }
        IsBusy = true;
        string? trashPath = null;
        try
        {
            trashPath = await _galleryCatalog.MoveToTrashAsync(library, files, cancellationToken);
            await RefreshAsync(CancellationToken.None);
            StatusMessage = $"Přesunuto do koše galerie: {files.Count} · {trashPath}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Přesun do koše byl zrušen.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Přesun do koše galerie nebo obnovení katalogu selhalo.");
            StatusMessage = trashPath is null ? $"Přesun do koše selhal: {exception.Message}" :
                $"Kopie jsou v koši ({trashPath}), ale katalog se nepodařilo obnovit. Použij Soubor → Aktualizovat.";
        }
        finally { IsBusy = false; }
    }
}

public sealed record GalleryFolder(string Name, string RelativePath)
{
    public int PhotoCount { get; init; }
    public int VideoCount { get; init; }
    public bool HasPhotos => PhotoCount > 0;
    public bool HasVideos => VideoCount > 0;

    public override string ToString() => Name;
}
