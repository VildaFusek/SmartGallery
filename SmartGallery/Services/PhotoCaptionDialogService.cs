using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SmartGallery.Application.AI;
using SmartGallery.Application.Catalog;
using SmartGallery.Application.Importing;
using Windows.Storage.Pickers;

namespace SmartGallery.Services;

public sealed class PhotoCaptionDialogService(
    Func<Window> getWindow,
    IImageCaptionGenerator generator,
    IMediaDateReader mediaDates,
    ILogger<PhotoCaptionDialogService> logger) : IPhotoCaptionDialogService
{
    private CancellationTokenSource? _stop;
    private Task? _operation;
    private ContentDialog? _dialog;

    public async Task CancelAndWaitAsync()
    {
        _stop?.Cancel();
        _dialog?.Hide();
        var operation = _operation;
        if (operation is not null)
        {
            await operation;
        }
    }

    public async Task PickAndDescribeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = getWindow();
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif" })
        {
            picker.FileTypeFilter.Add(extension);
        }
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (file is null)
        {
            return;
        }
        await DescribeAsync(file.Path, cancellationToken);
    }

    public Task DescribeAsync(string path, CancellationToken cancellationToken) =>
        ShowCaptionAsync(path, suggestName: false, cancellationToken);

    public Task<string?> ConfirmSmartNameAsync(string path, CancellationToken cancellationToken) =>
        ShowCaptionAsync(path, suggestName: true, cancellationToken);

    private async Task<string?> ShowCaptionAsync(string path, bool suggestName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var phase = new TextBlock { Text = "Načítám model a generuji popisek na CPU…", TextWrapping = TextWrapping.Wrap };
        var ring = new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left };
        var result = new TextBox
        {
            Header = "Výsledek Florence-2 (typicky anglicky)",
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 220
        };
        var content = new StackPanel { Spacing = 12, MaxWidth = 460 };
        var fileName = Path.GetFileName(path);
        content.Children.Add(new TextBlock { Text = fileName, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(phase);
        content.Children.Add(ring);
        content.Children.Add(result);
        var namePreview = new TextBox
        {
            Header = "Navržený název", IsReadOnly = true, TextWrapping = TextWrapping.Wrap
        };
        if (suggestName)
        {
            content.Children.Add(namePreview);
        }
        content.Children.Add(new TextBlock
        {
            Text = suggestName
                ? "AI se může mýlit. Po potvrzení se přejmenuje pouze kopie v galerii. Návrh vzniká lokálně."
                : "První výpočet načítá model do RAM a může trvat déle. AI se může mýlit. Fotografie se pouze čte; nic se nepřejmenovává ani neodesílá do cloudu.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12
        });
        var dialog = new ContentDialog
        {
            Title = suggestName ? "Chytré přejmenování" : "Lokální popisek fotografie", Content = content,
            CloseButtonText = "Zastavit a zavřít",
            PrimaryButtonText = suggestName ? "Přejmenovat" : "",
            IsPrimaryButtonEnabled = false, DefaultButton = ContentDialogButton.Close
        };
        string? suggestedName = null;
        async Task RunAsync(CancellationToken token)
        {
            try
            {
                var caption = await generator.GenerateAsync(path, token);
                token.ThrowIfCancellationRequested();
                result.Text = caption.Text;
                phase.Text = caption.Truncated
                    ? "Popisek dosáhl limitu 64 tokenů; může být neúplný."
                    : "Popisek byl vygenerován lokálně na CPU.";
                if (suggestName)
                {
                    suggestedName = GalleryPhotoAnalyzer.CreateSuggestedName(caption.Text, Path.GetExtension(fileName), "foto-");
                    namePreview.Text = suggestedName ?? "Z popisku nelze vytvořit název.";
                    dialog.IsPrimaryButtonEnabled = suggestedName is not null &&
                        !string.Equals(fileName, suggestedName, StringComparison.OrdinalIgnoreCase);
                    if (suggestedName is not null && !dialog.IsPrimaryButtonEnabled)
                    {
                        phase.Text = "Kopie už má navržený název.";
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                phase.Text = "Generování bylo zrušeno.";
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Lokální generování popisku selhalo.");
                phase.Text = "Popisek se nepodařilo vygenerovat.";
                result.Header = "Podrobnosti chyby";
                result.Text = exception.Message;
            }
            finally
            {
                ring.IsActive = false;
                dialog.CloseButtonText = "Zavřít";
            }
        }
        return await ShowCancelableDialogAsync(dialog, RunAsync, () => suggestedName, cancellationToken);
    }

    public Task<IReadOnlyList<SmartRenameProposal>?> ConfirmBatchSmartNamesAsync(
        IReadOnlyList<GalleryFile> photos, Func<GalleryFile, CancellationToken, Task<string>> getReadPath,
        CancellationToken cancellationToken)
    {
        var phase = new TextBlock { Text = "Připravuji návrhy…", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Minimum = 0, Maximum = photos.Count };
        var preview = new TextBox
        {
            Header = "Původní → nový název", IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true, MinHeight = 80, MaxHeight = 240
        };
        ScrollViewer.SetVerticalScrollBarVisibility(preview, ScrollBarVisibility.Auto);
        var content = new StackPanel { Spacing = 10, MaxWidth = 500 };
        content.Children.Add(new TextBlock
        {
            Text = "Vzor: RRRRMMDD_popis-z-ai.přípona. Po potvrzení se přejmenují jen kopie fotografií. Videa a originály se nemění.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(phase);
        content.Children.Add(progress);
        content.Children.Add(preview);
        var dialog = new ContentDialog
        {
            Title = "Hromadné chytré přejmenování", Content = content, PrimaryButtonText = "Přejmenovat",
            CloseButtonText = "Zastavit a zavřít", IsPrimaryButtonEnabled = false, DefaultButton = ContentDialogButton.Close
        };
        var proposals = new List<SmartRenameProposal>();
        async Task RunAsync(CancellationToken token)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = new StringBuilder();
            var processed = 0;
            try
            {
                foreach (var photo in photos)
                {
                    token.ThrowIfCancellationRequested();
                    phase.Text = $"Lokální AI: {processed + 1}/{photos.Count} · {photo.Name}";
                    try
                    {
                        var path = await getReadPath(photo, token);
                        var (dateTaken, _) = await mediaDates.ReadAsync(path, photo.Kind, token);
                        var date = dateTaken ?? (photo.LastWriteTimeUtc.Year > 1601 ? photo.LastWriteTimeUtc.ToLocalTime() : (DateTimeOffset?)null);
                        var prefix = (date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "bez-data") + "_";
                        var caption = await generator.GenerateAsync(path, token);
                        token.ThrowIfCancellationRequested();
                        var name = GalleryPhotoAnalyzer.CreateSuggestedName(caption.Text, Path.GetExtension(photo.Name), prefix);
                        var result = name is null ? "Přeskočeno: AI neposkytla použitelný název." :
                            string.Equals(name, photo.Name, StringComparison.OrdinalIgnoreCase) ? "Beze změny." :
                            !names.Add(Path.Combine(photo.Folder, name)) ? "Přeskočeno: stejný návrh v této složce." : null;
                        if (result is null && name is not null)
                        {
                            proposals.Add(new SmartRenameProposal(photo, name));
                        }
                        lines.AppendLine($"{photo.RelativePath} → {result ?? name}");
                    }
                    catch (Exception exception) when (!(exception is OperationCanceledException && token.IsCancellationRequested))
                    {
                        logger.LogWarning(exception, "Návrh názvu pro {File} selhal.", photo.RelativePath);
                        lines.AppendLine($"{photo.RelativePath} → Přeskočeno: návrh se nezdařil.");
                    }
                    progress.Value = ++processed;
                }
                token.ThrowIfCancellationRequested();
                preview.Text = lines.ToString();
                phase.Text = $"Návrhy: {proposals.Count} · vynecháno: {photos.Count - proposals.Count}. Soubory zatím nebyly změněny.";
                dialog.CloseButtonText = "Zavřít";
                dialog.IsPrimaryButtonEnabled = proposals.Count > 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        return ShowCancelableDialogAsync<IReadOnlyList<SmartRenameProposal>>(
            dialog, RunAsync, () => proposals, cancellationToken);
    }

    private async Task<TResult?> ShowCancelableDialogAsync<TResult>(ContentDialog dialog,
        Func<CancellationToken, Task> run, Func<TResult?> result, CancellationToken cancellationToken) where TResult : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stop is not null)
        {
            throw new InvalidOperationException("Generování jiného popisku již běží.");
        }
        var window = getWindow();
        dialog.XamlRoot = window.Content.XamlRoot;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var isOpen = false;
        _stop = stop;
        _dialog = dialog;
        dialog.Opened += (_, _) =>
        {
            isOpen = true;
            _operation = run(stop.Token);
        };
        dialog.Closed += (_, _) =>
        {
            isOpen = false;
            stop.Cancel();
        };
        using var cancellation = cancellationToken.Register(() =>
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                if (isOpen) { dialog.Hide(); }
            });
        });
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary && !cancellationToken.IsCancellationRequested
                ? result() : null;
        }
        finally
        {
            isOpen = false;
            stop.Cancel();
            try
            {
                if (_operation is not null) { await _operation; }
            }
            finally
            {
                _operation = null;
                _stop = null;
                _dialog = null;
            }
        }
    }
}
