using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartGallery.Infrastructure.Video;

namespace SmartGallery.ViewModels;

public sealed partial class ModelFooterViewModel
{
    private bool _videoToolsChecked;

    [ObservableProperty]
    public partial FfmpegInstallation? VideoTools { get; private set; }

    [ObservableProperty]
    public partial bool VideoSetupActive { get; private set; }

    [ObservableProperty]
    public partial string VideoSetupMessage { get; private set; }

    [ObservableProperty]
    public partial double VideoSetupProgress { get; private set; }

    [ObservableProperty]
    public partial bool VideoSetupIndeterminate { get; private set; }

    public bool CanDownloadVideoTools => FfmpegTools.IsSupported && !VideoSetupActive && VideoTools is null;

    public IAsyncRelayCommand CheckVideoToolsCommand { get; }

    public IAsyncRelayCommand DownloadVideoToolsCommand { get; }

    private async Task PrepareVideoToolsAsync(FfmpegTools tools, bool download, CancellationToken token)
    {
        VideoSetupMessage = download ? "Připravuji stažení FFmpegu…" : "Ověřuji místní FFmpeg…";
        VideoSetupProgress = 0;
        VideoSetupIndeterminate = true;
        VideoSetupActive = true;
        NotifyVideoSetupActions();
        var acceptProgress = true;
        try
        {
            var progress = new Progress<VideoProgress>(value =>
            {
                if (acceptProgress && !token.IsCancellationRequested)
                {
                    VideoSetupMessage = value.Message;
                    VideoSetupProgress = value.Percent ?? 0;
                    VideoSetupIndeterminate = !value.Percent.HasValue;
                }
            });
            VideoTools = download ? await tools.InstallAsync(progress, token) : await tools.LoadAsync(token);
            VideoSetupMessage = VideoTools is null ? "FFmpeg není nainstalován. Stažení čeká na potvrzení."
                : "FFmpeg, FFprobe a FFplay jsou připraveny.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            VideoSetupMessage = "Příprava FFmpegu byla zrušena.";
        }
        catch (Exception exception)
        {
            VideoSetupMessage = $"FFmpeg: {exception.Message}";
        }
        finally
        {
            acceptProgress = false;
            _videoToolsChecked = true;
            VideoSetupActive = false;
            NotifyVideoSetupActions();
        }
    }

    private void NotifyVideoSetupActions()
    {
        OnPropertyChanged(nameof(CanDownloadVideoTools));
        CheckVideoToolsCommand.NotifyCanExecuteChanged();
        DownloadVideoToolsCommand.NotifyCanExecuteChanged();
    }
}
