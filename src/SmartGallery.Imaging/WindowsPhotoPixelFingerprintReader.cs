using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SmartGallery.Application.Catalog;
using Windows.Graphics.Imaging;

namespace SmartGallery.Imaging;

public sealed class WindowsPhotoPixelFingerprintReader : IPhotoPixelFingerprintReader
{
    public async Task<PhotoPixelFingerprint> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var bitmapStream = stream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(bitmapStream).AsTask(cancellationToken);
            var width = decoder.OrientedPixelWidth;
            var height = decoder.OrientedPixelHeight;
            var pixelCount = (ulong)width * height;
            if (decoder.FrameCount != 1 || pixelCount is 0 or > 32_000_000)
            {
                throw new InvalidDataException("Podporovány jsou pouze jednosnímkové fotografie do 32 Mpx.");
            }

            var provider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb)
                .AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var pixels = provider.DetachPixelData();
            if ((ulong)pixels.Length != pixelCount * 4)
            {
                throw new InvalidDataException("Dekodér vrátil neočekávaný počet pixelů.");
            }
            return new PhotoPixelFingerprint(width, height, Convert.ToHexString(SHA256.HashData(pixels)));
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("Windows nedokázal dekódovat obsah fotografie.", exception);
        }
    }
}
