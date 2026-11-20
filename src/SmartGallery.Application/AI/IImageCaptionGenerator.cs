namespace SmartGallery.Application.AI;

public sealed record ImageCaption(string Text, bool Truncated);

public sealed record CaptionImageTensor(float[] Pixels, int Width, int Height);

public interface ICaptionImagePreprocessor
{
    Task<CaptionImageTensor> PrepareAsync(string path, CancellationToken cancellationToken);
}

public interface IImageCaptionGenerator
{
    Task<ImageCaption> GenerateAsync(string imagePath, CancellationToken cancellationToken);
}
