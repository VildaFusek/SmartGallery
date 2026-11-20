namespace SmartGallery.Application.AI;

public sealed record CaptionTensorSignature(
    string Name, string ElementType, IReadOnlyList<int> Dimensions);

public sealed record CaptionGraphSignature(
    string FileName,
    IReadOnlyList<CaptionTensorSignature> Inputs,
    IReadOnlyList<CaptionTensorSignature> Outputs);

public sealed record CaptionRuntimeValidation(
    string RuntimeVersion, IReadOnlyList<CaptionGraphSignature> Graphs)
{
    public bool ReusedVerification { get; init; }
}

public sealed record CaptionRuntimeProgress(string GraphName, int ValidatedGraphs, int TotalGraphs);

public interface ICaptionRuntimeValidator
{
    Task<CaptionRuntimeValidation> ValidateAsync(
        IProgress<CaptionRuntimeProgress> progress, CancellationToken cancellationToken);
}
