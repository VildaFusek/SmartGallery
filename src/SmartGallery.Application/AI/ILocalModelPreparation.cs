namespace SmartGallery.Application.AI;

public enum ModelPreparationPhase
{
    NotStarted,
    Verifying,
    Downloading,
    FilesVerified,
    Failed,
    Canceled,
    RuntimeLoading,
    RuntimeValidated,
    RuntimeFailed
}

public sealed record ModelPreparationSnapshot(
    ModelPreparationPhase Phase,
    string Message,
    int VerifiedFiles = 0,
    int TotalFiles = 0,
    long CompletedBytes = 0,
    long TotalBytes = 0)
{
    public int ValidatedGraphs { get; init; }

    public int TotalGraphs { get; init; }

    public string? RuntimeVersion { get; init; }
}

public interface ILocalModelProvisioner
{
    Task PrepareAsync(IProgress<ModelPreparationSnapshot> progress, CancellationToken cancellationToken);
}

public interface ILocalModelPreparation
{
    ModelPreparationSnapshot Current { get; }

    event EventHandler? Changed;

    void Retry();

    void Cancel();
}
