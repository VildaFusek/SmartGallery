using SmartGallery.Application.AI;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace SmartGallery.Imaging;

public sealed class WindowsCaptionImagePreprocessor : ICaptionImagePreprocessor
{
    public async Task<CaptionImageTensor> PrepareAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 100_000_000 ||
            decoder.PixelWidth == 0 || decoder.PixelHeight == 0)
        {
            throw new InvalidDataException("Fotografie je příliš velká nebo nemá platné rozměry.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var transform = new BitmapTransform
        {
            ScaledWidth = 768, ScaledHeight = 768, InterpolationMode = BitmapInterpolationMode.Cubic
        };
        var provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        cancellationToken.ThrowIfCancellationRequested();
        var rgba = provider.DetachPixelData();
        const int plane = 768 * 768;
        if (rgba.Length != plane * 4)
        {
            throw new InvalidDataException("Dekodér vrátil neočekávaný počet pixelů.");
        }
        var chw = new float[3 * plane];
        for (var index = 0; index < plane; index++)
        {
            if (index % 8192 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            chw[index] = (rgba[4 * index] / 255f - 0.485f) / 0.229f;
            chw[plane + index] = (rgba[4 * index + 1] / 255f - 0.456f) / 0.224f;
            chw[2 * plane + index] = (rgba[4 * index + 2] / 255f - 0.406f) / 0.225f;
        }
        return new CaptionImageTensor(chw, 768, 768);
    }
}
