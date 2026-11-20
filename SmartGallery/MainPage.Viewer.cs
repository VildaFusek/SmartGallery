using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;

namespace SmartGallery;

public sealed partial class MainPage
{
    private CancellationTokenSource? _previewLoad;
    private Control? _previewReturnFocus;
    private bool _showFileDetails;
    private bool _fitPreview;

    private void OnDetailsClick(object sender, RoutedEventArgs e) =>
        SetDetailsMode(DetailsToggle.IsChecked == true, ViewModel.SelectedFile);

    private void SetDetailsMode(bool show, GalleryFile? anchor)
    {
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

    private void OnGalleryHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdateGalleryItemSize();

    private void UpdateGalleryItemSize()
    {
        if (GalleryGrid?.ItemsPanelRoot is ItemsWrapGrid panel)
        {
            panel.MaximumRowsOrColumns = _showFileDetails ? 1 : -1;
            panel.ItemWidth = _showFileDetails ? Math.Max(1, GalleryHost.ActualWidth - 24) : double.NaN;
        }
    }

    private void StartDetailsVisual(FrameworkElement visual, GalleryFile file)
    {
        if (visual.FindName("ExtensionBadge") is TextBlock badge)
        {
            var extension = Path.GetExtension(file.Name);
            badge.Text = extension.Length > 0 ? extension[1..].ToUpperInvariant() : "—";
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
        ShowPhotoAsync(file.Name, returnFocus, token => ViewModel.GetGalleryReadPathAsync(file, token));

    private async Task ShowPhotoAsync(string name, Control? returnFocus, Func<CancellationToken, Task<string>> getPath)
    {
        if (PhotoViewer.Visibility == Visibility.Visible || _galleryConfirmation is not null)
        {
            return;
        }

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
