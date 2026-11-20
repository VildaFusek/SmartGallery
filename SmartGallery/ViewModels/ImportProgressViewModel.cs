using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using SmartGallery.Application.Importing;

namespace SmartGallery.ViewModels;

public sealed class ImportProgressViewModel : ObservableObject
{
    private readonly Queue<string> _log = new();
    private string _phase = "Příprava analýzy…";
    private string _statistics = "Prohlédnuto: 0 | média: 0 | ostatní: 0";
    private string _logText = "";
    private bool _isRunning;
    private double _filePercent;

    public double FilePercent
    {
        get => _filePercent;
        private set => SetProperty(ref _filePercent, value);
    }

    public string Phase
    {
        get => _phase;
        private set => SetProperty(ref _phase, value);
    }

    public string Statistics
    {
        get => _statistics;
        private set => SetProperty(ref _statistics, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public async Task AnalyzeAsync(
        IImportPreviewService analysis,
        ILogger logger,
        Guid libraryId,
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        IsRunning = true;
        Phase = "Analyzuji zdrojovou složku a podadresáře…";
        AppendLog($"Zdroj: {sourceDirectory}");
        AppendLog("Pouze příprava importu. Žádné kopírování ani změny originálů.");
        AppendLog("Datum se čte z metadat. Pokud chybí, použije se čas poslední změny nebo vytvoření souboru.");
        var progress = new Progress<ImportAnalysisProgress>(value =>
        {
            if (IsRunning && !cancellationToken.IsCancellationRequested)
            {
                Statistics = $"Prohlédnuto: {value.InspectedEntries} | média: {value.MediaFiles} | ostatní: {value.UnsupportedFiles}";
            }
        });

        try
        {
            var result = await analysis.PreviewFolderAsync(
                libraryId, sourceDirectory, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var conflicts = result.Items.Count(item => item.State == ImportPreviewState.Conflict);
            var blocked = result.Items.Count(item => item.State == ImportPreviewState.Blocked);
            Statistics = $"Prohlédnuto: {result.InspectedEntries} | média: {result.Items.Count} | ostatní: {result.UnsupportedFiles}\n" +
                $"Kolize: {conflicts} | blokováno: {blocked} | zkopírováno: 0";
            Phase = result.IsComplete
                ? "Samostatná analýza dokončena. Tato diagnostická akce nekopírovala."
                : "Analýza je neúplná. Kopírování neproběhlo.";
            AppendLog($"Použitá uložená pravidla: {result.FolderLayout}, {result.FileNaming}.");
            if (result.Warning is not null)
            {
                AppendLog(result.Warning);
            }
            foreach (var item in result.Items.Where(item => item.State != ImportPreviewState.Proposed))
            {
                AppendLog($"{item.SourcePath}: {item.Note}");
            }
            AppendLog("Konec přípravy. Zdroj ani cílová složka nebyly změněny.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Phase = "Analýza byla zrušena. Nic nebylo zkopírováno.";
            AppendLog(Phase);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Analýza zdrojové složky selhala.");
            Phase = "Analýza selhala. Podrobnosti jsou v logu.";
            AppendLog(exception.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public async Task ImportAsync(
        IFolderImportService importer, ILogger logger, Guid libraryId,
        string sourceDirectory, CancellationToken cancellationToken)
    {
        IsRunning = true;
        Phase = "Analýza zdrojů a metadat…";
        AppendLog($"Zdroj: {sourceDirectory}");
        AppendLog("Pouze kopírování. Kolize dostanou jedinečnou příponu, existující soubory se nepřepisují.");
        var progress = new Progress<ImportOperationProgress>(value =>
        {
            if (!IsRunning)
            {
                return;
            }
            Phase = value.Phase;
            Statistics = $"Nalezeno: {value.Discovered} | importováno: {value.Imported}\n" +
                $"Chyby: {value.Failed} | vynecháno: {value.Skipped}";
            FilePercent = value.FileTotalBytes > 0
                ? Math.Clamp(100d * value.FileBytes / value.FileTotalBytes, 0, 100) : 0;
            if (value.Message is not null)
            {
                AppendLog(value.Message);
            }
        });
        try
        {
            var result = await importer.ImportAsync(libraryId, sourceDirectory, progress, cancellationToken);
            Statistics = $"Importováno: {result.Imported} | chyby: {result.Failed} | vynecháno: {result.Skipped}\n" +
                $"Kopie bez záznamu v katalogu: {result.Uncataloged}";
            Phase = !result.IsComplete ? "Import nebyl dokončen. Podrobnosti jsou v logu." :
                result.Failed > 0 || result.Skipped > 0 ? "Import dokončen s upozorněními." : "Import dokončen.";
            FilePercent = result.IsComplete ? 100 : 0;
            AppendLog(Phase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Phase = "Import byl přerušen. Již dokončené kopie zůstávají zachovány.";
            AppendLog(Phase);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Import selhal.");
            Phase = "Import selhal. Dokončené kopie zůstávají zachovány; podrobnosti jsou v logu.";
            AppendLog(exception.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void AppendLog(string message)
    {
        _log.Enqueue(message);
        while (_log.Count > 80)
        {
            _log.Dequeue();
        }
        LogText = string.Join(Environment.NewLine, _log);
    }
}
