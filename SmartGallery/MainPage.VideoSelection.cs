using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using SmartGallery.Infrastructure.Video;

namespace SmartGallery;

public sealed partial class MainPage
{
    private GalleryFile[] _selectedVideos = [];
    private GalleryVideoInfo? _selectedVideoInfo;
    private CancellationTokenSource? _videoSelectionProbe;
    private Task? _videoSelectionProbeTask;
    private CancellationTokenSource? _videoSelectionCancellation;
    private Task? _videoSelectionOperation;
    private bool _updatingVideoPicker;

    private void SyncVideoSelection()
    {
        if (!_mediaUiActive)
        {
            return;
        }
        SelectionPhotoConvert.Visibility = ShowWhen(ViewModel.IsGalleryOpen && SelectedPhotos().Length > 0);
        GalleryFile[] videos = ViewModel.IsGalleryOpen
            ? GalleryGrid.SelectedItems.OfType<GalleryFile>().Where(file => file.Kind == MediaKind.Video).ToArray()
            : [];
        if (_selectedVideos.SequenceEqual(videos))
        {
            return;
        }
        var activeId = (VideoSelectionPicker.SelectedItem as GalleryFile)?.Id;
        _selectedVideos = videos;
        if (_videoSelectionOperation is null)
        {
            VideoSelectionResult.Visibility = Visibility.Collapsed;
        }
        _updatingVideoPicker = true;
        try
        {
            VideoSelectionPicker.ItemsSource = videos;
            var index = Array.FindIndex(videos, file => file.Id == activeId);
            VideoSelectionPicker.SelectedIndex = videos.Length == 0 ? -1 : Math.Max(index, 0);
            VideoSelectionPicker.Visibility = ShowWhen(videos.Length > 1);
            VideoSelectionTitle.Content = videos.Length == 1 ? videos[0].Name : $"Vybraná videa: {videos.Length}";
        }
        finally
        {
            _updatingVideoPicker = false;
        }
        if (_tileVideoFile is { } playing && !ReferenceEquals(VideoSelectionPicker.SelectedItem, playing))
        {
            StopTileVideo();
        }
        RefreshSelectedVideo();
    }

