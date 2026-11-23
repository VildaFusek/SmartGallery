using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using SmartGallery.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;

namespace SmartGallery;

public sealed partial class MainPage
{
    private CancellationTokenSource? _previewLoad;
    private GalleryFile? _previewFile;
    private CancellationTokenSource? _photoConversion;
    private Task? _photoConversionTask;
    private Control? _previewReturnFocus;
    private double _thumbnailScale = 1;
    private bool _showFileDetails;
    private bool _fitPreview;

    private void OnDetailsClick(object sender, RoutedEventArgs e) =>
        SetDetailsMode(DetailsToggle.IsChecked == true, ViewModel.SelectedFile);

    private void SetDetailsMode(bool show, GalleryFile? anchor)
    {
        StopTileVideo();
        if (_showFileDetails == show)
        {
            return;
        }

        if (anchor is null && GalleryGrid.ItemsPanelRoot is ItemsWrapGrid panel &&
            panel.FirstVisibleIndex >= 0 && panel.FirstVisibleIndex < GalleryGrid.Items.Count)
        {
            anchor = GalleryGrid.Items[panel.FirstVisibleIndex] as GalleryFile;
        }
        EndMarquee();
        _showFileDetails = show;
        foreach (var visual in _mediaRequests.Keys.ToArray())
        {
            ResetFileVisual(visual);
        }
        DetailsToggle.IsChecked = show;
        UpdateGalleryItemSize();
        GalleryGrid.ItemTemplate = (DataTemplate)Resources[show ? "DetailsTemplate" : "ThumbnailTemplate"];
        if (anchor is not null)
        {
            GalleryGrid.UpdateLayout();
            GalleryGrid.ScrollIntoView(anchor);
        }
    }

