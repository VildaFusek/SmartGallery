using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using SmartGallery.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SmartGallery
{
    /// <summary>
    /// The main content page displayed inside the application window.
    /// Add your UI logic, event handlers, and data binding here.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private readonly SemaphoreSlim _mediaReadSlots = new(4);
        private readonly Dictionary<FrameworkElement, CancellationTokenSource> _mediaRequests = new();
        private ContentDialog? _galleryConfirmation;

        public GalleryViewModel ViewModel { get; }
        public ModelFooterViewModel FooterViewModel { get; }

        public MainPage()
        {
            var services = ((App)Microsoft.UI.Xaml.Application.Current).Services;
            ViewModel = services.GetRequiredService<GalleryViewModel>();
            FooterViewModel = services.GetRequiredService<ModelFooterViewModel>();
            InitializeComponent();
            InitializeGalleryInteraction();
        }

        private void OnThemeClick(object sender, RoutedEventArgs e)
        {
            if (sender is RadioMenuFlyoutItem { Tag: string themeName } &&
                Enum.TryParse<ElementTheme>(themeName, out var theme) &&
                XamlRoot?.Content is FrameworkElement root)
            {
                root.RequestedTheme = theme;
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.PropertyChanged += OnGalleryPropertyChanged;
            ViewModel.PhotoAnalyzed += OnPhotoAnalyzed;
            UpdateTimelineSource();
            RebuildFolderTree();
            if (ViewModel.ReloadCommand.CanExecute(null))
            {
                await ViewModel.ReloadCommand.ExecuteAsync(null);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.PropertyChanged -= OnGalleryPropertyChanged;
            ViewModel.PhotoAnalyzed -= OnPhotoAnalyzed;
            CancelPendingOperations();
        }

        internal Task PendingGalleryChanges => Task.WhenAll(
            ViewModel.DeleteCommand.ExecutionTask ?? Task.CompletedTask,
            ViewModel.AnalyzeGalleryCommand.ExecutionTask ?? Task.CompletedTask,
            ViewModel.ApplyGalleryNamesCommand.ExecutionTask ?? Task.CompletedTask,
            ViewModel.FindDuplicatesCommand.ExecutionTask ?? Task.CompletedTask,
            ViewModel.OpenTrashCommand.ExecutionTask ?? Task.CompletedTask,
            ViewModel.ManageTrashCommand.ExecutionTask ?? Task.CompletedTask);

        internal void CancelPendingOperations()
        {
            var confirmation = _galleryConfirmation;
            _galleryConfirmation = null;
            confirmation?.Hide();
            ClosePhotoViewer(restoreFocus: false);
            EndMarquee();
            ViewModel.ReloadCommand.Cancel();
            ViewModel.ChooseLibraryCommand.Cancel();
            ViewModel.SaveImportSettingsCommand.Cancel();
            ViewModel.ImportCommand.Cancel();
            ViewModel.VerifyModelCommand.Cancel();
            ViewModel.CaptionCommand.Cancel();
            ViewModel.DescribeSelectionCommand.Cancel();
            ViewModel.DeleteCommand.Cancel();
            ViewModel.AnalyzeGalleryCommand.Cancel();
            ViewModel.ApplyGalleryNamesCommand.Cancel();
            ViewModel.FindDuplicatesCommand.Cancel();
            ViewModel.OpenTrashCommand.Cancel();
            ViewModel.ManageTrashCommand.Cancel();
            ViewModel.CancelTimelineFilter();
            foreach (var visual in _mediaRequests.Keys.ToArray())
            {
                ResetFileVisual(visual);
            }
        }

        private async void OnApplyGalleryNamesClick(object sender, RoutedEventArgs e)
        {
            var files = ViewModel.PendingNameFiles;
            if (XamlRoot is not { } root || !ViewModel.ApplyGalleryNamesCommand.CanExecute(files))
            {
                return;
            }

            const int previewLimit = 8;
            var preview = string.Join("\n\n", files.Take(previewLimit).Select(file =>
                $"{file.RelativePath}\n→ {file.Analysis!.SuggestedName}"));
            if (files.Count > previewLimit)
            {
                preview += $"\n\n… a dalších {files.Count - previewLimit}.";
            }
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "Použít uložené názvy?",
                Content = new ScrollViewer
                {
                    MaxHeight = 360,
                    Content = new TextBlock
                    {
                        Text = $"Přejmenuje se až {files.Count} kopií ve spravované knihovně bez ohledu na výběr nebo filtr. " +
                               $"Originály ani videa se nemění; existující soubory se nepřepisují.\n\n{preview}",
                        TextWrapping = TextWrapping.Wrap
                    }
                },
                PrimaryButtonText = "Přejmenovat kopie",
                CloseButtonText = "Zrušit",
                DefaultButton = ContentDialogButton.Close
            };
            if (await ConfirmGalleryChangeAsync(dialog) && ViewModel.ApplyGalleryNamesCommand.CanExecute(files))
            {
                await ViewModel.ApplyGalleryNamesCommand.ExecuteAsync(files);
            }
        }

        private void OnCancelGalleryWorkClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AnalyzeGalleryCommand.Cancel();
            ViewModel.ApplyGalleryNamesCommand.Cancel();
            ViewModel.FindDuplicatesCommand.Cancel();
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e) => await ConfirmDeleteSelectionAsync();

        private async Task ConfirmDeleteSelectionAsync()
        {
            var files = GalleryGrid.SelectedItems.OfType<GalleryFile>().ToArray();
            if (XamlRoot is not { } root || !ViewModel.DeleteCommand.CanExecute(files))
            {
                return;
            }

            var subject = files.Length == 1 ? files[0].RelativePath : $"Počet označených položek: {files.Length}";
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = files.Length == 1 ? "Přesunout kopii do koše galerie?" : "Přesunout označené kopie do koše?",
                Content = new TextBlock
                {
                    Text = $"{subject}\n\nVýběr zmizí z galerie a uloží se do složky .smartgallery-trash " +
                           "ve spravované knihovně. Zdrojové originály se nezmění.",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "Přesunout",
                CloseButtonText = "Zrušit",
                DefaultButton = ContentDialogButton.Close
            };
            if (await ConfirmGalleryChangeAsync(dialog) && ViewModel.DeleteCommand.CanExecute(files) &&
                files.ToHashSet().SetEquals(GalleryGrid.SelectedItems.OfType<GalleryFile>()))
            {
                await ViewModel.DeleteCommand.ExecuteAsync(files);
            }
        }

        private async Task<bool> ConfirmGalleryChangeAsync(ContentDialog dialog) =>
            await ShowGalleryDialogAsync(dialog) == ContentDialogResult.Primary;

        private async void OnHelpClick(object sender, RoutedEventArgs e)
        {
            if (XamlRoot is not { } root)
            {
                return;
            }
            await ShowGalleryDialogAsync(new ContentDialog
            {
                XamlRoot = root,
                Title = "Klávesové zkratky a koš",
                Content = new TextBlock
                {
                    Text = "Delete v galerii – přesune výběr do koše po potvrzení.\n" +
                           "Ctrl+A – označí všechna zobrazená média.\n" +
                           "Enter nebo dvojklik – otevře fotografii.\n" +
                           "Alt+Enter v galerii – přepne náhledy a podrobnosti.\n\n" +
                           "Koš galerie najdeš na konci stromu vlevo. Odtud můžeš vybrané kopie obnovit. " +
                           "Vysypání koše je trvalé.",
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "Zavřít",
                DefaultButton = ContentDialogButton.Close
            });
        }

        private async Task<ContentDialogResult> ShowGalleryDialogAsync(ContentDialog dialog)
        {
            if (_galleryConfirmation is not null)
            {
                return ContentDialogResult.None;
            }
            _galleryConfirmation = dialog;
            try
            {
                var result = await dialog.ShowAsync();
                return _galleryConfirmation == dialog ? result : ContentDialogResult.None;
            }
            finally { _galleryConfirmation = null; }
        }

        private void OnTileChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
        {
            if (GetFileVisual(e) is { } visual)
            {
                ResetFileVisual(visual);
            }
            if (!e.InRecycleQueue)
            {
                e.RegisterUpdateCallback(OnFileVisualPhase);
            }
        }

        private void OnFileVisualPhase(ListViewBase sender, ContainerContentChangingEventArgs e)
        {
            if (!e.InRecycleQueue && e.Item is GalleryFile file && GetFileVisual(e) is { } visual)
            {
                StartFileVisual(visual, file);
            }
        }

        private static FrameworkElement? GetFileVisual(ContainerContentChangingEventArgs e) =>
            GetFileVisual(e.ItemContainer);

        private static FrameworkElement? GetFileVisual(SelectorItem container) =>
            container.ContentTemplateRoot is FrameworkElement root
                ? root.Name == "FileDetails" ? root : root.FindName("TileThumbnail") as Image
                : null;

        private void OnPhotoAnalyzed(GalleryFile file)
        {
            if (GalleryGrid.ContainerFromItem(file) is SelectorItem container &&
                GetFileVisual(container) is { } visual && ReferenceEquals(visual.Tag, file))
            {
                SetAnalysisBadge(visual, file);
            }
        }

        private void SetAnalysisBadge(FrameworkElement visual, GalleryFile? file)
        {
            var root = visual is Image image ? image.Parent as FrameworkElement : visual;
            if (root?.FindName("AnalysisBadge") is not Border badge)
            {
                return;
            }
            var analysis = file is null ? null : ViewModel.GetPhotoAnalysis(file);
            badge.Visibility = analysis is null ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(badge, analysis is null ? null :
                $"Popisek AI{(analysis.Truncated ? " (zkrácený)" : "")}: {analysis.Caption}\n" +
                $"Návrh názvu: {analysis.SuggestedName ?? "není k dispozici"}");
        }

        private void OnFileVisualLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: GalleryFile file } visual)
            {
                StartFileVisual(visual, file);
            }
        }

        private void OnFileVisualUnloaded(object sender, RoutedEventArgs e) =>
            ResetFileVisual((FrameworkElement)sender);

        private void StartFileVisual(FrameworkElement visual, GalleryFile file)
        {
            if (ReferenceEquals(visual.Tag, file) || (visual is Image) == _showFileDetails)
            {
                return;
            }
            ResetFileVisual(visual);
            visual.Tag = file;
            SetAnalysisBadge(visual, file);
            if (visual is Image image)
            {
                _ = LoadFileVisualAsync(image, (storageFile, token) => LoadThumbnailAsync(image, storageFile, token), file);
            }
            else
            {
                StartDetailsVisual(visual, file);
            }
        }

        private void ResetFileVisual(FrameworkElement visual)
        {
            if (_mediaRequests.Remove(visual, out var request))
            {
                request.Cancel();
            }
            visual.Tag = null;
            SetAnalysisBadge(visual, null);
            if (visual is Image image)
            {
                image.Source = null;
                SetReviewPlaceholder(image, Visibility.Visible);
            }
            else if (visual.FindName("MediaMetadata") is TextBlock metadata)
            {
                metadata.Text = "";
            }
        }

        private Task LoadFileVisualAsync(FrameworkElement visual,
            Func<StorageFile, CancellationToken, Task> load, GalleryFile file) =>
            LoadFileVisualAsync(visual, load, file.RelativePath, token => ViewModel.GetGalleryReadPathAsync(file, token));

        private async Task LoadFileVisualAsync(FrameworkElement visual,
            Func<StorageFile, CancellationToken, Task> load, string label, Func<CancellationToken, Task<string>> getPath)
        {
            using var request = new CancellationTokenSource();
            _mediaRequests.Add(visual, request);
            var token = request.Token;
            var acquired = false;
            try
            {
                await _mediaReadSlots.WaitAsync(token);
                acquired = true;
                var path = await getPath(token);
                var storageFile = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
                await load(storageFile, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested && visual.FindName("MediaMetadata") is TextBlock metadata)
                {
                    metadata.Text = "Další údaje nejsou dostupné.";
                }
                System.Diagnostics.Debug.WriteLine($"Média {label}: {exception.Message}");
            }
            finally
            {
                if (acquired)
                {
                    _mediaReadSlots.Release();
                }
                if (_mediaRequests.TryGetValue(visual, out var current) && current == request)
                {
                    _mediaRequests.Remove(visual);
                }
            }
        }

        private static async Task LoadThumbnailAsync(Image image, StorageFile file, CancellationToken token,
            bool fallbackToPhoto = false)
        {
            using var thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem, 256, ThumbnailOptions.UseCurrentScale).AsTask(token);
            if (thumbnail?.Type == ThumbnailType.Image)
            {
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail).AsTask(token);
                token.ThrowIfCancellationRequested();
                image.Source = bitmap;
            }
            else if (fallbackToPhoto)
            {
                using var stream = await file.OpenReadAsync().AsTask(token);
                var bitmap = new BitmapImage { DecodePixelWidth = 256 };
                await bitmap.SetSourceAsync(stream).AsTask(token);
                token.ThrowIfCancellationRequested();
                image.Source = bitmap;
            }
        }

        private void OnImportDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            e.AcceptedOperation = ViewModel.CanStartImport &&
                e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy : DataPackageOperation.None;
            if (e.AcceptedOperation == DataPackageOperation.Copy)
            {
                e.DragUIOverride.Caption = "Importovat kopie ze složky";
                e.DragUIOverride.IsCaptionVisible = true;
            }
        }

        private async void OnImportDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (!ViewModel.CanStartImport || !e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            string? sourcePath = null;
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count == 1 && items[0] is StorageFolder folder &&
                    !string.IsNullOrWhiteSpace(folder.Path))
                {
                    sourcePath = folder.Path;
                    e.AcceptedOperation = DataPackageOperation.Copy;
                }
                else
                {
                    e.AcceptedOperation = DataPackageOperation.None;
                    ViewModel.ReportImportInputError(
                        "V tomto kroku přetáhni jednu složku. Jednotlivé soubory a více složek zatím nejsou podporovány.");
                }
            }
            catch (Exception exception)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                ViewModel.ReportImportInputError($"Přetaženou složku nelze načíst: {exception.Message}");
            }
            finally
            {
                deferral.Complete();
            }

            if (sourcePath is not null && ViewModel.CanStartImport)
            {
                await ViewModel.ImportCommand.ExecuteAsync(sourcePath);
            }
        }
    }
}