    private void OnVideoSelectionPicked(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingVideoPicker)
        {
            StopTileVideo();
            VideoSelectionResult.Visibility = Visibility.Collapsed;
            RefreshSelectedVideo();
        }
    }

    private void RefreshSelectedVideo()
    {
        _videoSelectionProbe?.Cancel();
        _selectedVideoInfo = null;
        UpdateSelectedVideoButtons();
        if (!_mediaUiActive || _videoSelectionOperation is not null || VideoSelectionPicker.SelectedItem is not GalleryFile file)
        {
            return;
        }
        if (FooterViewModel.VideoTools is not { } tools)
        {
            VideoSelectionStatus.Text = FooterViewModel.VideoSetupMessage;
            return;
        }
        VideoSelectionStatus.Text = "Analyzuji formát a kodeky videa…";
        _videoSelectionProbeTask = AnalyzeSelectedVideoAsync(file, tools);
    }

    private async Task AnalyzeSelectedVideoAsync(GalleryFile file, FfmpegInstallation tools)
    {
        using var request = new CancellationTokenSource();
        _videoSelectionProbe = request;
        try
        {
            await Task.Delay(180, request.Token);
            var info = await ProbeVideoAsync(file, tools, request.Token);
            request.Token.ThrowIfCancellationRequested();
            _selectedVideoInfo = info;
            VideoSelectionStatus.Text = DescribeVideo(info).Replace('\n', ' ');
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!request.IsCancellationRequested)
            {
                VideoSelectionStatus.Text = $"Video nelze analyzovat: {exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_videoSelectionProbe, request))
            {
                _videoSelectionProbe = null;
                _videoSelectionProbeTask = null;
                UpdateSelectedVideoButtons();
            }
        }
    }

    private void UpdateSelectedVideoButtons()
    {
        var busy = _videoSelectionCancellation is not null;
        var available = _mediaUiActive && ViewModel.CanBrowse && !ViewModel.IsBusy && !busy;
        VideoSelectionBar.Visibility = ShowWhen(_mediaUiActive && ViewModel.IsGalleryOpen &&
            (_selectedVideos.Length > 0 || busy || VideoSelectionResult.Visibility == Visibility.Visible));
        VideoSelectionPicker.IsEnabled = !busy;
        VideoSelectionTitle.IsEnabled = available && _selectedVideos.Length == 1;
        SelectionVideoInstall.Visibility = ShowWhen(available && _selectedVideos.Length > 0 && FooterViewModel.CanDownloadVideoTools);
        SelectionVideoPlay.Visibility = ShowWhen(available && _selectedVideoInfo is { CanPlay: true });
        SelectionVideoWindow.Visibility = ShowWhen(available && _selectedVideoInfo is { CanPlayExternal: true });
        SelectionVideoWindow.Content = _selectedVideoInfo is { CanPlay: true } ? "Zvětšit" : "Otevřít detail";
        SelectionVideoPause.Visibility = SelectionVideoStop.Visibility = ShowWhen(_tileVideo is not null);
        SelectionVideoPlay.IsEnabled = _tileVideo is null;
        SelectionVideoConvert.Visibility = ShowWhen(available && _selectedVideoInfo is { ShouldOfferConversion: true });
        SelectionVideoBatch.Visibility = ShowWhen(available && _selectedVideos.Length > 1 && FooterViewModel.VideoTools is not null);
        SelectionVideoCancel.Visibility = ShowWhen(busy);
    }

    private static Visibility ShowWhen(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;

    private async void OnSelectionVideoPlay(object sender, RoutedEventArgs e)
    {
        if (VideoSelectionPicker.SelectedItem is GalleryFile file && _selectedVideoInfo is { CanPlay: true } info)
        {
            if (_showFileDetails)
            {
                SetDetailsMode(false, file);
            }
            GalleryGrid.ScrollIntoView(file);
            await RunSelectedVideoOperationAsync("Otevírám video…", async (_, token) =>
            {
                var media = await OpenAnalyzedVideoAsync(file, info, token);
                token.ThrowIfCancellationRequested();
                GalleryGrid.UpdateLayout();
                try { StartTileVideo(file, media); }
                catch { StopTileVideo(); throw; }
                return "Přehrávání v miniatuře.";
            });
        }
    }

    private async void OnSelectionVideoWindow(object sender, RoutedEventArgs e)
    {
        if (VideoSelectionPicker.SelectedItem is GalleryFile file && _selectedVideoInfo is { CanPlayExternal: true } info)
        {
            await ShowVideoAsync(file, SelectionVideoWindow, info);
            if (info.CanPlay && ReferenceEquals(_videoFile, file))
            {
                await PlayVideoAsync();
            }
        }
    }

    private void OnSelectionVideoPause(object sender, RoutedEventArgs e)
    {
        try { _tileVideo?.TogglePause(); }
        catch (Exception exception)
        {
            StopTileVideo();
            VideoSelectionResult.Text = exception.Message;
            VideoSelectionResult.Visibility = Visibility.Visible;
        }
    }

    private void OnSelectionVideoStop(object sender, RoutedEventArgs e)
    {
        StopTileVideo();
        VideoSelectionResult.Visibility = Visibility.Collapsed;
    }

    private async void OnSelectionVideoConvert(object sender, RoutedEventArgs e)
    {
        if (VideoSelectionPicker.SelectedItem is GalleryFile file)
        {
            await RunVideoConversionAsync([file]);
        }
    }

    private async void OnSelectionVideoBatch(object sender, RoutedEventArgs e) =>
        await RunVideoConversionAsync(_selectedVideos);

    private Task RunVideoConversionAsync(GalleryFile[] requested)
    {
        if (requested.Length == 0)
        {
            return Task.CompletedTask;
        }
        if (FooterViewModel.VideoTools is not { } tools)
        {
            ShowRequiredFiles();
            return Task.CompletedTask;
        }
        StopTileVideo();
        return RunSelectedVideoOperationAsync(requested.Length == 1 ? "Analyzuji video…" : "Analyzuji vybraná videa…", async (progress, token) =>
        {
            var candidates = new List<(GalleryFile File, GalleryVideoInfo Info)>();
            var readErrors = 0;
            string? firstError = null;
            for (var index = 0; index < requested.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var file = requested[index];
                progress.Report(new($"Analýza {index + 1}/{requested.Length} · {file.Name}", 100d * index / requested.Length));
                GalleryVideoInfo info;
                try
                {
                    info = await ProbeVideoAsync(file, tools, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    readErrors++;
                    firstError ??= $"{file.Name}: {exception.Message}";
                    continue;
                }
                candidates.Add((file, info));
            }
            token.ThrowIfCancellationRequested();
            var summary = $"Videa: {requested.Length} · načtená: {candidates.Count} · nečitelná: {readErrors}.";
            var scopeCount = requested.Length;
            if (candidates.Count == 0)
            {
                return $"{summary} {firstError}";
            }
            progress.Report(new("Zjišťuji dostupné formáty a kodeky FFmpeg…"));
            var modes = await _videoService.GetConversionModesAsync(tools, token);
            (VideoConversionQuality Quality, string Label)[] presets =
            [
                (VideoConversionQuality.Balanced, "MP4 · H.264 · vyvážená kvalita"),
                (VideoConversionQuality.High, "MP4 · H.264 · vysoká kvalita"),
                (VideoConversionQuality.Small, "MP4 · H.264 · vyšší komprese"),
                (VideoConversionQuality.CopyOnly, "MP4 · kopie kompatibilních stop"),
                (VideoConversionQuality.QuickTime, "MOV · H.264 + AAC"),
                (VideoConversionQuality.WebM, "WebM · VP9 + Opus"),
                (VideoConversionQuality.MatroskaCopy, "MKV · kopie stop"),
                (VideoConversionQuality.ArchiveLossless, "MKV · bezeztrátový archiv (FFV1)")
            ];
            var options = presets.Where(preset => modes.Contains(preset.Quality))
                .Select(preset => (preset.Quality, preset.Label, Count: candidates.Count(item => item.Info.CanConvertWith(preset.Quality))))
                .Where(option => option.Count > 0).ToArray();
            if (options.Length == 0)
            {
                return $"{summary} Místní FFmpeg nemá pro vybraná videa vhodný režim převodu.";
            }
            var picker = new ComboBox
            {
                ItemsSource = options.Select(option => $"{option.Label} ({option.Count})").ToArray(),
                SelectedIndex = -1,
                PlaceholderText = "Zvolte režim převodu",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var details = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var replace = new CheckBox { Content = "Nahradit původní kopie v galerii" };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = $"{summary}\nRozsah převodu: {scopeCount} videí.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(picker);
            panel.Children.Add(replace);
            panel.Children.Add(details);
            var dialog = new ContentDialog
            {
                Title = "Formát a kvalita převodu",
                Content = panel,
                PrimaryButtonText = "Převést",
                CloseButtonText = "Storno",
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false
            };
            picker.SelectionChanged += (_, _) => UpdateConversionDetails();
            replace.Checked += (_, _) => UpdateConversionDetails();
            replace.Unchecked += (_, _) => UpdateConversionDetails();

            void UpdateConversionDetails()
            {
                if (picker.SelectedIndex < 0)
                {
                    dialog.IsPrimaryButtonEnabled = false;
                    return;
                }
                var (option, _, count) = options[picker.SelectedIndex];
                var extraTracks = candidates.Count(item => item.Info.CanConvertWith(option) && item.Info.HasOtherStreams);
                var replaceOriginal = replace.IsChecked == true;
                dialog.IsPrimaryButtonEnabled = count > 0;
                dialog.PrimaryButtonText = replaceOriginal ? "Převést a nahradit" : "Převést";
                details.Text = $"Režim lze použít u {count} videí, přeskočí se {scopeCount - count}.\n" +
                    (replaceOriginal
                        ? "Po ověření výstup nahradí kopii v galerii bez uložení do koše. Základ názvu zůstane, přípona se změní jen při změně formátu. Zdrojové originály se nezmění.\n"
                        : "Původní soubory se nezmění.\n") +
                    (extraTracks > 0 ? $"U {extraTracks} videí se nepřenesou titulky, pomocné obrazové nebo datové stopy. " : "") +
                    (option switch
                    {
                         VideoConversionQuality.CopyOnly => "Obraz i zvuk se kopírují bez překódování.",
                         VideoConversionQuality.MatroskaCopy => "Obraz i zvuk se kopírují bez překódování. Některé kodeky nemusejí být s MKV slučitelné.",
                         VideoConversionQuality.ArchiveLossless => "Hlavní obraz se kóduje beze ztráty, zvuk se kopíruje. Soubor může být výrazně větší, je určen pro FFplay. Některé vstupy nemusí být s MKV slučitelné.",
                         VideoConversionQuality.WebM => "Obraz i zvuk se překódují. Tento režim nepřevádí HDR.",
                         VideoConversionQuality.Balanced => "Obraz SDR se překóduje do H.264. Kompatibilní HDR obraz i kompatibilní zvuk se kopírují.",
                         _ => "Obraz SDR se překóduje do H.264, nekompatibilní zvuk do AAC. Tento režim nepřevádí HDR."
                    });
            }
            if (!await ShowConfirmedDialogAsync(dialog) || picker.SelectedIndex < 0)
            {
                return $"Převod nebyl spuštěn. {summary}";
            }
            token.ThrowIfCancellationRequested();
            var quality = options[picker.SelectedIndex].Quality;
            var convertible = candidates.Where(item => item.Info.CanConvertWith(quality)).ToArray();
            return await ViewModel.ConvertVideoBatchAsync(convertible, tools, _videoService,
                quality, replace.IsChecked == true, scopeCount - convertible.Length, progress, token);
        });
    }

    private Task RunSelectedVideoOperationAsync(string message, Func<IProgress<VideoProgress>, CancellationToken, Task<string>> action)
    {
        if (!_mediaUiActive || ViewModel.IsBusy || !ViewModel.CanBrowse || _videoSelectionOperation is not null)
        {
            return Task.CompletedTask;
        }
        _videoSelectionProbe?.Cancel();
        var request = new CancellationTokenSource();
        _videoSelectionCancellation = request;
        if (_selectedVideos.Length == 0)
        {
            VideoSelectionTitle.Content = "Převod videa";
        }
        UpdateSelectedVideoButtons();
        VideoSelectionResult.Visibility = Visibility.Collapsed;
        VideoSelectionStatus.Text = message;
        SelectionVideoProgress.Visibility = Visibility.Visible;
        SelectionVideoProgress.IsIndeterminate = true;
        var progress = new Progress<VideoProgress>(value =>
        {
            if (ReferenceEquals(_videoSelectionCancellation, request) && !request.IsCancellationRequested && _mediaUiActive)
            {
                VideoSelectionStatus.Text = value.Message;
                SelectionVideoProgress.IsIndeterminate = !value.Percent.HasValue;
                SelectionVideoProgress.Value = value.Percent ?? 0;
                ConversionFilePanel.Visibility = ShowWhen(value.FileDetail is not null);
                if (value.FileDetail is { } detail)
                {
                    ConversionFileDetail.Text = detail;
                    ConversionFileProgress.IsIndeterminate = !value.FilePercent.HasValue;
                    ConversionFileProgress.Value = value.FilePercent ?? 0;
                }
            }
        });
        return _videoSelectionOperation = RunAsync();

        async Task RunAsync()
        {
            using (request)
            {
                await Task.Yield();
                string result;
                var activeFile = VideoSelectionPicker.SelectedItem;
                try
                {
                    request.Token.ThrowIfCancellationRequested();
                    result = await action(progress, request.Token);
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested)
                {
                    result = "Operace byla zastavena.";
                }
                catch (Exception exception)
                {
                    result = exception.Message;
                }
                _videoSelectionCancellation = null;
                _videoSelectionOperation = null;
                SelectionVideoProgress.Visibility = Visibility.Collapsed;
                ConversionFilePanel.Visibility = Visibility.Collapsed;
                if (_mediaUiActive && _selectedVideos.Length > 0)
                {
                    if (_selectedVideoInfo is null || !ReferenceEquals(activeFile, VideoSelectionPicker.SelectedItem))
                    {
                        RefreshSelectedVideo();
                    }
                    else
                    {
                        VideoSelectionStatus.Text = DescribeVideo(_selectedVideoInfo).Replace('\n', ' ');
                    }
                    VideoSelectionResult.Text = result;
                    VideoSelectionResult.Visibility = Visibility.Visible;
                }
                else if (_mediaUiActive)
                {
                    VideoSelectionTitle.Content = "Výsledek práce s videem";
                    VideoSelectionStatus.Text = "";
                    VideoSelectionResult.Text = result;
                    VideoSelectionResult.Visibility = Visibility.Visible;
                }
                UpdateSelectedVideoButtons();
            }
        }
    }

    private void OnSelectionVideoCancel(object sender, RoutedEventArgs e) => _videoSelectionCancellation?.Cancel();
}
