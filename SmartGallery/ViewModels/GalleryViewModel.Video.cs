using Microsoft.Extensions.Logging;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using SmartGallery.Infrastructure.Video;

namespace SmartGallery.ViewModels;

public sealed partial class GalleryViewModel
{
    internal async Task<string> ConvertVideoBatchAsync(IReadOnlyList<(GalleryFile File, GalleryVideoInfo Info)> videos,
        FfmpegInstallation tools, GalleryVideoService service, VideoConversionQuality quality, bool replaceOriginal,
        int skipped, IProgress<VideoProgress> progress,
        CancellationToken token)
    {
        if (!CanBrowse || IsBusy || _library is not { } library || library.Id != _displayedLibraryId)
        {
            throw new IOException("Knihovna není připravena na převod.");
        }
        IsBusy = true;
        try
        {
            var completed = 0;
            var failed = 0;
            string? firstError = null;
            var byDuration = videos.All(item => item.Info.Duration > TimeSpan.Zero);
            var totalWeight = byDuration ? videos.Sum(item => item.Info.Duration.TotalSeconds) : videos.Count;
            double finishedWeight = 0;
            for (var index = 0; index < videos.Count && !token.IsCancellationRequested; index++)
            {
                var (file, info) = videos[index];
                var weight = byDuration ? info.Duration.TotalSeconds : 1;
                var stepProgress = new ConversionProgress(value =>
                {
                    var percent = value.Percent ?? (info.Duration > TimeSpan.Zero ? 0d : (double?)null);
                    var batch = Math.Floor(Math.Clamp((finishedWeight + weight * (percent ?? 0) / 100) / totalWeight * 100, 0, 99));
                    var detail = $"Video {index + 1}/{videos.Count}: {file.Name} · {value.Message}";
                    if (percent.HasValue) detail += $" · {Math.Floor(percent.Value):0} %";
                    if (value.Processed is { } processed && (info.Duration > TimeSpan.Zero || !percent.HasValue))
                    {
                        detail += $" · zpracováno {MediaTime(processed)}";
                        detail += info.Duration > TimeSpan.Zero
                            ? $" z {MediaTime(info.Duration)} · zbývá {MediaTime(info.Duration - processed)}"
                            : " · zbývající délka neznámá";
                    }
                    progress.Report(new($"Celá úloha: {batch:0} % · zbývá {100 - batch:0} % · převedeno {completed}/{videos.Count}" +
                        $" · ve frontě {videos.Count - index - 1} · chyby {failed} · přeskočeno {skipped}", batch)
                        { FileDetail = detail, FilePercent = percent });
                });
                stepProgress.Report(new("Kontroluji vstup…") { Processed = TimeSpan.Zero });
                try
                {
                    await ConvertVideoFileAsync(library, file, tools, info, service, quality, replaceOriginal, stepProgress, token);
                    completed++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    failed++;
                    firstError ??= $"{file.Name}: {exception.Message}";
                    _logger.LogWarning(exception, "Převod videa {FileName} selhal.", file.Name);
                }
                finishedWeight += weight;
            }
            if (completed + failed == videos.Count)
                progress.Report(new($"Celá úloha: 100 % · zbývá 0 videí · převedeno {completed}/{videos.Count} · chyby {failed} · dokončuji…", 100));
            var refreshError = completed > 0 || (replaceOriginal && failed > 0) ? await RefreshAfterGalleryWorkAsync() : "";
            var remaining = videos.Count - completed - failed;
            var stopped = token.IsCancellationRequested && remaining > 0;
            return StatusMessage = (stopped ? "Převod zastaven. " : "") +
                $"Převedeno {completed}/{videos.Count} · přeskočeno {skipped} · chyby {failed}" +
                (stopped ? $" · nedokončeno {remaining}" : "") +
                (firstError is null ? "" : $" · {firstError}") + refreshError;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string MediaTime(TimeSpan time) => $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";

    private sealed class ConversionProgress(Action<VideoProgress> report) : IProgress<VideoProgress>
    {
        public void Report(VideoProgress value) => report(value);
    }

    private async Task ConvertVideoFileAsync(Library library, GalleryFile file, FfmpegInstallation tools,
        GalleryVideoInfo video, GalleryVideoService service, VideoConversionQuality quality, bool replaceOriginal,
        IProgress<VideoProgress> progress, CancellationToken token)
    {
        var path = await GetGalleryReadPathAsync(file, token);
        if (!string.Equals(path, video.Path, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Umístění zdrojového videa se změnilo.");
        }
        if (replaceOriginal && (file.SizeBytes != video.Size || file.LastWriteTimeUtc.UtcTicks != video.LastWriteTimeUtc.Ticks))
        {
            throw new IOException("Kopie se od zápisu do katalogu změnila; aplikace ji nenahradí.");
        }
        await service.ConvertAsync(tools, video, quality,
            (temporary, cancellationToken) => replaceOriginal
                ? _galleryCatalog.ReplaceConvertedVideoAsync(library, file, temporary, cancellationToken)
                : _galleryCatalog.AddConvertedVideoAsync(library, file, temporary, cancellationToken),
            progress, token);
    }
}
