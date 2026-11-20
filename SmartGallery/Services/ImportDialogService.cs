using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SmartGallery.Application.Importing;
using SmartGallery.Views;

namespace SmartGallery.Services;

public sealed class ImportDialogService(
    Func<Window> getWindow,
    IFolderImportService importer,
    IImportPreviewService analysis,
    ILogger<ImportDialogService> logger) : IImportDialogService
{
    private CancellationTokenSource? _activeStop;
    private ContentDialog? _activeDialog;
    private Task? _activeOperation;

    public Task AnalyzeFolderAsync(Guid libraryId, string sourceDirectory, CancellationToken cancellationToken) =>
        ShowFolderAsync(libraryId, sourceDirectory, copyFiles: false, cancellationToken);

    public Task ImportFolderAsync(Guid libraryId, string sourceDirectory, CancellationToken cancellationToken) =>
        ShowFolderAsync(libraryId, sourceDirectory, copyFiles: true, cancellationToken);

    public async Task CancelAndWaitAsync()
    {
        _activeStop?.Cancel();
        _activeDialog?.Hide();
        var operation = _activeOperation;
        if (operation is not null)
        {
            await operation;
        }
    }

    private async Task ShowFolderAsync(
        Guid libraryId, string sourceDirectory, bool copyFiles, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_activeStop is not null)
        {
            throw new InvalidOperationException("Jiný import již probíhá.");
        }
        var window = getWindow();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var content = new ImportProgressView();
        var viewModel = content.ViewModel;
        var dialog = new ContentDialog
        {
            Title = copyFiles ? "Import fotografií a videí" : "Analýza importu",
            Content = content,
            CloseButtonText = "Zastavit a zavřít",
            XamlRoot = window.Content.XamlRoot
        };
        Task? operation = null;
        var isOpen = false;
        _activeStop = stop;
        _activeDialog = dialog;

        async Task RunAsync()
        {
            if (copyFiles)
            {
                await viewModel.ImportAsync(importer, logger, libraryId, sourceDirectory, stop.Token);
            }
            else
            {
                await viewModel.AnalyzeAsync(analysis, logger, libraryId, sourceDirectory, stop.Token);
            }
            if (isOpen)
            {
                dialog.CloseButtonText = "Zavřít";
            }
        }

        dialog.Opened += (_, _) =>
        {
            isOpen = true;
            operation = RunAsync();
            _activeOperation = operation;
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
                if (isOpen)
                {
                    dialog.Hide();
                }
            });
        });

        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            isOpen = false;
            stop.Cancel();
            try
            {
                if (operation is not null)
                {
                    await operation;
                }
            }
            finally
            {
                _activeStop = null;
                _activeDialog = null;
                _activeOperation = null;
            }
        }
    }
}
