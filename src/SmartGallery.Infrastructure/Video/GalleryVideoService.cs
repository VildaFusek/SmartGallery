using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace SmartGallery.Infrastructure.Video;

public sealed record VideoTrack(int Index, string Codec, bool CanCopy, int Channels = 0)
{
    public int OutputChannels => Channels is 1 or 6 ? Channels : 2;
}

public enum VideoConversionQuality { CopyOnly, ArchiveLossless, High, Balanced, Small, MatroskaCopy, WebM, QuickTime }

public sealed record GalleryVideoInfo(string Path, long Size, DateTime LastWriteTimeUtc, string Format, bool IsMp4,
    VideoTrack Video, IReadOnlyList<VideoTrack> Audio, bool HasOtherStreams, bool HasAlternateVideo, bool IsHdr,
    string PixelFormat, TimeSpan Duration)
{
    private bool IsQuickTimeFamily => IsMp4 ||
        (System.IO.Path.GetExtension(Path).ToLowerInvariant() is (".mov" or ".m4v") &&
            (Format is "qt" or "mov,mp4,m4a,3gp,3g2,mj2" || IsMp4Brand(Format)));
    private bool IsMatroskaFamily => Format.Split(',').Contains("matroska");
    public bool CanPlay => IsQuickTimeFamily && !HasAlternateVideo && Video.CanCopy && Audio.All(track => track.CanCopy);
    public bool CanPlayExternal => CanPlay ||
        (IsQuickTimeFamily && Video.Codec is ("h264" or "hevc" or "av1") &&
            Audio.All(track => track.Codec is ("aac" or "mp3" or "ac3" or "eac3" or "alac") ||
                track.Codec.StartsWith("pcm_", StringComparison.Ordinal))) ||
        (IsMatroskaFamily && (Video.Codec == "ffv1" ||
            (Video.Codec is ("h264" or "hevc" or "vp8" or "vp9" or "av1") &&
                Audio.All(track => track.Codec is ("aac" or "mp3" or "ac3" or "eac3" or "opus" or "vorbis" or "flac")))));
    public bool CanConvert => Video.CanCopy || !IsHdr;
    public bool CopiesAllAvTracks => Video.CanCopy && Audio.All(track => track.CanCopy);
    public bool CanArchiveLosslessly => !IsHdr && PixelFormat is ("yuv420p" or "yuv422p" or "yuv444p" or
        "yuv420p10le" or "yuv422p10le" or "yuv444p10le" or "gray" or "bgr0");

    public bool ShouldOfferConversion => CanConvertWith(VideoConversionQuality.MatroskaCopy) || CanConvert;

    internal static bool IsMp4Brand(string brand) => brand is ("mp41" or "mp42" or "avc1" or "hvc1" or "hev1" or "M4V") ||
        brand.StartsWith("iso", StringComparison.Ordinal);

    public bool CanConvertWith(VideoConversionQuality quality) => quality switch
    {
        VideoConversionQuality.ArchiveLossless => CanArchiveLosslessly && Video.Codec != "ffv1",
        VideoConversionQuality.CopyOnly => CopiesAllAvTracks,
        VideoConversionQuality.MatroskaCopy => Video.Codec.Length > 0 && Audio.All(track => track.Codec.Length > 0),
        VideoConversionQuality.Balanced => CanConvert,
        VideoConversionQuality.High or VideoConversionQuality.Small or VideoConversionQuality.WebM or
            VideoConversionQuality.QuickTime => !IsHdr,
        _ => false
    };
}

public sealed class GalleryVideoService
{
    private const string LocalFormats = "mov,avi,matroska,webm,mpeg,mpegvideo,mpegts,asf,flv,ogg,mxf";

    private static (string Muxer, string Extension) GetOutputFormat(VideoConversionQuality quality) => quality switch
    {
        VideoConversionQuality.ArchiveLossless or VideoConversionQuality.MatroskaCopy => ("matroska", ".mkv"),
        VideoConversionQuality.WebM => ("webm", ".webm"),
        VideoConversionQuality.QuickTime => ("mov", ".mov"),
        _ => ("mp4", ".mp4")
    };

