using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartGallery.Application.AI;

namespace SmartGallery.Worker;

public sealed class ModelPreparationWorker(
    ILocalModelProvisioner provisioner,
    ICaptionRuntimeValidator runtime,
    ILogger<ModelPreparationWorker> logger)
    : BackgroundService, ILocalModelPreparation
{
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(1);
    private readonly object _gate = new();
    private CancellationTokenSource? _attempt;
    private ModelPreparationSnapshot _current = new(ModelPreparationPhase.NotStarted, "Model: čeká na přípravu");

    public ModelPreparationSnapshot Current => Volatile.Read(ref _current);

    public event EventHandler? Changed;

    public void Retry()
    {
        lock (_gate)
        {
            if (_attempt is null)
            {
                _requests.Writer.TryWrite(true);
            }
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            while (_requests.Reader.TryRead(out _))
            {
            }
            _attempt?.Cancel();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _requests.Writer.TryWrite(true);
        try
        {
            while (await _requests.Reader.WaitToReadAsync(stoppingToken))
            {
                if (!_requests.Reader.TryRead(out _))
                {
                    continue;
                }
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (_gate)
                {
                    _attempt = attempt;
                }
                try
                {
                    SetState(new ModelPreparationSnapshot(ModelPreparationPhase.Verifying, "Ověřuji lokální model…"));
                    await provisioner.PrepareAsync(new PreparationProgress(SetState), attempt.Token);
                    attempt.Token.ThrowIfCancellationRequested();
                    SetState(Current with
                    {
                        Phase = ModelPreparationPhase.RuntimeLoading,
                        Message = "Načítám ONNX Runtime a ověřuji grafy Florence-2 na CPU…",
                        ValidatedGraphs = 0,
                        TotalGraphs = 4
                    });
                    var validation = await runtime.ValidateAsync(
                        new RuntimeProgress(value => SetState(Current with
                        {
                            Phase = ModelPreparationPhase.RuntimeLoading,
                            Message = $"CPU ONNX: {value.GraphName}",
                            ValidatedGraphs = value.ValidatedGraphs,
                            TotalGraphs = value.TotalGraphs
                        })), attempt.Token);
                    attempt.Token.ThrowIfCancellationRequested();
                    logger.LogInformation("ONNX Runtime {Version}, CPU: {Source}, {Count} grafů Florence-2.",
                        validation.RuntimeVersion,
                        validation.ReusedVerification ? "použit uložený záznam" : "provedeno nové ověření",
                        validation.Graphs.Count);
                    foreach (var graph in validation.ReusedVerification
                        ? Enumerable.Empty<CaptionGraphSignature>() : validation.Graphs)
                    {
                        logger.LogInformation("Graf {Graph}: {Inputs} vstupů, {Outputs} výstupů.",
                            graph.FileName, graph.Inputs.Count, graph.Outputs.Count);
                        foreach (var tensor in graph.Inputs)
                        {
                            logger.LogDebug("{Graph} vstup {Name}: {Type} [{Shape}].",
                                graph.FileName, tensor.Name, tensor.ElementType, string.Join(", ", tensor.Dimensions));
                        }
                        foreach (var tensor in graph.Outputs)
                        {
                            logger.LogDebug("{Graph} výstup {Name}: {Type} [{Shape}].",
                                graph.FileName, tensor.Name, tensor.ElementType, string.Join(", ", tensor.Dimensions));
                        }
                    }
                    SetState(Current with
                    {
                        Phase = ModelPreparationPhase.RuntimeValidated,
                        Message = validation.ReusedVerification
                            ? $"ONNX {validation.RuntimeVersion}: použit uložený záznam ověření; popisky dostupné."
                            : $"ONNX {validation.RuntimeVersion}: 4/4 grafy ověřeny a zaznamenány; popisky dostupné.",
                        ValidatedGraphs = validation.Graphs.Count,
                        RuntimeVersion = validation.RuntimeVersion
                    });
                }
                catch (OperationCanceledException) when (attempt.IsCancellationRequested)
                {
                    SetState(Current with { Phase = ModelPreparationPhase.Canceled, Message = "Příprava modelu zastavena." });
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Příprava lokálního modelu selhala; galerie zůstává dostupná.");
                    SetState(Current with
                    {
                        Phase = Current.Phase == ModelPreparationPhase.RuntimeLoading
                            ? ModelPreparationPhase.RuntimeFailed : ModelPreparationPhase.Failed,
                        Message = $"Příprava modelu/ONNX selhala: {exception.Message}"
                    });
                }
                finally
                {
                    lock (_gate)
                    {
                        _attempt = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private void SetState(ModelPreparationSnapshot state)
    {
        Volatile.Write(ref _current, state);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class PreparationProgress(Action<ModelPreparationSnapshot> report) : IProgress<ModelPreparationSnapshot>
    {
        public void Report(ModelPreparationSnapshot value) => report(value);
    }

    private sealed class RuntimeProgress(Action<CaptionRuntimeProgress> report) : IProgress<CaptionRuntimeProgress>
    {
        public void Report(CaptionRuntimeProgress value) => report(value);
    }
}
