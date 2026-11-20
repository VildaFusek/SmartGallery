using System.Security.Cryptography;
using System.Text.Json;
using SmartGallery.Application.AI;

namespace SmartGallery.AI.Models;

public sealed class LocalCaptionModelInspector(CaptionModelVerificationCache cache) : ILocalCaptionModelInspector
{
    public string ModelDirectory => CaptionModelStorage.Directory;

    public Task<CaptionModelInspection> InspectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => InspectCoreAsync(cancellationToken), cancellationToken);

    private async Task<CaptionModelInspection> InspectCoreAsync(CancellationToken cancellationToken)
    {
        using var manifestStream = typeof(LocalCaptionModelInspector).Assembly
            .GetManifestResourceStream("SmartGallery.AI.ModelManifest.json")
            ?? throw new InvalidOperationException("Chybí vestavěný manifest modelu.");
        var manifest = await JsonSerializer.DeserializeAsync(
            manifestStream, CaptionModelManifestJsonContext.Default.CaptionModelManifest, cancellationToken)
            ?? throw new InvalidDataException("Manifest modelu je prázdný.");
        if (manifest.SchemaVersion != 1 || manifest.ModelId != "florence-2-base-ft-fp32" ||
            manifest.Files is null || manifest.Files.Count is 0 or > 32)
        {
            throw new InvalidDataException("Manifest modelu má nepodporovaný formát.");
        }

        var root = Path.GetFullPath(ModelDirectory);
        var issues = new List<string>();
        var verified = 0;
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.Path) || file.Path.Contains('\\') ||
                file.Path.Contains(':') || file.Path.Split('/').Any(part => part is "" or "." or "..") ||
                file.Bytes <= 0 || file.Sha256 is null || file.Sha256.Length != 64 ||
                !file.Sha256.All(Uri.IsHexDigit) || !uniquePaths.Add(file.Path))
            {
                throw new InvalidDataException("Manifest obsahuje neplatnou položku.");
            }

            var fullPath = Path.GetFullPath(Path.Combine(
                root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Soubor manifestu je mimo složku modelu.");
            }
            try
            {
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new IOException("Očekáván běžný soubor, nikoli adresář nebo odkaz.");
                }
                for (var parent = new DirectoryInfo(Path.GetDirectoryName(fullPath)!);
                     parent is not null; parent = parent.Parent)
                {
                    if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("Cesta modelu obsahuje reparse point.");
                    }
                }
                await using var stream = new FileStream(fullPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                if (stream.Length != file.Bytes)
                {
                    issues.Add($"{file.Path}: nesouhlasí velikost ({stream.Length} místo {file.Bytes} B).");
                    continue;
                }
                var hash = await SHA256.HashDataAsync(stream, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(file.Sha256)))
                {
                    issues.Add($"{file.Path}: nesouhlasí SHA-256.");
                    continue;
                }
                verified++;
                await cache.RememberAsync(file, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                issues.Add($"{file.Path}: soubor chybí.");
            }
            catch (DirectoryNotFoundException)
            {
                issues.Add($"{file.Path}: složka/soubor chybí.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add($"{file.Path}: {exception.Message}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (issues.Count > 0)
        {
            await cache.ForgetAsync(cancellationToken);
        }
        return new CaptionModelInspection(root, manifest.Files.Count, verified, issues.AsReadOnly());
    }
}
