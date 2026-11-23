using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using SmartGallery.Infrastructure.Video;
using SmartGallery.Views;
using Windows.Storage;
using Windows.System;

namespace SmartGallery;

public sealed partial class MainPage
{
    private readonly GalleryVideoService _videoService;
    private GalleryFile? _videoFile;
    private FfmpegInstallation? _videoTools;
    private GalleryVideoInfo? _videoInfo;
    private Control? _videoReturnFocus;
    private VideoPlayback? _inlineVideo;
    private VideoPlayback? _tileVideo;
    private ContentControl? _tileVideoHost;
    private Image? _tileVideoImage;
    private GalleryFile? _tileVideoFile;
    private CancellationTokenSource? _videoCancellation;
    private Task? _videoOperation;

    private Task ShowVideoAsync(GalleryFile file, Control? returnFocus, GalleryVideoInfo? selectedInfo = null)
    {
        if (_videoFile is not null || _videoOperation is not null || _galleryConfirmation is not null ||
            _videoSelectionOperation is not null || PhotoViewer.Visibility == Visibility.Visible)
        {
            return Task.CompletedTask;
        }
        EndMarquee();
        StopTileVideo();
        _videoFile = file;
        _ = LoadFileVisualAsync(VideoPoster, async (media, token) =>
        {
            try { await LoadThumbnailAsync(VideoPoster, media, token, requestedSize: 1280); }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine($"Miniatura videa {file.Name}: {error.Message}");
            }
            token.ThrowIfCancellationRequested();
            if (VideoPoster.Source is not null) return;
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(media).AsTask(token);
            var composition = new Windows.Media.Editing.MediaComposition();
            composition.Clips.Add(clip);
            using var frame = await composition.GetThumbnailAsync(
                TimeSpan.FromTicks(Math.Min(TimeSpan.TicksPerSecond, clip.OriginalDuration.Ticks / 2)),
                1280, 0, Windows.Media.Editing.VideoFramePrecision.NearestFrame).AsTask(token);
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bitmap.SetSourceAsync(frame).AsTask(token);
            token.ThrowIfCancellationRequested();
            VideoPoster.Source = bitmap;
        }, file);
        _videoReturnFocus = returnFocus;
        VideoName.Text = file.Name;
        GalleryWorkspace.Visibility = Visibility.Collapsed;
        VideoViewer.Visibility = Visibility.Visible;
        CloseVideoButton.Focus(FocusState.Programmatic);
        if (selectedInfo is not null && FooterViewModel.VideoTools is { } tools)
        {
            _videoTools = tools;
            _videoInfo = selectedInfo;
            VideoStatus.Text = DescribeVideo(selectedInfo);
            UpdateVideoButtons();
            return Task.CompletedTask;
        }
        return LoadVideoDetailAsync(file);
    }

    private Task LoadVideoDetailAsync(GalleryFile file)
    {
        _videoTools = FooterViewModel.VideoTools;
        if (_videoTools is not { } tools)
        {
            VideoStatus.Text = FooterViewModel.VideoSetupMessage;
            UpdateVideoButtons();
            UpdateVideoDetailSetupProgress();
            return Task.CompletedTask;
        }
        return RunVideoOperationAsync("Analyzuji formát a kodeky videa…", async (_, token) =>
        {
            var info = await ProbeVideoAsync(file, tools, token);
            token.ThrowIfCancellationRequested();
            _videoInfo = info;
            VideoStatus.Text = DescribeVideo(info);
        });
    }

    private async Task<GalleryVideoInfo> ProbeVideoAsync(GalleryFile file, FfmpegInstallation tools, CancellationToken token)
    {
        var path = await ViewModel.GetGalleryReadPathAsync(file, token);
        return await _videoService.ProbeAsync(tools, path, token);
    }

    private static string DescribeVideo(GalleryVideoInfo info)
    {
        var audio = string.Join(", ", info.Audio.Select(track => track.Codec).Distinct());
        var description = $"{info.Format} · {info.Video.Codec} · {(audio.Length == 0 ? "bez zvuku" : audio)}" +
            (info.Duration > TimeSpan.Zero ? $" · {info.Duration:g}" : "");
        return description + (info.CanPlay ? "" : info.CanPlayExternal
            ? "\nVideo lze přehrát ve FFplay bez převodu."
            : info.CanConvertWith(VideoConversionQuality.Balanced)
            ? "\nPro přehrávání doporučujeme převod do kompatibilního MP4."
            : "\nTento HDR formát zatím nelze bezpečně převést pro běžné přehrávání.");
    }

    private async void OnVideoConvertClick(object sender, RoutedEventArgs e)
    {
        if (_videoFile is not { } file || _videoTools is null || _videoInfo is null || _videoOperation is not null)
        {
            return;
        }
        CloseVideoViewer();
        await RunVideoConversionAsync([file]);
    }

    private async void OnVideoTrashClick(object sender, RoutedEventArgs e)
    {
        if (_videoFile is { } file)
        {
            await ConfirmDeleteSelectionAsync(detailFile: file);
        }
    }

    private async void OnVideoDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_videoFile is { } file)
        {
            await ConfirmDeleteSelectionAsync(permanent: true, detailFile: file);
        }
    }

    private async void OnVideoPlayClick(object sender, RoutedEventArgs e) => await PlayVideoAsync();

    private Task PlayVideoAsync()
    {
        if (_videoFile is not { } file || _videoInfo is not { CanPlay: true } info)
        {
            return Task.CompletedTask;
        }
        return RunVideoOperationAsync("Otevírám video…", async (_, token) =>
        {
            var media = await OpenAnalyzedVideoAsync(file, info, token);
            token.ThrowIfCancellationRequested();
            StopInlineVideo();
            try
            {
                _inlineVideo = new VideoPlayback(media, message =>
                {
                    StopInlineVideo();
                    VideoStatus.Text = $"{message} Zkuste přehrávání ve FFplay bez převodu.";
                });
                VideoPlayerHost.Content = _inlineVideo.Element;
                _inlineVideo.Play();
            }
            catch
            {
                StopInlineVideo();
                throw;
            }
            VideoStatus.Text = "Přehrávání v aplikaci";
        }, showProgress: false);
    }

    private async Task<StorageFile> OpenAnalyzedVideoAsync(GalleryFile file, GalleryVideoInfo info, CancellationToken token)
    {
        var path = await ViewModel.GetGalleryReadPathAsync(file, token);
        await Task.Run(() =>
        {
            var snapshot = new FileInfo(path);
            if (!string.Equals(info.Path, path, StringComparison.OrdinalIgnoreCase) ||
                snapshot.Length != info.Size || snapshot.LastWriteTimeUtc != info.LastWriteTimeUtc)
            {
                throw new IOException("Video se od analýzy změnilo. Vyberte ho znovu.");
            }
        }, token);
        return await StorageFile.GetFileFromPathAsync(path).AsTask(token);
    }

    private void StartTileVideo(GalleryFile file, StorageFile media)
    {
        if (!ReferenceEquals(VideoSelectionPicker.SelectedItem, file) ||
            GalleryGrid.ContainerFromItem(file) is not SelectorItem container ||
            !ReferenceEquals(container.Content, file) || GetFileVisual(container) is not Image image ||
            image.Parent is not FrameworkElement root || root.FindName("TileVideoHost") is not ContentControl host)
        {
            throw new IOException("Miniatura není zobrazená. Přejděte na video nebo zvolte Zvětšit.");
        }
        StopTileVideo();
        StartFileVisual(image, file);
        _tileVideo = new VideoPlayback(media, message =>
        {
            StopTileVideo();
            VideoSelectionResult.Text = $"{message} Ve zvětšeném detailu lze zkusit FFplay bez převodu.";
            VideoSelectionResult.Visibility = Visibility.Visible;
        }, transportControls: false);
        _tileVideoHost = host;
        _tileVideoImage = image;
        _tileVideoFile = file;
        host.Content = _tileVideo.Element;
        host.Visibility = Visibility.Visible;
        _tileVideo.Play();
    }

    private void StopTileVideo()
    {
        if (_tileVideoHost is not { } host)
        {
            return;
        }
        var playback = _tileVideo;
        _tileVideoHost = null;
        _tileVideoImage = null;
        _tileVideoFile = null;
        _tileVideo = null;
        host.Content = null;
        host.Visibility = Visibility.Collapsed;
        ReleaseVideoPlayback(playback);
        UpdateSelectedVideoButtons();
    }

    private async void OnVideoExternalClick(object sender, RoutedEventArgs e)
    {
        if (_videoInfo is not { CanPlayExternal: true } info || _videoTools is not { } tools)
        {
            return;
        }
        await RunVideoOperationAsync("Přehrávám v externím FFplay…", async (_, token) =>
        {
            StopInlineVideo();
            await _videoService.PlayExternalAsync(tools, info, token);
            if (!token.IsCancellationRequested)
            {
                VideoStatus.Text = "Externí přehrávání skončilo.";
            }
        }, showProgress: false);
    }

    private Task RunVideoOperationAsync(string message, Func<IProgress<VideoProgress>, CancellationToken, Task> action,
        bool showProgress = true)
    {
        if (_videoOperation is not null)
        {
            return Task.CompletedTask;
        }
        var request = new CancellationTokenSource();
        _videoCancellation = request;
        UpdateVideoButtons();
        VideoStatus.Text = message;
        VideoProgressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        VideoProgressBar.IsIndeterminate = true;
        var progress = new Progress<VideoProgress>(update =>
        {
            if (ReferenceEquals(_videoCancellation, request) && !request.IsCancellationRequested && _videoFile is not null)
            {
                VideoStatus.Text = update.Message;
                VideoProgressBar.IsIndeterminate = !update.Percent.HasValue;
                VideoProgressBar.Value = update.Percent ?? 0;
            }
        });
        return _videoOperation = RunAsync();

        async Task RunAsync()
        {
            using (request)
            {
                // Assign the pending task before the action can finish synchronously.
                await Task.Yield();
                try
                {
                    request.Token.ThrowIfCancellationRequested();
                    await action(progress, request.Token);
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested)
                {
                    if (_videoFile is not null)
                    {
                        VideoStatus.Text = "Operace byla zrušena.";
                    }
                }
                catch (Exception exception)
                {
                    if (_videoFile is not null)
                    {
                        VideoStatus.Text = exception.Message;
                    }
                }
                finally
                {
                    _videoCancellation = null;
                    _videoOperation = null;
                    VideoProgressBar.Visibility = Visibility.Collapsed;
                    UpdateVideoButtons();
                }
            }
        }
    }

    private void UpdateVideoButtons()
    {
        var busy = _videoCancellation is not null;
        var play = !busy && _videoInfo is { CanPlay: true };
        NextVideoButton.IsEnabled = !busy && FindNextVideo() is not null;
        VideoTrashButton.IsEnabled = VideoDeleteButton.IsEnabled = !busy && _videoFile is not null && ViewModel.CanChangeFilters;
        VideoInstallButton.Visibility = !busy && FooterViewModel.CanDownloadVideoTools ? Visibility.Visible : Visibility.Collapsed;
        VideoPlayButton.Visibility = ShowWhen(play);
        VideoExternalButton.Visibility = ShowWhen(!busy && _videoInfo is { CanPlayExternal: true });
        VideoConvertButton.Visibility = ShowWhen(!busy && _videoInfo is { ShouldOfferConversion: true });
        VideoCancelButton.Visibility = ShowWhen(busy || (_videoTools is null && FooterViewModel.VideoSetupActive));
    }

    private void UpdateVideoDetailSetupProgress()
    {
        if (_videoFile is null || _videoOperation is not null || _videoTools is not null)
        {
            return;
        }
        VideoProgressBar.Visibility = ShowWhen(FooterViewModel.VideoSetupActive);
        VideoProgressBar.IsIndeterminate = FooterViewModel.VideoSetupIndeterminate;
        VideoProgressBar.Value = FooterViewModel.VideoSetupProgress;
    }

    private void OnVideoCancelClick(object sender, RoutedEventArgs e)
    {
        if (_videoCancellation is { } request)
        {
            request.Cancel();
        }
        else
        {
            OnCancelVideoToolsClick(sender, e);
        }
    }

    private void OnCloseVideoClick(object sender, RoutedEventArgs e) => CloseVideoViewer();

    private GalleryFile? FindNextVideo()
    {
        if (_videoFile is null) return null;
        var files = ViewModel.VisibleFiles;
        var found = false;
        foreach (var file in files)
        {
            if (found && file.Kind == MediaKind.Video) return file;
            if (file.Id == _videoFile.Id) found = true;
        }
        return null;
    }

    private async void OnNextVideoClick(object sender, RoutedEventArgs e)
    {
        if (_galleryConfirmation is not null || _videoOperation is not null || FindNextVideo() is not { } next) return;
        var focus = _videoReturnFocus;
        CloseVideoViewer(restoreFocus: false);
        await ShowVideoAsync(next, focus);
    }

    private void OnVideoViewerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseVideoViewer();
        }
    }

    private void CloseVideoViewer(bool restoreFocus = true)
    {
        if (_videoFile is null)
        {
            return;
        }
        _videoCancellation?.Cancel();
        if (_mediaRequests.Remove(VideoPoster, out var posterRequest)) posterRequest.Cancel();
        VideoPoster.Source = null;
        StopInlineVideo();
        _videoFile = null;
        _videoInfo = null;
        _videoTools = null;
        VideoViewer.Visibility = Visibility.Collapsed;
        GalleryWorkspace.Visibility = Visibility.Visible;
        if (restoreFocus && _videoReturnFocus?.Focus(FocusState.Programmatic) != true)
        {
            GalleryGrid.Focus(FocusState.Programmatic);
        }
        _videoReturnFocus = null;
    }

    private void StopInlineVideo()
    {
        var playback = _inlineVideo;
        _inlineVideo = null;
        VideoPlayerHost.Content = null;
        ReleaseVideoPlayback(playback);
    }

    private static void ReleaseVideoPlayback(VideoPlayback? playback)
    {
        try { playback?.Dispose(); }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("Ukončení přehrávání selhalo: {0}", exception.Message);
        }
    }
}
