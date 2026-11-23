using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using SmartGallery.Application.Catalog;
using SmartGallery.Application.AI;
using SmartGallery.Application.Importing;
using SmartGallery.Domain;
using SmartGallery.Services;

namespace SmartGallery.ViewModels;

public sealed partial class GalleryViewModel : ObservableObject, IDisposable
{
    private readonly ICatalogStatusReader _statusReader;
    private readonly ILibraryCatalog _libraryCatalog;
    private readonly IManagedLibraryRegistration _registration;
    private readonly ILibraryImportSettings _importSettings;
    private readonly IFolderPicker _folderPicker;
    private readonly IImportDialogService _importDialog;
    private readonly ILocalCaptionModelInspector _modelInspector;
    private readonly AppReadinessState _readiness;
    private readonly IPhotoCaptionDialogService _captionDialog;
    private readonly GalleryPhotoAnalyzer _photoAnalyzer;
    private readonly ILogger<GalleryViewModel> _logger;
    private string _emptyMessage = "Katalog fotografií a videí";
    private string _statusMessage = "Stav katalogu zatím nebyl načten.";
    private string _librarySummary = "Cílová složka zatím nebyla načtena.";
    private bool _isBusy;
    private bool _canRegisterLibrary;
    private bool _canEditImportSettings;
    private Library? _library;
    private int _selectedLayoutIndex;
    private int _selectedNamingIndex;
    private string _modelCheckSummary = "Model se připravuje automaticky; nezměněné soubory používají záznam ověření. Popisek lze vyzkoušet samostatně.";

    public GalleryViewModel(
        ICatalogStatusReader statusReader,
        ILibraryCatalog libraryCatalog,
        IGalleryCatalog galleryCatalog,
        IManagedLibraryRegistration registration,
        ILibraryImportSettings importSettings,
        IFolderPicker folderPicker,
        IImportDialogService importDialog,
        ILocalCaptionModelInspector modelInspector,
        AppReadinessState readiness,
        IPhotoCaptionDialogService captionDialog,
        GalleryPhotoAnalyzer photoAnalyzer,
        GalleryPhotoDuplicates photoDuplicates,
        IMediaDateReader mediaDates,
        ILogger<GalleryViewModel> logger)
    {
        _statusReader = statusReader;
        _libraryCatalog = libraryCatalog;
        _galleryCatalog = galleryCatalog;
        _registration = registration;
        _importSettings = importSettings;
        _folderPicker = folderPicker;
        _importDialog = importDialog;
        _modelInspector = modelInspector;
        _readiness = readiness;
        _captionDialog = captionDialog;
        _photoAnalyzer = photoAnalyzer;
        _photoDuplicates = photoDuplicates;
        _mediaDates = mediaDates;
        _logger = logger;
        ReloadCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
        ChooseLibraryCommand = new AsyncRelayCommand(ChooseLibraryAsync, () => !IsBusy && _canRegisterLibrary);
        SaveImportSettingsCommand = new AsyncRelayCommand(
            SaveImportSettingsAsync, () => CanEditImportSettings && HasUnsavedSettings);
        ImportCommand = new AsyncRelayCommand<string?>(ImportAsync, _ => CanStartImport);
        VerifyModelCommand = new AsyncRelayCommand(VerifyModelAsync, () => !IsBusy);
        CaptionCommand = new AsyncRelayCommand(CaptionAsync, () => !IsBusy && _readiness.CaptioningReady);
        DescribeSelectionCommand = new AsyncRelayCommand(DescribeSelectionAsync,
            () => CanBrowse && !IsBusy && _readiness.CaptioningReady && SelectedFile?.Kind == MediaKind.Photo);
        DeleteCommand = new AsyncRelayCommand<IReadOnlyList<GalleryFile>>(DeleteAsync,
            files => CanChangeFilters && files is { Count: > 0 });
        DeletePermanentlyCommand = new AsyncRelayCommand<IReadOnlyList<GalleryFile>>(DeletePermanentlyAsync,
            files => CanChangeFilters && files is { Count: > 0 });
        AnalyzeGalleryCommand = new AsyncRelayCommand(AnalyzeGalleryAsync, CanAnalyzeGallery);
        ApplyGalleryNamesCommand = new AsyncRelayCommand<IReadOnlyList<GalleryFile>>(ApplyGalleryNamesAsync,
            files => CanApplyGalleryNames && files is { Count: > 0 });
        FindDuplicatesCommand = new AsyncRelayCommand<Func<PhotoDuplicateScan, Task<IReadOnlyList<PhotoDuplicateSelection>?>>>(
            FindDuplicatesAsync, review => review is not null && CanFindDuplicates);
        OpenTrashCommand = new AsyncRelayCommand(OpenTrashAsync, () => CanNavigateLibrary && !IsBusy && !IsTrashOpen);
        ManageTrashCommand = new AsyncRelayCommand<GalleryTrashRequest>(ManageTrashAsync,
            request => request is { Items.Count: > 0 } && IsTrashOpen && !IsBusy);
        _readiness.PropertyChanged += OnReadinessChanged;
    }

