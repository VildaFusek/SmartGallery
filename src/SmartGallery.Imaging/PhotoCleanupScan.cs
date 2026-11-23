using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace SmartGallery.Imaging;

public sealed record CleanupSuggestion(GalleryFile File, GalleryFile? Keep, bool Exact, bool Protected,
    bool OfferTrash, PhotoOutputFormat? Format, string Reason)
{
    public PhotoPixelFingerprint? Pixels { get; init; }
    public bool Screenshot { get; init; }
}
public sealed record CleanupScan(IReadOnlyList<CleanupSuggestion> Rows, int Skipped);

public static class PhotoCleanupScan
{
    private sealed record Sample(long Bytes, long Ticks, int Width, int Height, bool Camera, ulong Hash,
        byte[] Colors, string? Sha, string? EdgeHash, PhotoPixelFingerprint? Pixels = null);
    private sealed record Item(GalleryFile File, Sample Sample, bool Screen, bool Derived)
    {
        public bool Protected => Sample.Camera || (!Derived && !Screen);
    }

    public static Task<CleanupScan> ScanAsync(Library library, IReadOnlyList<GalleryFile> files, IGalleryCatalog catalog,
        IProgress<int> progress, CancellationToken token, IProgress<(GalleryFile File, string Message)>? activity = null) => Task.Run(async () =>
    {
        var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SmartGallery", "Cleanup", $"{library.Id:N}-v3.json");
        Dictionary<Guid, Sample> cache = [];
        try
        {
            using var stream = File.OpenRead(cachePath);
            cache = await JsonSerializer.DeserializeAsync<Dictionary<Guid, Sample>>(stream, cancellationToken: token) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        var sizes = files.GroupBy(f => f.SizeBytes).Where(g => g.Skip(1).Any()).Select(g => g.Key).ToHashSet();
        var items = new List<Item>();
        var skipped = 0;
        var last = -1;
        bool changed = false;
        long nextPreview = 0;
        void Show(GalleryFile file, string phase, int index, int count)
        {
            var now = Environment.TickCount64;
            if (index != 0 && now < nextPreview) return;
            nextPreview = now + 350;
            activity?.Report((file, $"{phase} · {index + 1}/{count}"));
        }
        for (int i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var f = files[i];
            Show(f, "Kontrola metadat a obrazových otisků", i, files.Count);
            try
            {
                var path = await catalog.GetReadPathAsync(library.RootPath, f, token);
                var info = new FileInfo(path);
                if (info.Length != f.SizeBytes || info.LastWriteTimeUtc.Ticks != f.LastWriteTimeUtc.UtcTicks)
                    throw new IOException("Soubor se změnil.");
                if (!cache.TryGetValue(f.Id, out var s) || s is null || s.Bytes != f.SizeBytes || s.Ticks != f.LastWriteTimeUtc.UtcTicks ||
                    s.Colors is not { Length: 0 or 216 })
                {
                    changed = true;
                    s = await ReadSampleAsync(path, f, token);
                }
                if (sizes.Contains(f.SizeBytes) && s.EdgeHash is null)
                {
                    using var input = File.OpenRead(path);
                    var data = new byte[(int)Math.Min(input.Length, 8192)];
                    if (input.Length <= data.Length)
                    {
                        await input.ReadExactlyAsync(data, token);
                        var hash = Convert.ToHexString(SHA256.HashData(data));
                        s = s with { EdgeHash = hash, Sha = hash };
                    }
                    else
                    {
                        await input.ReadExactlyAsync(data.AsMemory(0, 4096), token);
                        input.Seek(-4096, SeekOrigin.End);
                        await input.ReadExactlyAsync(data.AsMemory(4096), token);
                        s = s with { EdgeHash = Convert.ToHexString(SHA256.HashData(data)) };
                    }
                    changed = true;
                }
                cache[f.Id] = s;
                string name = f.RelativePath.ToLowerInvariant(), caption = f.Analysis?.Caption.ToLowerInvariant() ?? "";
                var named = Has(name, "screenshot", "screen shot", "snímek obrazovky", "snimek obrazovky", "výstřiž");
                var described = Has(caption, "screenshot", "screen capture", "snímek obrazovky", "snímek webové stránky");
                var screen = named || described;
                var derived = named || Has(name, "_webp.", "_jpeg.", "_png.", "whatsapp images", "resized", "thumbnail");
                items.Add(new(f, s, screen, derived));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
            var p = (i + 1) * 80L / files.Count;
            if (p != last) progress.Report(last = (int)p);
        }
        token.ThrowIfCancellationRequested();
        // Equal lengths and matching edges are only a shortlist, never proof of identity.
        var collisions = items.Where(i => i.Sample.EdgeHash is not null)
            .GroupBy(i => (i.Sample.Bytes, i.Sample.EdgeHash)).Where(g => g.Skip(1).Any())
            .SelectMany(g => g.Where(i => i.Sample.Sha is null)).ToArray();
        var unreadable = new HashSet<Guid>();
        for (var n = 0; n < collisions.Length; n++)
        {
            token.ThrowIfCancellationRequested();
            var item = collisions[n];
            Show(item.File, "Porovnávání obsahu souborů", n, collisions.Length);
            try
            {
                var path = await catalog.GetReadPathAsync(library.RootPath, item.File, token);
                cache[item.File.Id] = item.Sample with { Sha = await HashAsync(path, token) };
                changed = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unreadable.Add(item.File.Id); }
            var p = 80 + (int)((n + 1L) * 5 / collisions.Length);
            if (p != last) progress.Report(last = p);
        }
        items.RemoveAll(i => unreadable.Contains(i.File.Id));
        for (var n = 0; n < items.Count; n++)
        {
            items[n] = items[n] with { Sample = cache[items[n].File.Id] };
            if (items[n].Sample.Width == 0 && items[n].Sample.Sha is null && !items[n].Screen) skipped++;
        }
        // Full pixel verification only where dimensions match another encoding. Reuse unchanged fingerprints.
        var candidates = items.Where(i => i.Sample.Width > 0 && (long)i.Sample.Width * i.Sample.Height <= 32_000_000)
            .GroupBy(i => (i.Sample.Width, i.Sample.Height))
            .Where(g => g.Select(i => i.Sample.Sha ?? i.File.Id.ToString("N")).Distinct().Skip(1).Any())
            .SelectMany(g => g).Where(i => i.Sample.Pixels is null).ToArray();
        var knownPixels = items.Where(i => i.Sample.Sha is not null && i.Sample.Pixels is not null)
            .DistinctBy(i => i.Sample.Sha).ToDictionary(i => i.Sample.Sha!, i => i.Sample.Pixels!);
        for (int n = 0; n < candidates.Length; n++)
        {
            token.ThrowIfCancellationRequested();
            var item = candidates[n];
            Show(item.File, "Ověřování shodných pixelů", n, candidates.Length);
            try
            {
                if (item.Sample.Sha is not { } sha || !knownPixels.TryGetValue(sha, out var pixels))
                {
                    var path = await catalog.GetReadPathAsync(library.RootPath, item.File, token);
                    pixels = await PixelHashAsync(path, token);
                    if (item.Sample.Sha is { } hash) knownPixels[hash] = pixels;
                }
                cache[item.File.Id] = item.Sample with { Pixels = pixels };
                changed = true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            var p = 85 + (int)((n + 1L) * 14 / candidates.Length);
            if (p != last) progress.Report(last = p);
        }
        var result = Plan(items.Select(i =>
        {
            var sample = cache[i.File.Id];
            if (sample.Pixels is null && sample.Sha is { } hash && knownPixels.TryGetValue(hash, out var pixels))
                sample = sample with { Pixels = pixels };
            return i with { Sample = sample };
        }).ToList(), skipped + unreadable.Count, token);
        if (!changed) { progress.Report(100); return result; }
        var temporary = cachePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, cache, cancellationToken: token);
            File.Move(temporary, cachePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        progress.Report(100);
        return result;
    }, token);

    public static async Task<string> HashAsync(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    public static Task<PhotoPixelFingerprint> PixelHashAsync(string path, CancellationToken token) => Task.Run(async () =>
    {
        using var stream = File.OpenRead(path);
        return await new WindowsPhotoPixelFingerprintReader().ReadAsync(stream, token);
    }, token);

    private static bool Has(string text, params string[] terms) => terms.Any(text.Contains);

    private static async Task<Sample> ReadSampleAsync(string path, GalleryFile f, CancellationToken token)
    {
        var sample = new Sample(f.SizeBytes, f.LastWriteTimeUtc.UtcTicks, 0, 0, false, 0, [], null, null);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
            using var stream = await file.OpenReadAsync().AsTask(token);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
            if (decoder.FrameCount != 1) return sample;
            var properties = await file.Properties.GetImagePropertiesAsync().AsTask(token);
            var rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight,
                new BitmapTransform { ScaledWidth = rotated ? 8u : 9u, ScaledHeight = rotated ? 9u : 8u },
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token);
            var pixels = data.DetachPixelData();
            if (pixels.Length != 9 * 8 * 4) return sample;
            var colors = new byte[216];
            ulong hash = 0;
            for (int i = 0; i < 72; i++)
            {
                for (int c = 0; c < 3; c++) colors[i * 3 + c] = (byte)(255 - (255 - pixels[i * 4 + c]) * pixels[i * 4 + 3] / 255);
                if (i % 9 > 0)
                {
                    int L(int n) => colors[n * 3] + 2 * colors[n * 3 + 1] + colors[n * 3 + 2];
                    hash = (hash << 1) | (L(i) > L(i - 1) ? 1UL : 0UL);
                }
            }
            return sample with { Width = (int)decoder.OrientedPixelWidth, Height = (int)decoder.OrientedPixelHeight,
                Camera = !string.IsNullOrWhiteSpace(properties.CameraModel), Hash = hash, Colors = colors };
        }
        catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException) { return sample; }
    }

    private static CleanupScan Plan(List<Item> items, int skipped, CancellationToken token)
    {
        var rows = new List<CleanupSuggestion>();
        var retained = new HashSet<Guid>();
        var roots = new List<Item>();
        var bands = new Dictionary<(int, ushort), List<Item>>();
        var ordered = items.OrderByDescending(i => i.Sample.Camera).ThenBy(i => i.Derived)
            .ThenByDescending(i => (long)i.Sample.Width * i.Sample.Height).ThenByDescending(i => i.File.SizeBytes)
            .ThenBy(i => i.File.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var group in ordered.GroupBy(i => i.Sample.Pixels is { } p ? $"pixels:{p.Width}:{p.Height}:{p.Sha256}" :
                     i.Sample.Sha ?? i.File.Id.ToString("N")))
        {
            token.ThrowIfCancellationRequested();
            var i = group.First();
            foreach (var copy in group.Skip(1))
            {
                retained.Add(i.File.Id);
                var bytesMatch = copy.Sample.Sha is not null && copy.Sample.Sha == i.Sample.Sha;
                rows.Add(new(copy.File, i.File, bytesMatch, !bytesMatch && copy.Protected, true, null, bytesMatch
                    ? "Bajtově shodná kopie (SHA-256)." : "Shodné zobrazené pixely; soubory mohou mít rozdílná metadata.")
                    { Pixels = bytesMatch ? null : copy.Sample.Pixels, Screenshot = copy.Screen });
            }
            var s = i.Sample;
            Item? match = null;
            if (s.Colors.Length == 216 && BitOperations.PopCount(s.Hash) is > 5 and < 59)
            {
                for (int b = 0; b < 4 && match is null && !retained.Contains(i.File.Id); b++)
                    if (bands.TryGetValue((b, (ushort)(s.Hash >> (16 * b))), out var candidates))
                        match = candidates.FirstOrDefault(other => Similar(s, other.Sample));
                if (match is null)
                    for (int b = 0; b < 4; b++)
                    {
                        var key = (b, (ushort)(s.Hash >> (16 * b)));
                        if (!bands.TryGetValue(key, out var bucket)) bands[key] = bucket = [];
                        if (bucket.Count < 64) bucket.Add(i);
                    }
            }
            if (match is null) roots.Add(i);
            else
            {
                retained.Add(match.File.Id);
                var reason = match.Sample.Camera && !s.Camera ? "Druhá verze má údaj o fotoaparátu." :
                    (long)match.Sample.Width * match.Sample.Height > (long)s.Width * s.Height ? "Druhá verze má vyšší rozlišení." :
                    "Podobnost není důkazem stejného záběru.";
                rows.Add(new(i.File, match.File, false, i.Protected, true, null, $"Vizuálně podobná verze. {reason}") { Screenshot = i.Screen });
            }
        }
        foreach (var i in roots)
        {
            token.ThrowIfCancellationRequested();
            if (retained.Contains(i.File.Id)) continue;
            var ext = Path.GetExtension(i.File.Name).ToLowerInvariant();
            var s = i.Sample;
            var density = (double)s.Bytes / Math.Max(1L, (long)s.Width * s.Height);
            var palette = new HashSet<int>();
            for (int n = 0; n < s.Colors.Length; n += 3)
                palette.Add((s.Colors[n] >> 5) * 64 + (s.Colors[n + 1] >> 5) * 8 + (s.Colors[n + 2] >> 5));
            var flat = palette.Count is > 0 and <= 20;
            PhotoOutputFormat? format = s.Width > 0 && s.Bytes >= 64 * 1024 &&
                (ext == ".bmp" || (ext == ".png" && density > (flat ? 0.35 : 1.25) && Math.Max(s.Width, s.Height) <= 16383))
                ? ext == ".bmp" ? PhotoOutputFormat.Png : PhotoOutputFormat.WebPLossless : null;
            if (i.Screen || format.HasValue)
                rows.Add(new(i.File, null, false, i.Protected, i.Screen, format, i.Screen
                    ? "Možný snímek obrazovky podle názvu nebo uloženého AI popisu."
                    : $"Kandidát na úsporu: {ext} · {density:F1} B/pixel" + (flat ? " · malá paleta v náhledu." : ".")) { Screenshot = i.Screen });
        }
        return new(rows, skipped);
    }

    private static bool Similar(Sample a, Sample b)
    {
        if (BitOperations.PopCount(a.Hash ^ b.Hash) > 3 ||
            Math.Abs((double)a.Width * b.Height / ((double)a.Height * b.Width) - 1) > 0.04) return false;
        int sum = 0;
        for (int i = 0; i < a.Colors.Length; i++) sum += Math.Abs(a.Colors[i] - b.Colors[i]);
        return sum < a.Colors.Length * 12;
    }
}
