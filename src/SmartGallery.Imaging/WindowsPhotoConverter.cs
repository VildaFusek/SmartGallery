using System.Buffers.Binary;
using NetVips;

namespace SmartGallery.Imaging;

public enum PhotoOutputFormat { Jpeg, Png, WebP, WebPLossless }

public sealed record PhotoConversionOptions(PhotoOutputFormat Format, int Quality = 82, int? MaxEdge = null);
public sealed record PhotoConversionResult(long SourceBytes, long OutputBytes, int Width, int Height);

public static class WindowsPhotoConverter
{
    public static Task<string> ConvertAsync(string path, PhotoOutputFormat format,
        Func<string, CancellationToken, Task<string>> publish, CancellationToken token) =>
        ConvertAsync(path, new PhotoConversionOptions(format), (temporary, _, ct) => publish(temporary, ct), token);

    public static Task<string> ConvertAsync(string path, PhotoConversionOptions options,
        Func<string, PhotoConversionResult, CancellationToken, Task<string>> publish, CancellationToken token) =>
        ConvertAsync(path, options, publish, token, null);

    public static async Task<string> ConvertAsync(string path, PhotoConversionOptions options,
        Func<string, PhotoConversionResult, CancellationToken, Task<string>> publish, CancellationToken token,
        IProgress<int>? progress, bool verifyPixels = false)
    {
        var name = options.Format switch
        {
            PhotoOutputFormat.Png => "photo.png",
            PhotoOutputFormat.Jpeg => "photo.jpg",
            PhotoOutputFormat.WebP or PhotoOutputFormat.WebPLossless => "photo.webp",
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
        if (options.Quality is < 1 or > 100 || options.MaxEdge is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        var work = Path.Combine(Path.GetDirectoryName(path)!, $".smartgallery-photo-{Guid.NewGuid():N}");
        var temporary = Path.Combine(work, name);
        try
        {
            var result = await Task.Run(() =>
            {
                Directory.CreateDirectory(work);
                return Encode(path, temporary, options, token, progress, verifyPixels);
            }, token);
            token.ThrowIfCancellationRequested();
            return await publish(temporary, result, token);
        }
        finally
        {
            await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(work)) return;
                    for (var folder = new DirectoryInfo(work); folder is not null; folder = folder.Parent)
                    {
                        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            throw new IOException("Cesta pracovní složky se změnila.");
                    }
                    File.Delete(temporary);
                    Directory.Delete(work);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.TraceWarning("Nelze uklidit pracovní složku {0}: {1}", work, error.Message);
                }
            });
        }
    }

    private sealed class StageProgress(IProgress<int>? target, int start, int span) : IProgress<int>
    {
        public void Report(int value) => target?.Report(start + Math.Clamp(value, 0, 100) * span / 100);
    }

    private static PhotoConversionResult Encode(string path, string temporary, PhotoConversionOptions options, CancellationToken token,
        IProgress<int>? progress, bool verifyPixels)
    {
        using var source = File.OpenRead(path);
        RejectAnimatedPng(source, token);
        using var image = Image.NewFromStream(source, access: Enums.Access.Sequential, failOn: Enums.FailOn.Error);
        if (image.GetTypeOf("n-pages") != 0 && (int)image.Get("n-pages") > 1)
            throw new InvalidDataException("Animace a vícestránkové obrázky nelze převést jako jednu fotografii.");

        using var oriented = image.Autorot();
        var edge = Math.Max(oriented.Width, oriented.Height);
        using var resized = options.MaxEdge is int limit && edge > limit ? oriented.Resize((double)limit / edge) : null;
        var output = resized ?? oriented;
        if ((options.Format is PhotoOutputFormat.WebP or PhotoOutputFormat.WebPLossless) &&
            Math.Max(output.Width, output.Height) > 16383)
            throw new InvalidDataException("WebP umožňuje nejvýše 16 383 px na stranu. Zvolte kratší maximální hranu.");
        if (options.Format == PhotoOutputFormat.WebPLossless && output.Format != Enums.BandFormat.Uchar)
            throw new InvalidDataException("Bezeztrátový WebP podporuje 8bitové kanály. Pro vyšší bitovou hloubku zvolte PNG.");

        token.ThrowIfCancellationRequested();
        try
        {
            output.SetProgress(new StageProgress(progress, 0, 80), token);
            switch (options.Format)
            {
                case PhotoOutputFormat.Png:
                    output.Pngsave(temporary, compression: 9, filter: Enums.ForeignPngFilter.All, keep: Enums.ForeignKeep.Icc);
                    break;
                case PhotoOutputFormat.Jpeg:
                    output.Jpegsave(temporary, q: options.Quality, optimizeCoding: true, background: [255, 255, 255],
                        keep: Enums.ForeignKeep.Icc);
                    break;
                default:
                    output.Webpsave(temporary, q: options.Quality, lossless: options.Format == PhotoOutputFormat.WebPLossless,
                        exact: true, keep: Enums.ForeignKeep.Icc);
                    break;
            }
            token.ThrowIfCancellationRequested();
            using var encoded = File.OpenRead(temporary);
            using var check = Image.NewFromStream(encoded, access: Enums.Access.Sequential, failOn: Enums.FailOn.Error);
            if (encoded.Length == 0 || check.Width != output.Width || check.Height != output.Height)
                throw new IOException("Rozměry výsledné fotografie neodpovídají převodu.");
            if (verifyPixels)
            {
                using var input = File.OpenRead(path);
                using var original = Image.NewFromStream(input, access: Enums.Access.Sequential, failOn: Enums.FailOn.Error);
                using var reference = original.Autorot();
                if (check.Width != reference.Width || check.Height != reference.Height || check.Bands != reference.Bands ||
                    check.Format != reference.Format || check.Interpretation != reference.Interpretation)
                    throw new InvalidDataException("Výsledek nezachovává původní obrazové kanály nebo rozměry.");
                const string profile = "icc-profile-data";
                if (reference.GetTypeOf(profile) != check.GetTypeOf(profile) ||
                    (reference.GetTypeOf(profile) != 0 && !((byte[])reference.Get(profile)).AsSpan().SequenceEqual((byte[])check.Get(profile))))
                    throw new InvalidDataException("Barevný profil výsledku se liší.");
                using var difference = check.Relational(reference, Enums.OperationRelational.Noteq);
                difference.SetProgress(new StageProgress(progress, 80, 19), token);
                if (difference.Max() != 0) throw new InvalidDataException("Obrazové body výsledku se liší.");
            }
            else
            {
                check.SetProgress(new StageProgress(progress, 80, 19), token);
                _ = check.Avg();
            }
            token.ThrowIfCancellationRequested();
            return new(source.Length, encoded.Length, check.Width, check.Height);
        }
        catch (VipsException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }

    // Some PNG loaders expose only the default image of an APNG, without n-pages.
    private static void RejectAnimatedPng(Stream stream, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[8];
        try
        {
            if (stream.Length < 8) return;
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt64BigEndian(header) != 0x89504e470d0a1a0aUL) return;
            while (stream.Position <= stream.Length - 8)
            {
                token.ThrowIfCancellationRequested();
                stream.ReadExactly(header);
                if (header[4..].SequenceEqual("acTL"u8))
                    throw new InvalidDataException("Animované PNG nelze převést jako jednu fotografii.");
                if (header[4..].SequenceEqual("IDAT"u8) || header[4..].SequenceEqual("IEND"u8)) return;
                var skip = BinaryPrimitives.ReadUInt32BigEndian(header) + 4L;
                if (skip > stream.Length - stream.Position) throw new InvalidDataException("Neúplný soubor PNG.");
                stream.Seek(skip, SeekOrigin.Current);
            }
        }
        finally { stream.Position = 0; }
    }
}