    public string Title => IsTrashOpen ? "Koš galerie" : "Galerie";

    public string EmptyMessage
    {
        get => _emptyMessage;
        private set => SetProperty(ref _emptyMessage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string LibrarySummary
    {
        get => _librarySummary;
        private set => SetProperty(ref _librarySummary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ReloadCommand.NotifyCanExecuteChanged();
                ChooseLibraryCommand.NotifyCanExecuteChanged();
                SaveImportSettingsCommand.NotifyCanExecuteChanged();
                ImportCommand.NotifyCanExecuteChanged();
                VerifyModelCommand.NotifyCanExecuteChanged();
                CaptionCommand.NotifyCanExecuteChanged();
                DescribeSelectionCommand.NotifyCanExecuteChanged();
                DeleteCommand.NotifyCanExecuteChanged();
                DeletePermanentlyCommand.NotifyCanExecuteChanged();
                NotifyTrashActions();
                NotifyGalleryAnalysisActions();
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(CanShowDetails));
                OnPropertyChanged(nameof(CanBrowse));
                OnPropertyChanged(nameof(CanNavigateLibrary));
                OnPropertyChanged(nameof(CanChangeFilters));
                OnPropertyChanged(nameof(CanEditImportSettings));
                OnPropertyChanged(nameof(CanStartImport));
                UpdateImportReadiness();
                if (!value)
                {
                    TryStartCatalogRepair();
                }
            }
        }
    }

    public string StageMessage =>
        "Import ověřuje kopie pomocí SHA-256. Datum bere z metadat, jinak z času souboru. Originály se nemění.";

    public bool CanEditImportSettings => _canEditImportSettings && !IsBusy;

    public bool CanStartImport => !IsBusy && _library is not null && !HasUnsavedSettings;

    public int SelectedLayoutIndex
    {
        get => _selectedLayoutIndex;
        set
        {
            if (value is >= 0 and <= 2 && SetProperty(ref _selectedLayoutIndex, value))
            {
                NotifyImportSettingsChanged();
            }
        }
    }

    public int SelectedNamingIndex
    {
        get => _selectedNamingIndex;
        set
        {
            if (value is >= 0 and <= 1 && SetProperty(ref _selectedNamingIndex, value))
            {
                NotifyImportSettingsChanged();
            }
        }
    }

    public string ExampleRelativePath
    {
        get
        {
            var folder = SelectedLayoutIndex switch
            {
                0 => @"2026\11\",
                1 => @"2026\2026-11-18\",
                _ => @"Dovolená\Den 1\"
            };
            var name = SelectedNamingIndex == 0
                ? "IMG_0001.jpg"
                : "20261118_IMG_0001.jpg";
            return folder + name;
        }
    }

    public string ExampleVideoRelativePath => @"2026\Video\" +
        (SelectedNamingIndex == 0 ? "VID_0001.mp4" : "20261118_VID_0001.mp4");

    public string ImportSettingsMessage => _library is null
        ? "Nejprve nastav cílovou složku a načti katalog."
        : !_canEditImportSettings
            ? "Úprava pravidel nyní není dostupná; u knihovny s importovanými soubory je zatím zakázaná."
            : HasUnsavedSettings ? "Změny pravidel nejsou uloženy." : "Zobrazená pravidla jsou uložena.";

    private bool HasUnsavedSettings => _library is not null &&
        (_library.FolderLayout != SelectedFolderLayout || _library.FileNaming != SelectedFileNaming);