    public Task<IReadOnlySet<VideoConversionQuality>> GetConversionModesAsync(FfmpegInstallation tools, CancellationToken token) =>
        Task.Run<IReadOnlySet<VideoConversionQuality>>(async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var encoders = ReadNames(await VideoToolProcess.RunAsync(tools.Ffmpeg, ["-hide_banner", "-encoders"], timeout.Token));
                var muxers = ReadNames(await VideoToolProcess.RunAsync(tools.Ffmpeg, ["-hide_banner", "-muxers"], timeout.Token));
                return Enum.GetValues<VideoConversionQuality>()
                    .Where(mode => muxers.Contains(GetOutputFormat(mode).Muxer) && (mode switch
                    {
                        VideoConversionQuality.CopyOnly or VideoConversionQuality.MatroskaCopy => true,
                        VideoConversionQuality.ArchiveLossless => encoders.Contains("ffv1"),
                        VideoConversionQuality.WebM => encoders.Contains("libvpx-vp9") && encoders.Contains("libopus"),
                        _ => encoders.Contains("libx264") && encoders.Contains("aac")
                    })).ToHashSet();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("Kontrola formátů a kodeků FFmpeg překročila časový limit.");
            }
        }, token);

    private static HashSet<string> ReadNames(string listing) => listing.Split('\n')
        .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length >= 2)
        .SelectMany(parts => parts[1].Split(','))
        .ToHashSet(StringComparer.Ordinal);

    public Task<GalleryVideoInfo> ProbeAsync(FfmpegInstallation tools, string path, CancellationToken token) =>
        Task.Run(async () =>
        {
            FfmpegTools.InspectPath(path);
            using var lease = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await ProbeCoreAsync(tools, path, token);
        }, token);

    private static async Task<GalleryVideoInfo> ProbeCoreAsync(FfmpegInstallation tools, string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string json;
        try
        {
            json = await VideoToolProcess.RunAsync(tools.Ffprobe,
                ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", LocalFormats,
                    "-show_entries", "format=format_name,duration:format_tags=major_brand:"
                    + "stream=index,codec_type,codec_name,pix_fmt,profile,color_transfer,sample_rate,channels:"
                    + "stream_disposition=default,attached_pic:stream_side_data=side_data_type",
                    "-of", "json", "-i", path], timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Analýza videa překročila časový limit.");
        }
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.GetProperty("format");
        var formatName = Text(format, "format_name");
        var brand = format.TryGetProperty("tags", out var tags) ? Text(tags, "major_brand").Trim() : "";
        var isMp4 = string.Equals(System.IO.Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase) &&
            formatName.Split(',').Contains("mp4", StringComparer.Ordinal) &&
            GalleryVideoInfo.IsMp4Brand(brand);
        var seconds = double.TryParse(Text(format, "duration"), CultureInfo.InvariantCulture, out var duration) &&
            duration > 0 && duration < TimeSpan.MaxValue.TotalSeconds ? duration : 0;
        var audio = new List<VideoTrack>();
        VideoTrack? video = null;
        var hdr = false;
        var pixelFormat = "";
        var hasDefaultVideo = false;
        var hasOtherStreams = false;
        var hasAlternateVideo = false;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var type = Text(stream, "codec_type");
            var codec = Text(stream, "codec_name");
            var index = stream.GetProperty("index").GetInt32();
            if (type == "video" &&
                (!stream.TryGetProperty("disposition", out var disposition) || Text(disposition, "attached_pic") != "1"))
            {
                var isDefault = disposition.ValueKind == JsonValueKind.Object && Text(disposition, "default") == "1";
                if (video is not null)
                {
                    hasOtherStreams = hasAlternateVideo = true;
                    if (hasDefaultVideo || !isDefault)
                    {
                        continue;
                    }
                }
                hasDefaultVideo = isDefault;
                pixelFormat = Text(stream, "pix_fmt");
                hdr = Text(stream, "color_transfer") is "smpte2084" or "arib-std-b67" ||
                    (stream.TryGetProperty("side_data_list", out var sideData) && sideData.EnumerateArray()
                        .Any(item => Text(item, "side_data_type").Contains("DOVI", StringComparison.OrdinalIgnoreCase)));
                var profile = Text(stream, "profile");
                var canCopy = codec switch
                {
                    "h264" => !hdr && pixelFormat is ("yuv420p" or "yuvj420p") &&
                        profile is ("Constrained Baseline" or "Baseline" or "Main" or "High"),
                    "hevc" => (profile == "Main" && pixelFormat is ("yuv420p" or "yuvj420p")) ||
                        (profile == "Main 10" && pixelFormat is ("yuv420p" or "yuv420p10le")),
                    _ => false
                };
                video = new VideoTrack(index, codec, canCopy);
            }
            else if (type == "audio")
            {
                var channels = int.TryParse(Text(stream, "channels"), CultureInfo.InvariantCulture, out var count) ? count : 0;
                audio.Add(new(index, codec, codec == "aac" && Text(stream, "profile") is ("LC" or "HE-AAC" or "HE-AACv2") &&
                    channels is (1 or 2 or 6) &&
                    Text(stream, "sample_rate") is ("8000" or "11025" or "12000" or "16000" or "22050" or "24000" or
                        "32000" or "44100" or "48000"), channels));
            }
            else
            {
                hasOtherStreams = true;
            }
        }
        var file = new FileInfo(path);
        return new(path, file.Length, file.LastWriteTimeUtc, brand.Length == 0 ? formatName : brand,
            isMp4, video ?? throw new InvalidDataException("Soubor neobsahuje obrazovou stopu."),
            audio, hasOtherStreams, hasAlternateVideo, hdr, pixelFormat, TimeSpan.FromSeconds(seconds));
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "";

    public Task ConvertAsync(FfmpegInstallation tools, GalleryVideoInfo input, VideoConversionQuality quality,
        Func<string, CancellationToken, Task> publish, IProgress<VideoProgress> progress, CancellationToken token) =>
        Task.Run(async () =>
        {
            if (!input.CanConvertWith(quality))
            {
                throw new NotSupportedException("Video nelze převést zvoleným režimem.");
            }
            FfmpegTools.InspectPath(input.Path);
            using var lease = File.Open(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var current = await ProbeCoreAsync(tools, input.Path, token);
            if (current != (input with { Audio = current.Audio }) || !current.Audio.SequenceEqual(input.Audio))
            {
                throw new IOException("Video se od analýzy změnilo. Otevřete jeho detail znovu.");
            }
            var work = Path.Combine(Path.GetDirectoryName(input.Path)!, $".smartgallery-video-{Guid.NewGuid():N}");
            var archive = quality == VideoConversionQuality.ArchiveLossless;
            var webm = quality == VideoConversionQuality.WebM;
            var remux = quality is VideoConversionQuality.CopyOnly or VideoConversionQuality.MatroskaCopy;
            var videoEncoder = archive ? "ffv1" : remux || input.IsHdr ? "copy" : webm ? "libvpx-vp9" : "libx264";
            var (muxer, extension) = GetOutputFormat(quality);
            var temporary = Path.Combine(work, (quality == VideoConversionQuality.MatroskaCopy ? "remux" : "video") + extension);
            Directory.CreateDirectory(work);
            try
            {
                var args = new List<string>
                {
                    "-hide_banner", "-nostdin", "-n", "-v", "error", "-xerror", "-nostats", "-stats_period", "0.5",
                    "-progress", "pipe:1", "-protocol_whitelist", "file", "-format_whitelist", LocalFormats
                };
                if (archive || videoEncoder == "copy")
                {
                    args.Add("-noautorotate");
                }
                args.AddRange(["-i", input.Path, "-map", $"0:{input.Video.Index}", "-c:v", videoEncoder]);
                if (archive)
                {
                    // A leading '+' makes FFmpeg reject implicit pixel-format conversion.
                    args.AddRange(["-level", "3", "-pix_fmt", "+" + input.PixelFormat, "-noautoscale", "-fps_mode", "passthrough"]);
                }
                else if (videoEncoder != "copy")
                {
                    if (webm)
                    {
                        args.AddRange(["-crf", "31", "-b:v", "0", "-deadline", "good", "-cpu-used", "2"]);
                    }
                    else
                    {
                        var crf = quality switch { VideoConversionQuality.High => "16", VideoConversionQuality.Small => "25", _ => "20" };
                        args.AddRange(["-preset", "medium", "-crf", crf]);
                    }
                    args.AddRange(["-pix_fmt", "yuv420p", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2"]);
                }
                var expectedAudio = new List<string>(input.Audio.Count);
                for (var index = 0; index < input.Audio.Count; index++)
                {
                    var track = input.Audio[index];
                    var copyAudio = archive || remux || (!webm && track.CanCopy);
                    args.AddRange(["-map", $"0:{track.Index}", $"-c:a:{index}", copyAudio ? "copy" : webm ? "libopus" : "aac"]);
                    expectedAudio.Add(copyAudio ? track.Codec : webm ? "opus" : "aac");
                    if (!copyAudio)
                    {
                        var bitrate = (quality, track.OutputChannels == 6) switch
                        {
                            (VideoConversionQuality.High, true) => "448k", (VideoConversionQuality.High, false) => "256k",
                            (VideoConversionQuality.Small, true) => "256k", (VideoConversionQuality.Small, false) => "128k",
                            (_, true) => "384k", _ => "192k"
                        };
                        args.AddRange([$"-b:a:{index}", bitrate,
                            $"-ar:a:{index}", "48000", $"-ac:a:{index}", track.OutputChannels.ToString(CultureInfo.InvariantCulture)]);
                    }
                }
                if (muxer is "mp4" or "mov")
                {
                    args.AddRange(["-movflags", "+faststart"]);
                }
                args.AddRange(["-f", muxer, temporary]);
                progress.Report(new("Převádím video…") { Processed = TimeSpan.Zero });
                await VideoToolProcess.RunAsync(tools.Ffmpeg, args, token, line =>
                {
                    const string key = "out_time_us=";
                    if (line.StartsWith(key, StringComparison.Ordinal) &&
                        long.TryParse(line.AsSpan(key.Length), CultureInfo.InvariantCulture, out var time))
                    {
                        var processed = TimeSpan.FromTicks(Math.Clamp(time, 0, TimeSpan.MaxValue.Ticks / 10) * 10);
                        if (input.Duration > TimeSpan.Zero && processed > input.Duration) processed = input.Duration;
                        progress.Report(new("Převádím video…", input.Duration > TimeSpan.Zero
                            ? Math.Min(99, processed / input.Duration * 100) : null) { Processed = processed });
                    }
                });
                progress.Report(new("Ověřuji výstup převodu…", 99) { Processed = input.Duration });
                var output = await ProbeCoreAsync(tools, temporary, token);
                var compatibleOutput = muxer switch
                {
                    "mp4" => output.IsMp4 && output.Video.CanCopy && output.Audio.All(track => track.CanCopy),
                    "mov" => output.Format == "qt",
                    _ => output.Format.Split(',').Contains("matroska")
                };
                var expectedVideo = videoEncoder switch { "copy" => input.Video.Codec, "libx264" => "h264", "libvpx-vp9" => "vp9", _ => videoEncoder };
                if (!compatibleOutput || output.Size <= 0 || output.Duration <= TimeSpan.Zero ||
                    output.Video.Codec != expectedVideo || (archive && output.PixelFormat != input.PixelFormat) ||
                    !output.Audio.Select(track => track.Codec).SequenceEqual(expectedAudio) ||
                    (input.Duration > TimeSpan.Zero && output.Duration < input.Duration &&
                     (input.Duration - output.Duration).TotalSeconds > Math.Max(1, input.Duration.TotalSeconds * 0.01)))
                {
                    throw new IOException("Výstup nemá očekávaný formát, stopy nebo délku.");
                }
                token.ThrowIfCancellationRequested();
                // Publishing may replace the input; the read lease would block that on Windows.
                lease.Dispose();
                progress.Report(new("Ukládám výsledek do galerie…", 99) { Processed = input.Duration });
                await publish(temporary, token);
            }
            finally
            {
                try
                {
                    FfmpegTools.InspectPath(temporary);
                    File.Delete(temporary);
                    Directory.Delete(work);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning("Nelze uklidit pracovní výstup {0}: {1}", work, exception.Message);
                }
            }
        }, token);

    public Task PlayExternalAsync(FfmpegInstallation tools, GalleryVideoInfo video, CancellationToken token) =>
        Task.Run(async () =>
        {
            if (!video.CanPlayExternal)
            {
                throw new InvalidOperationException("Pro tento formát není přímé přehrávání ve FFplay k dispozici.");
            }
            FfmpegTools.InspectPath(video.Path);
            using var lease = File.Open(video.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var file = new FileInfo(video.Path);
            if (file.Length != video.Size || file.LastWriteTimeUtc != video.LastWriteTimeUtc)
            {
                throw new IOException("Video se od analýzy změnilo.");
            }
            await VideoToolProcess.RunAsync(tools.Ffplay,
                ["-hide_banner", "-loglevel", "error", "-nostats", "-autoexit", "-protocol_whitelist", "file",
                    "-format_whitelist", LocalFormats, "-i", video.Path], token, showWindow: true);
        }, token);
}