    private void OnThumbnailSizeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string tag } || !int.TryParse(tag, out var percent) ||
            _thumbnailScale == percent / 100d)
        {
            return;
        }

        EndMarquee();
        _thumbnailScale = percent / 100d;
        if (!_showFileDetails && GalleryGrid.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            var last = Math.Min(panel.LastCacheIndex, GalleryGrid.Items.Count - 1);
            for (var index = Math.Max(0, panel.FirstCacheIndex); index <= last; index++)
            {
                if (GalleryGrid.ContainerFromIndex(index) is GridViewItem { Content: GalleryFile file } tile &&
                    GetFileVisual(tile) is Image image && ReferenceEquals(image.Tag, file))
                {
                    ResetFileVisual(image);
                    StartFileVisual(image, file);
                }
            }
            UpdateGalleryItemSize(panel);
        }
    }

    private void OnGalleryHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdateGalleryItemSize();

    private void OnGalleryPanelLoaded(object sender, RoutedEventArgs e) => UpdateGalleryItemSize((ItemsWrapGrid)sender);

    private void UpdateGalleryItemSize()
    {
        if (GalleryGrid?.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            UpdateGalleryItemSize(panel);
        }
    }

    private void UpdateGalleryItemSize(ItemsWrapGrid panel)
    {
        var viewportWidth = Math.Max(1, GalleryHost.ActualWidth - 24);
        var thumbnailWidth = 172 * _thumbnailScale;
        panel.MaximumRowsOrColumns = _showFileDetails ? 1 : -1;
        panel.ItemWidth = _showFileDetails
            ? viewportWidth
            : GalleryHost.ActualWidth > 0 ? Math.Min(thumbnailWidth, viewportWidth) : thumbnailWidth;
        panel.ItemHeight = _showFileDetails ? double.NaN : 150 * _thumbnailScale;
    }

    private void StartDetailsVisual(FrameworkElement visual, GalleryFile file)
    {
        if (visual.FindName("ExtensionBadge") is TextBlock badge)
        {
            badge.Text = ThumbnailMetadata.FormatExtension(file.Name);
        }
        if (visual.FindName("FileMetadata") is TextBlock metadata)
        {
            var kind = file.Kind == MediaKind.Photo ? "Fotografie" : "Video";
            metadata.Text = $"{kind} · {file.SizeBytes:N0} B · Změněno při importu: {file.LastWriteTimeUtc.ToLocalTime():g}";
        }
        if (visual.FindName("MediaMetadata") is TextBlock media)
        {
            _ = LoadFileVisualAsync(visual, (storageFile, token) =>
                LoadMediaDetailsAsync(storageFile, file.Kind, media, token), file);
        }
    }

    private static async Task LoadMediaDetailsAsync(StorageFile file, MediaKind kind, TextBlock metadata, CancellationToken token)
    {
        uint width, height;
        var duration = TimeSpan.Zero;
        if (kind == MediaKind.Photo)
        {
            var properties = await file.Properties.GetImagePropertiesAsync().AsTask(token);
            (width, height) = (properties.Width, properties.Height);
        }
        else
        {
            var properties = await file.Properties.GetVideoPropertiesAsync().AsTask(token);
            (width, height, duration) = (properties.Width, properties.Height, properties.Duration);
        }
        token.ThrowIfCancellationRequested();
        var dimensions = width > 0 && height > 0 ? $"{width:N0} × {height:N0} px" : "";
        var length = duration > TimeSpan.Zero ? $"Délka: {duration:g}" : "";
        metadata.Text = dimensions.Length == 0 ? length :
            length.Length == 0 ? dimensions : $"{dimensions} · {length}";
    }

    private Task ShowPhotoAsync(GalleryFile file, Control? returnFocus) =>
        ShowPhotoAsync(file.Name, returnFocus, token => ViewModel.GetGalleryReadPathAsync(file, token), file);

    private async Task ShowPhotoAsync(string name, Control? returnFocus, Func<CancellationToken, Task<string>> getPath,
        GalleryFile? galleryFile = null)
    {
        if (PhotoViewer.Visibility == Visibility.Visible || _galleryConfirmation is not null)
        {
            return;
        }

        StopTileVideo();
        _previewFile = galleryFile;
        PhotoConvertButton.Visibility = ShowWhen(galleryFile is not null);
        using var request = new CancellationTokenSource();
        _previewLoad = request;
        var token = request.Token;
        _previewReturnFocus = returnFocus;
        EndMarquee();
        _fitPreview = true;
        PreviewName.Text = name;
        PreviewStatus.Text = "Načítám fotografii…";
        FitPreviewButton.IsEnabled = false;
        ActualSizeButton.IsEnabled = false;
        GalleryWorkspace.Visibility = Visibility.Collapsed;
        PhotoViewer.Visibility = Visibility.Visible;
        ClosePreviewButton.Focus(FocusState.Programmatic);

        try
        {
            var path = await getPath(token);
            var photo = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
            using var stream = await photo.OpenReadAsync().AsTask(token);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
            var width = decoder.PixelWidth;
            var height = decoder.PixelHeight;
            if (width == 0 || height == 0)
            {
                throw new InvalidDataException("Soubor neobsahuje obrázek s platnými rozměry.");
            }

            // Only one full-size picture is kept; exceptionally large files are decoded smaller.
            const double maxPixels = 50_000_000;
            const double maxEdge = 12_000;
            var reduction = Math.Min(1, Math.Min(Math.Sqrt(maxPixels / ((double)width * height)),
                maxEdge / Math.Max(width, height)));
            var bitmap = new BitmapImage();
            if (reduction < 1)
            {
                if (width >= height)
                {
                    bitmap.DecodePixelWidth = Math.Max(1, (int)(width * reduction));
                }
                else
                {
                    bitmap.DecodePixelHeight = Math.Max(1, (int)(height * reduction));
                }
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream).AsTask(token);
            token.ThrowIfCancellationRequested();

            PreviewImage.Source = bitmap;
            FitPreviewButton.IsEnabled = true;
            ActualSizeButton.IsEnabled = reduction == 1;
            PreviewStatus.Text = $"{decoder.OrientedPixelWidth:N0} × {decoder.OrientedPixelHeight:N0} px" +
                (reduction < 1 ? " · Velký snímek je zobrazen ve sníženém rozlišení" : "");
            PreviewStatus.Tag = PreviewStatus.Text;
            UpdatePreviewSize();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                PreviewStatus.Text = $"Fotografii nelze zobrazit: {exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_previewLoad, request))
            {
                _previewLoad = null;
            }
        }
    }

    private void UpdatePreviewSize()
    {
        if (PreviewImage.Source is not BitmapImage bitmap || PhotoScroll.ActualWidth <= 2 || PhotoScroll.ActualHeight <= 2)
        {
            return;
        }
        var dpi = XamlRoot?.RasterizationScale ?? 1;
        var width = bitmap.PixelWidth / dpi;
        var height = bitmap.PixelHeight / dpi;
        if (width <= 0 || height <= 0)
        {
            return;
        }
        var scale = _fitPreview
            ? Math.Min(1, Math.Min((PhotoScroll.ActualWidth - 2) / width, (PhotoScroll.ActualHeight - 2) / height))
            : 1;
        PhotoScroll.HorizontalScrollBarVisibility = _fitPreview ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        PhotoScroll.VerticalScrollBarVisibility = _fitPreview ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        PreviewImage.Width = width * scale;
        PreviewImage.Height = height * scale;
        if (_fitPreview)
        {
            PhotoScroll.ChangeView(0, 0, null, disableAnimation: true);
        }
        var description = PreviewStatus.Tag as string;
        PreviewStatus.Text = ActualSizeButton.IsEnabled ? $"{description} · {scale:P0}" : description ?? "";
    }

    private void OnFitPreviewClick(object sender, RoutedEventArgs e)
    {
        _fitPreview = true;
        UpdatePreviewSize();
    }

    private void OnActualSizeClick(object sender, RoutedEventArgs e)
    {
        _fitPreview = false;
        UpdatePreviewSize();
    }

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewSize();

    private void OnClosePreviewClick(object sender, RoutedEventArgs e) => ClosePhotoViewer();

    private GalleryFile[] SelectedPhotos() =>
        GalleryGrid.SelectedItems.OfType<GalleryFile>().Where(file => file.Kind == MediaKind.Photo).ToArray();

    private async void OnSelectionPhotoConvert(object sender, RoutedEventArgs e) =>
        await ConfigurePhotoConversionAsync(SelectedPhotos(), fromViewer: false);

    private void OnSelectionPhotoCancel(object sender, RoutedEventArgs e) => _photoConversion?.Cancel();

    private async void OnPhotoConvertClick(object sender, RoutedEventArgs e)
    {
        if (_previewFile is { } file) await ConfigurePhotoConversionAsync([file], fromViewer: true);
    }

    private async Task ConfigurePhotoConversionAsync(GalleryFile[] files, bool fromViewer)
    {
        if (files.Length == 0 || _photoConversion is not null || !ViewModel.CanChangeFilters) return;
        PhotoOutputFormat[] formats = [PhotoOutputFormat.WebP, PhotoOutputFormat.WebPLossless, PhotoOutputFormat.Png, PhotoOutputFormat.Jpeg];
        var picker = new ComboBox { ItemsSource = new[] { "WebP · menší soubor", "WebP · bezeztrátový", "PNG · optimalizované", "JPEG" }, SelectedIndex = 0 };
        var quality = new Slider { Header = "Kvalita", Minimum = 1, Maximum = 100, StepFrequency = 1, Value = 82 };
        picker.SelectionChanged += (_, _) => quality.Visibility = ShowWhen(picker.SelectedIndex is 0 or 3);
        int?[] limits = [null, 8192, 4096, 2048];
        var size = new ComboBox { Header = "Maximální délka delší strany", ItemsSource = new[] { "Původní rozměry", "8192 px", "4096 px", "2048 px" }, SelectedIndex = 0 };
        var replace = new CheckBox { Content = "Nahradit původní kopii v galerii (bez koše)" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(picker);
        panel.Children.Add(quality);
        panel.Children.Add(size);
        panel.Children.Add(new TextBlock { Text = "PNG a WebP zachovají průhlednost, JPEG ji nahradí bílou. Údaje EXIF se nepřenesou. Před nahrazením uvidíte skutečnou velikost výsledku.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(replace);
        if (!await ShowConfirmedDialogAsync(new ContentDialog
        {
            Title = files.Length == 1 ? $"Převést {files[0].Name}?" : $"Převést vybrané fotografie ({files.Length})?",
            Content = panel, PrimaryButtonText = "Převést",
            CloseButtonText = "Storno", DefaultButton = ContentDialogButton.Close
        }) || !ViewModel.CanChangeFilters ||
            (fromViewer ? !ReferenceEquals(files[0], _previewFile) : !files.ToHashSet().SetEquals(SelectedPhotos()))) return;

        ClosePhotoViewer();
        var applyToAll = false;
        await RunPhotoTaskAsync(token => ViewModel.ConvertPhotosAsync(files,
                new PhotoConversionOptions(formats[picker.SelectedIndex], (int)quality.Value, limits[size.SelectedIndex]),
                replace.IsChecked == true, async (file, result) =>
                {
                    if (applyToAll) return true;
                    var difference = (double)(result.SourceBytes - result.OutputBytes) / result.SourceBytes;
                    var message = $"Původní soubor: {result.SourceBytes:N0} B\nVýsledek: {result.OutputBytes:N0} B · {result.Width} × {result.Height} px\n" +
                        (difference >= 0 ? $"Úspora: {difference:P1}." : $"Výsledek je o {-difference:P1} větší.") +
                        "\nNahradí se pouze kopie v galerii, bez uložení do koše.";
                    var content = new StackPanel { Spacing = 8 };
                    content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
                    var all = new CheckBox { Content = "Nahradit i všechny zbývající vybrané kopie (i větším souborem)" };
                    if (files.Length > 1) content.Children.Add(all);
                    var confirmed = await ShowConfirmedDialogAsync(new ContentDialog
                    {
                        Title = $"Nahradit {file.Name}?", Content = content, PrimaryButtonText = "Nahradit",
                        CloseButtonText = "Zastavit", DefaultButton = ContentDialogButton.Close
                    });
                    applyToAll = confirmed && all.IsChecked == true;
                    return confirmed;
                }, token, ReportPhotoProgress));
    }


    private void ReportPhotoProgress(string label, int file, int batch)
    {
        PhotoFileProgress.Value = file;
        PhotoBatchProgress.Value = batch;
        PhotoFilePercent.Text = $"Soubor: {file} %";
        PhotoBatchPercent.Text = $"Celá úloha: {batch} %";
        AutomationProperties.SetName(PhotoFileProgress, label);
    }

    private async Task RunPhotoTaskAsync(Func<CancellationToken, Task> run)
    {
        if (_photoConversion is not null || !ViewModel.CanChangeFilters) return;
        using var request = new CancellationTokenSource();
        _photoConversion = request;
        SelectionPhotoCancel.Visibility = PhotoProgressBanner.Visibility = Visibility.Visible;
        try
        {
            _photoConversionTask = run(request.Token);
            await _photoConversionTask;
        }
        finally
        {
            _photoConversion = null;
            SelectionPhotoCancel.Visibility = PhotoProgressBanner.Visibility = Visibility.Collapsed;
            _photoConversionTask = null;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ClosePhotoViewer();
        }
    }

    private void ClosePhotoViewer(bool restoreFocus = true)
    {
        if (PhotoViewer.Visibility != Visibility.Visible)
        {
            return;
        }
        var request = _previewLoad;
        _previewLoad = null;
        request?.Cancel();
        _previewFile = null;
        PreviewImage.Source = null;
        PreviewStatus.Tag = null;
        PhotoViewer.Visibility = Visibility.Collapsed;
        GalleryWorkspace.Visibility = Visibility.Visible;
        if (restoreFocus && _previewReturnFocus?.Focus(FocusState.Programmatic) != true)
        {
            (ViewModel.IsTrashOpen ? TrashGrid : GalleryGrid).Focus(FocusState.Programmatic);
        }
        _previewReturnFocus = null;
    }
}
