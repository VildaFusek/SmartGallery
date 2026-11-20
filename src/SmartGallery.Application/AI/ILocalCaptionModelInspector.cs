namespace SmartGallery.Application.AI;

public sealed record CaptionModelInspection(
    string Directory,
    int ExpectedFiles,
    int VerifiedFiles,
    IReadOnlyList<string> Issues)
{
    public bool FilesVerified => ExpectedFiles > 0 && VerifiedFiles == ExpectedFiles && Issues.Count == 0;
}

public interface ILocalCaptionModelInspector
{
    string ModelDirectory { get; }

    Task<CaptionModelInspection> InspectAsync(CancellationToken cancellationToken);
}
