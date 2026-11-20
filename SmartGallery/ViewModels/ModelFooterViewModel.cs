using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using SmartGallery.Application.AI;
using SmartGallery.Services;

namespace SmartGallery.ViewModels;

public sealed class ModelFooterViewModel : ObservableObject, IDisposable
{
    private readonly ILocalModelPreparation _preparation;
    private readonly DispatcherQueue _dispatcher;
    private ModelPreparationSnapshot _snapshot;
    private bool _disposed;

    public ModelFooterViewModel(ILocalModelPreparation preparation, AppReadinessState modules)
    {
        _preparation = preparation;
        Modules = modules;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Stavová lišta musí vzniknout na UI threadu.");
        _snapshot = preparation.Current;
        RetryCommand = new RelayCommand(preparation.Retry, () => !IsActive);
        CancelCommand = new RelayCommand(preparation.Cancel, () => IsActive);
        _preparation.Changed += OnPreparationChanged;
        ApplySnapshot();
    }

    public AppReadinessState Modules { get; }

    public IRelayCommand RetryCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public string Message => _snapshot.Message;

    public string RuntimeStatus => _snapshot.Phase switch
    {
        ModelPreparationPhase.RuntimeLoading => "ONNX: ověřování na CPU",
        ModelPreparationPhase.RuntimeValidated => "AI: lokální popisky na CPU",
        ModelPreparationPhase.RuntimeFailed => "ONNX: chyba načtení",
        _ => "AI: čeká na runtime"
    };

    public bool IsActive => _snapshot.Phase is ModelPreparationPhase.Verifying or
        ModelPreparationPhase.Downloading or ModelPreparationPhase.RuntimeLoading;

    public bool IsIndeterminate => _snapshot.Phase is ModelPreparationPhase.NotStarted or
        ModelPreparationPhase.Verifying or ModelPreparationPhase.RuntimeLoading;

    public double Progress => _snapshot.TotalGraphs > 0
        ? Math.Clamp(100d * _snapshot.ValidatedGraphs / _snapshot.TotalGraphs, 0, 100)
        : _snapshot.TotalBytes > 0
            ? Math.Clamp(100d * _snapshot.CompletedBytes / _snapshot.TotalBytes, 0, 100) : 0;

    public string Counts => _snapshot.TotalGraphs > 0
        ? $"{_snapshot.ValidatedGraphs}/{_snapshot.TotalGraphs} grafů · {_snapshot.VerifiedFiles}/{_snapshot.TotalFiles} souborů"
        : _snapshot.TotalFiles > 0
        ? $"{_snapshot.VerifiedFiles}/{_snapshot.TotalFiles} · {_snapshot.CompletedBytes / 1048576d:F0}/{_snapshot.TotalBytes / 1048576d:F0} MiB"
        : "";

    private void OnPreparationChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.HasThreadAccess)
        {
            ApplySnapshot();
        }
        else
        {
            _dispatcher.TryEnqueue(ApplySnapshot);
        }
    }

    private void ApplySnapshot()
    {
        if (_disposed)
        {
            return;
        }
        _snapshot = _preparation.Current;
        Modules.CaptioningReady = _snapshot.Phase == ModelPreparationPhase.RuntimeValidated;
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(Counts));
        OnPropertyChanged(nameof(RuntimeStatus));
        RetryCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        _preparation.Changed -= OnPreparationChanged;
    }
}