    private LibraryFolderLayout SelectedFolderLayout => (LibraryFolderLayout)SelectedLayoutIndex;

    private LibraryFileNaming SelectedFileNaming => (LibraryFileNaming)SelectedNamingIndex;

    public IAsyncRelayCommand ReloadCommand { get; }

    public IAsyncRelayCommand ChooseLibraryCommand { get; }

    public IAsyncRelayCommand SaveImportSettingsCommand { get; }

    public IAsyncRelayCommand<string?> ImportCommand { get; }

    public IAsyncRelayCommand VerifyModelCommand { get; }

    public IAsyncRelayCommand CaptionCommand { get; }

    private void OnReadinessChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppReadinessState.CaptioningReady))
        {
            CaptionCommand.NotifyCanExecuteChanged();
            DescribeSelectionCommand.NotifyCanExecuteChanged();
            AnalyzeGalleryCommand.NotifyCanExecuteChanged();
        }
    }

    public void Dispose() => _readiness.PropertyChanged -= OnReadinessChanged;

    private async Task CaptionAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await _captionDialog.PickAndDescribeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Otevření lokálního popisku selhalo.");
            StatusMessage = $"Popisek se nepodařilo otevřít: {exception.Message}";
        }
        finally { IsBusy = false; }
    }

    public string LocalModelDirectory => _modelInspector.ModelDirectory;

    public string ModelCheckSummary
    {
        get => _modelCheckSummary;
        private set => SetProperty(ref _modelCheckSummary, value);
    }

    public void ReportImportInputError(string message)
    {
        StatusMessage = message;
    }

    private void NotifyImportSettingsChanged()
    {
        OnPropertyChanged(nameof(ExampleRelativePath));
        OnPropertyChanged(nameof(ExampleVideoRelativePath));
        OnPropertyChanged(nameof(ImportSettingsMessage));
        SaveImportSettingsCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStartImport));
        UpdateImportReadiness();
    }

    private void UpdateImportReadiness()
    {
        _readiness.Import = IsBusy ? "Import: probíhá operace" :
            _library is null ? (_canRegisterLibrary ? "Import: nastavte knihovnu" : "Import: nedostupný") :
            HasUnsavedSettings ? "Import: uložte pravidla" : "Import: připraven";
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        _canRegisterLibrary = false;
        _canEditImportSettings = false;
        _library = null;
        NotifyImportSettingsChanged();
        EmptyMessage = "Katalog fotografií a videí";
        StatusMessage = "Načítání stavu katalogu…";
        try
        {
            var (_, auditMessage) = await AuditCatalogAsync(cancellationToken);
            await RefreshAsync(cancellationToken);
            if (auditMessage is not null)
            {
                StatusMessage = auditMessage;
            }
            if (IsTrashOpen && _library is { } library)
            {
                try
                {
                    await LoadTrashItemsAsync(library, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(exception, "Aktualizace koše galerie selhala.");
                    StatusMessage = $"Koš galerie se nepodařilo aktualizovat: {exception.Message}";
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Načítání bylo zrušeno.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Nepodařilo se načíst stav katalogu.");
            await SetGalleryAsync(null, [], [], CancellationToken.None);
            StatusMessage = $"Katalog se nepodařilo načíst: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyImportSettingsChanged();
        }
    }

    private async Task ChooseLibraryAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            var path = await _folderPicker.PickAsync(cancellationToken);
            if (path is null)
            {
                return;
            }

            StatusMessage = "Ukládání cílové složky…";
            var library = await _registration.CreateAsync(path, cancellationToken);
            _canRegisterLibrary = false;
            LibrarySummary = $"Cílová složka: {library.RootPath}";
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Operace byla zrušena. Pro ověření uloženého stavu znovu načti katalog.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Nepodařilo se nastavit cílovou složku galerie.");
            StatusMessage = $"Nastavení cílové složky se nepodařilo dokončit: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var (status, libraries) = await ReadCatalogStateAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _library = libraries.Count == 1 ? libraries[0] : null;
        _canEditImportSettings = _library is not null && !status.HasAssets;
        if (_library is not null)
        {
            if (!Enum.IsDefined(_library.FolderLayout) || !Enum.IsDefined(_library.FileNaming))
            {
                _canEditImportSettings = false;
                _library = null;
                throw new InvalidOperationException("Knihovna obsahuje neznámá pravidla importu.");
            }

            SelectedLayoutIndex = (int)_library.FolderLayout;
            SelectedNamingIndex = (int)_library.FileNaming;
        }
        NotifyImportSettingsChanged();

        _canRegisterLibrary = libraries.Count == 0;
        LibrarySummary = libraries.Count == 0
            ? "Cílová složka galerie zatím není nastavena."
            : $"Cílová složka: {libraries[0].RootPath}";
        if (libraries.Count > 1)
        {
            LibrarySummary += " Katalog obsahuje více knihoven; jejich přepínání zatím není podporováno.";
        }

        await RefreshGalleryAsync(cancellationToken);
        StatusMessage = $"Katalog připojen. Počet knihoven: {libraries.Count}. SQLite journal: {status.JournalMode}.";
        if (!string.Equals(status.JournalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage += " Upozornění: očekávaný režim WAL není aktivní.";
        }
    }

    private async Task<(CatalogStatus Status, IReadOnlyList<Library> Libraries)> ReadCatalogStateAsync(
        CancellationToken cancellationToken)
    {
        _readiness.Catalog = "Katalog: ověřuji připojení";
        try
        {
            var status = await _statusReader.ReadAsync(cancellationToken);
            var libraries = await _libraryCatalog.GetLibrariesAsync(cancellationToken);
            _readiness.Catalog = "Katalog: připojen";
            return (status, libraries);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _readiness.Catalog = "Katalog: ověření přerušeno";
            throw;
        }
        catch
        {
            _readiness.Catalog = "Katalog: chyba připojení";
            throw;
        }
    }

    private async Task SaveImportSettingsAsync(CancellationToken cancellationToken)
    {
        var library = _library;
        if (library is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _importSettings.SaveAsync(
                library.Id, SelectedFolderLayout, SelectedFileNaming, cancellationToken);
            _canEditImportSettings = false;
            await RefreshAsync(cancellationToken);
            StatusMessage = "Pravidla importu byla uložena. " + StatusMessage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Ukládání bylo zrušeno. Znovu načti katalog pro ověření uložených pravidel.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Nepodařilo se uložit pravidla importu.");
            StatusMessage = $"Uložení pravidel se nepodařilo dokončit: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyImportSettingsChanged();
        }
    }

    private async Task ImportAsync(string? sourceDirectory, CancellationToken cancellationToken)
    {
        if (!CanStartImport || _library is not { } library)
        {
            return;
        }

        IsBusy = true;
        try
        {
            sourceDirectory ??= await _folderPicker.PickAsync(cancellationToken);
            if (sourceDirectory is null)
            {
                return;
            }

            await _importDialog.ImportFolderAsync(library.Id, sourceDirectory, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Import byl přerušen. Znovu načti katalog pro ověření dokončených kopií.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Import nebo následné načtení katalogu selhaly.");
            StatusMessage = $"Import/obnovení stavu selhalo: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
    private async Task VerifyModelAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ModelCheckSummary = "Ověřuji velikosti a SHA-256 lokálních souborů Florence-2…";
        try
        {
            var result = await _modelInspector.InspectAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.FilesVerified)
            {
                ModelCheckSummary = $"Florence-2: ověřeno {result.VerifiedFiles}/{result.ExpectedFiles} souborů. " +
                    "Podrobná kontrola potvrdila integritu souborů. Popisek lze vyzkoušet samostatně; automatické názvy při importu ještě nejsou zapnuté.";
            }
            else
            {
                ModelCheckSummary = $"Florence-2: ověřeno {result.VerifiedFiles}/{result.ExpectedFiles} souborů.\n" +
                    string.Join("\n", result.Issues.Take(3)) +
                    (result.Issues.Count > 3 ? $"\nDalší problémy: {result.Issues.Count - 3}. Podrobnosti jsou v debug logu." : "");
                foreach (var issue in result.Issues)
                {
                    _logger.LogWarning("Kontrola modelu: {Issue}", issue);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ModelCheckSummary = "Ověření modelu bylo zrušeno; soubory nebyly změněny.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Ověření lokálního modelu selhalo.");
            ModelCheckSummary = $"Ověření modelu selhalo: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
