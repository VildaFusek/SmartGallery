using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using SmartGallery.Imaging;

namespace SmartGallery.ViewModels;

internal enum CleanupAction { Keep, Trash, Optimize }
internal sealed record CleanupChoice(CleanupSuggestion Row, CleanupAction Action);

public sealed partial class GalleryViewModel
{
    internal GalleryFile[] GalleryPhotos() => _catalogFiles.Where(f => f.Kind == MediaKind.Photo).ToArray();

    internal async Task ConvertPhotosAsync(IReadOnlyList<GalleryFile> files, PhotoConversionOptions options, bool replace,
        Func<GalleryFile, PhotoConversionResult, Task<bool>> confirmReplacement, CancellationToken token,
        Action<string, int, int> report)
    {
        if (!CanChangeFilters || _library is not { } library || library.Id != _displayedLibraryId || files.Count == 0) return;
        var catalog = _catalogFiles.ToHashSet();
        if (files.Any(f => f.Kind != MediaKind.Photo || !catalog.Contains(f))) return;
        IsBusy = true;
        var completed = 0;
        var failed = 0;
        string? error = null;
        bool stopped = false, attempted = false;
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var file = files[i];
                var label = StatusMessage = $"Převod {i + 1}/{files.Count} · {file.Name}";
                using var step = new PhotoStep(report, label, i, files.Count);
                try
                {
                    var path = await GetGalleryReadPathAsync(file, token);
                    await WindowsPhotoConverter.ConvertAsync(path, options, async (temporary, result, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (replace && !await confirmReplacement(file, result)) throw new OperationCanceledException();
                        ct.ThrowIfCancellationRequested();
                        attempted = true;
                        return await _galleryCatalog.PublishConvertedPhotoAsync(library, file, temporary, replace, ct);
                    }, token, step.Progress);
                    completed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { failed++; error ??= $"{file.Name}: {ex.Message}"; }
                step.Update(100);
            }
        }
        catch (OperationCanceledException) { stopped = true; }
        finally
        {
            var refresh = attempted ? await RefreshAfterGalleryWorkAsync() : "";
            IsBusy = false;
            StatusMessage = $"Převedeno: {completed} · chyby: {failed}" + (stopped ? " · zastaveno" : "") +
                (error is null ? "" : $" · {error}") + refresh;
        }
    }

    internal async Task CleanupAsync(GalleryFile[] files, Func<CleanupScan, Task<IReadOnlyList<CleanupChoice>?>> review,
        Action<string, int, int> report, CancellationToken token, Action<GalleryFile, string> detail, Action<GalleryFile, string> issue)
    {
        if (!CanChangeFilters || _requiresGalleryRefresh || _library is not { } library || library.Id != _displayedLibraryId)
        { StatusMessage = "Galerie není připravena. Obnovte ji a zkuste kontrolu znovu."; return; }
        string ai = "";
        int scanStart = 0;
        var pending = files.Where(f => GetPhotoAnalysis(f) is null).Select(f => f.Id).ToHashSet();
        if (pending.Count > 0 && CanAnalyzeGallery())
        {
            scanStart = 40;
            await AnalyzeGalleryAsync(token, pending, (label, file, total) => report(label, file, total * scanStart / 100), detail, issue);
            if (token.IsCancellationRequested || _requiresGalleryRefresh || !CanChangeFilters || _displayedLibraryId != library.Id) return;
            ai = $" · {StatusMessage}";
            // A catalog refresh replaces records; use the newly saved captions for this same selection.
            var ids = files.Select(f => f.Id).ToHashSet();
            files = _catalogFiles.Where(f => ids.Contains(f.Id)).ToArray();
        }
        else if (pending.Count > 0 && !_readiness.CaptioningReady)
            ai = " · AI model není připraven, použity dostupné popisy";
        var paths = _catalogFiles.ToLookup(f => f.RelativePath.Replace('/', '\\'), StringComparer.OrdinalIgnoreCase);
        var scope = files.Where(f => paths[f.RelativePath.Replace('/', '\\')].SequenceEqual([f])).ToArray();
        _isAnalyzing = true;
        IsBusy = true;
        bool attempted = false;
        int trashed = 0, converted = 0, unchanged = 0, failed = 0;
        long saved = 0, trashBytes = 0;
        string outcome = "Úklid dokončen", extra = "";
        try
        {
            StatusMessage = "Hledám kopie, snímky obrazovky a možnosti úspory…";
            CleanupScan scan;
            bool scanning = true;
            var activity = new Progress<(GalleryFile File, string Message)>(v =>
            { if (scanning && !token.IsCancellationRequested) detail(v.File, v.Message); });
            using (var step = new PhotoStep((label, file, total) => report(label, -1, scanStart + total * (75 - scanStart) / 100),
                "Kontrola galerie", 0, 1))
            {
                try { scan = await PhotoCleanupScan.ScanAsync(library, scope, _galleryCatalog, step.Progress, token, activity); }
                finally { scanning = false; }
            }
            token.ThrowIfCancellationRequested();
            var skipped = scan.Skipped + files.Length - scope.Length;
            var destinations = paths.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var proposals = new List<CleanupSuggestion>(scan.Rows.Count);
            foreach (var suggestion in scan.Rows)
            {
                var row = suggestion;
                if (row.Format is { } format && !destinations.Add(Path.ChangeExtension(row.File.RelativePath.Replace('/', '\\'),
                    format == PhotoOutputFormat.Png ? ".png" : ".webp")))
                {
                    if (!row.OfferTrash) { skipped++; continue; }
                    row = row with { Format = null };
                }
                proposals.Add(row);
            }
            scan = new(proposals, skipped);
            if (skipped > 0) extra = $" · při analýze přeskočeno: {skipped}";
            if (scan.Rows.Count == 0) { outcome = "Nenalezeny žádné návrhy"; report(outcome, 100, 100); return; }
            StatusMessage = "Zkontrolujte návrhy úklidu.";
            var choices = await review(scan);
            token.ThrowIfCancellationRequested();
            if (choices is null) { outcome = "Úklid nepotvrzen"; return; }
            var rows = scan.Rows.ToHashSet();
            var actions = choices.Where(c => c.Action != CleanupAction.Keep).ToArray();
            var keepers = scan.Rows.Where(r => r.Keep is not null).Select(r => r.Keep!.Id).ToHashSet();
            if (actions.Select(c => c.Row.File.Id).Distinct().Count() != actions.Length ||
                actions.Any(c => !rows.Contains(c.Row) || keepers.Contains(c.Row.File.Id) ||
                    (c.Action == CleanupAction.Trash ? !c.Row.OfferTrash : c.Row.Format is null)))
                throw new IOException("Návrh obsahuje konfliktní akce.");
            var trash = actions.Where(c => c.Action == CleanupAction.Trash).Select(c => c.Row).ToArray();
            var retainedHashes = new Dictionary<Guid, string>();
            var retainedPixels = new Dictionary<Guid, PhotoPixelFingerprint>();
            for (int i = 0; i < trash.Length; i++)
            {
                var row = trash[i];
                token.ThrowIfCancellationRequested();
                var label = StatusMessage = $"Kontrola před přesunem {i + 1}/{trash.Length} · {row.File.Name}";
                detail(row.File, "Ověření před přesunem do koše.");
                using var step = new PhotoStep(ReportAction, label, i, actions.Length);
                if (row.Exact && row.Keep is { } keep)
                {
                    if (!retainedHashes.TryGetValue(keep.Id, out var hash))
                        retainedHashes[keep.Id] = hash = await PhotoCleanupScan.HashAsync(await GetGalleryReadPathAsync(keep, token), token);
                    if (hash != await PhotoCleanupScan.HashAsync(await GetGalleryReadPathAsync(row.File, token), token))
                        throw new IOException($"Shoda už neplatí: {row.File.Name}. Vyhledejte návrhy znovu.");
                }
                else if (row.Pixels is { } expected && row.Keep is { } original)
                {
                    if (!retainedPixels.TryGetValue(original.Id, out var pixels))
                        retainedPixels[original.Id] = pixels = await PhotoCleanupScan.PixelHashAsync(await GetGalleryReadPathAsync(original, token), token);
                    if (pixels != expected || await PhotoCleanupScan.PixelHashAsync(await GetGalleryReadPathAsync(row.File, token), token) != expected)
                        throw new IOException($"Obrazová shoda už neplatí: {row.File.Name}.");
                }
                step.Update(100);
            }
            if (trash.Length > 0)
            {
                attempted = true;
                var remove = trash.Select(r => r.File).ToArray();
                var keep = trash.Where(r => r.Keep is not null).Select(r => r.Keep!).DistinctBy(f => f.Id).ToArray();
                if (keep.Length > 0) await _galleryCatalog.MoveDuplicatesToTrashAsync(library, remove, keep, token);
                else await _galleryCatalog.MoveToTrashAsync(library, remove, token);
                trashed = remove.Length;
                trashBytes = remove.Sum(f => f.SizeBytes);
            }
            var index = trashed;
            foreach (var choice in actions.Where(c => c.Action == CleanupAction.Optimize))
            {
                token.ThrowIfCancellationRequested();
                var file = choice.Row.File;
                var label = StatusMessage = $"Bezeztrátový převod {index + 1}/{actions.Length} · {file.Name}";
                detail(file, "Převod a ověření rozměrů, pixelů a barevného profilu.");
                using var step = new PhotoStep(ReportAction, label, index++, actions.Length);
                try
                {
                    var path = await GetGalleryReadPathAsync(file, token);
                    await WindowsPhotoConverter.ConvertAsync(path, new(choice.Row.Format!.Value), async (temporary, result, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (result.OutputBytes >= result.SourceBytes) { unchanged++; return file.RelativePath; }
                        attempted = true;
                        var output = await _galleryCatalog.PublishConvertedPhotoAsync(library, file, temporary, true, ct);
                        converted++;
                        saved += result.SourceBytes - result.OutputBytes;
                        return output;
                    }, token, step.Progress, verifyPixels: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    issue(file, "Převod této fotografie se nezdařil.");
                    System.Diagnostics.Trace.TraceWarning("{0}: {1}", file.RelativePath, ex);
                }
                step.Update(100);
            }
        }
        catch (OperationCanceledException) { outcome = "Úklid zastaven"; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("{0}", ex);
            outcome = "Kontrolu nebylo možné dokončit. Obnovte galerii a spusťte ji znovu.";
        }
        finally
        {
            var refresh = attempted ? await RefreshAfterGalleryWorkAsync() : "";
            _isAnalyzing = false;
            IsBusy = false;
            StatusMessage = outcome + (trashed > 0 ? $" · v koši: {trashed} ({trashBytes:N0} B až po vysypání)" : "") +
                (converted > 0 ? $" · převedeno: {converted}, ušetřeno {saved:N0} B" : "") +
                (unchanged > 0 ? $" · ponecháno bez úspory: {unchanged}" : "") +
                (failed > 0 ? $" · nepřevedeno: {failed}" : "") + extra + ai + refresh;
        }

        void ReportAction(string label, int file, int total) => report(label, file, 75 + total / 4);
    }

    private sealed class PhotoStep : IDisposable
    {
        private Action<int>? _update;
        public IProgress<int> Progress { get; }
        public PhotoStep(Action<string, int, int> report, string label, int index, int count)
        {
            int last = -1;
            _update = p =>
            {
                if (p <= last) return;
                last = p;
                report(label, p, (int)((index + p / 100d) * 100 / count));
            };
            Progress = new Progress<int>(Update);
            Update(0);
        }
        public void Update(int value) => _update?.Invoke(value);
        public void Dispose() => _update = null;
    }
}
