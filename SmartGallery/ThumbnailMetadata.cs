using System.Globalization;

namespace SmartGallery;

public static class ThumbnailMetadata
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("cs-CZ");

    public static string FormatExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length > 0 ? extension[1..].ToUpperInvariant() : "—";
    }

    public static string FormatSize(long bytes)
    {
        var (divisor, unit) = bytes switch
        {
            >= 1_000_000_000_000 => (1_000_000_000_000d, "TB"),
            >= 1_000_000_000 => (1_000_000_000d, "GB"),
            >= 100_000 => (1_000_000d, "MB"),
            >= 1_000 => (1_000d, "kB"),
            _ => (1d, "B")
        };
        return $"{(bytes / divisor).ToString("F1", DisplayCulture)} {unit}";
    }
}
