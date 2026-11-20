using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SmartGallery.Application.AI;

namespace SmartGallery.AI.Models;

public sealed class LocalModelProvisioner(
    HttpClient httpClient, ILocalCaptionModelInspector inspector,
    CaptionModelVerificationCache cache) : ILocalModelProvisioner
{
    public Task PrepareAsync(IProgress<ModelPreparationSnapshot> progress, CancellationToken cancellationToken) =>
        Task.Run(() => PrepareCoreAsync(progress, cancellationToken), cancellationToken);

    private async Task PrepareCoreAsync(
        IProgress<ModelPreparationSnapshot> progress, CancellationToken cancellationToken)
    {
        using var resource = typeof(LocalModelProvisioner).Assembly
            .GetManifestResourceStream("SmartGallery.AI.ModelManifest.json")
            ?? throw new InvalidOperationException("Chybí manifest modelu.");
        var manifest = await JsonSerializer.DeserializeAsync(
            resource, CaptionModelManifestJsonContext.Default.CaptionModelManifest, cancellationToken)
            ?? throw new InvalidDataException("Prázdný manifest modelu.");
        if (manifest.SchemaVersion != 1 || manifest.ModelId != "florence-2-base-ft-fp32" ||
            manifest.Repository != "onnx-community/Florence-2-base-ft" ||
            manifest.Revision != "e88a44eaf3791a35eae0c5a47b3dbcd36e67eb6f" ||
            manifest.Files is null || manifest.Files.Count != 14)
        {
            throw new InvalidDataException("Nepodporovaný manifest modelu.");
        }

        var root = Path.GetFullPath(inspector.ModelDirectory);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            _ = ResolvePath(root, file);
            if (!unique.Add(file.Path))
            {
                throw new InvalidDataException("Duplicitní soubor manifestu.");
            }
        }

        var total = manifest.Files.Sum(file => file.Bytes);
        long completed = 0;
        var verified = 0;
        void Report(ModelPreparationPhase phase, string message, long currentBytes = 0) =>
            progress.Report(new ModelPreparationSnapshot(
                phase, message, verified, manifest.Files.Count, completed + currentBytes, total));

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolvePath(root, file);
            Report(ModelPreparationPhase.Verifying, $"Kontroluji záznam {file.Path}");
            if (!await cache.IsCurrentAsync(file, cancellationToken) &&
                !await VerifyExistingAsync(path, file, cancellationToken))
            {
                var url = file.Url ?? $"https://huggingface.co/{manifest.Repository}/resolve/{manifest.Revision}/{file.Path}";
                var uri = new Uri(url, UriKind.Absolute);
                if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "huggingface.co")
                {
                    throw new InvalidDataException("Nepovolený zdroj modelu.");
                }
                await DownloadAsync(path, file, uri,
                    bytes => Report(ModelPreparationPhase.Downloading, $"Stahuji {file.Path}", bytes),
                    () => Report(ModelPreparationPhase.Verifying, $"Ověřuji stažený {file.Path}", file.Bytes),
                    cancellationToken);
            }
            await cache.RememberAsync(file, cancellationToken);
            verified++;
            completed += file.Bytes;
        }
        Report(ModelPreparationPhase.FilesVerified,
            "Florence-2: soubory ověřeny. AI inference ještě není zapojena.");
    }

    private static string ResolvePath(string root, CaptionModelFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Path) || file.Path.Contains('\\') || file.Path.Contains(':') ||
            file.Path.Split('/').Any(part => part is "" or "." or "..") || file.Bytes <= 0 ||
            file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Neplatná položka manifestu.");
        }
        var path = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Soubor modelu je mimo místní složku.");
        }
        return path;
    }

    private static void VerifyDirectoryChain(string directory, bool allowMissing)
    {
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            try
            {
                var attributes = File.GetAttributes(current.FullName);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) == 0)
                {
                    throw new IOException("Cesta modelu obsahuje odkaz nebo blokující soubor.");
                }
            }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }
    }

    private static async Task<bool> VerifyExistingAsync(
        string path, CaptionModelFile file, CancellationToken cancellationToken)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new IOException("Soubor modelu není běžný soubor.");
        }
        VerifyDirectoryChain(Path.GetDirectoryName(path)!, allowMissing: false);
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        if (stream.Length != file.Bytes ||
            !CryptographicOperations.FixedTimeEquals(
                await SHA256.HashDataAsync(stream, cancellationToken), Convert.FromHexString(file.Sha256)))
        {
            throw new InvalidDataException(
                $"Soubor {file.Path} neodpovídá manifestu. Existující soubor nebyl přepsán; zkontroluj jej ručně.");
        }
        return true;
    }

    private async Task DownloadAsync(
        string destination, CaptionModelFile file, Uri uri, Action<long> report,
        Action verifying, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(destination)!;
        VerifyDirectoryChain(parent, allowMissing: true);
        Directory.CreateDirectory(parent);
        VerifyDirectoryChain(parent, allowMissing: false);
        var temporary = destination + $".{Guid.NewGuid():N}.download";
        var ownsTemporary = false;
        try
        {
            report(0);
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps ||
                (response.Content.Headers.ContentLength is { } length && length != file.Bytes))
            {
                throw new InvalidDataException("Stažení nemá očekávaný protokol nebo velikost.");
            }
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }))
            {
                ownsTemporary = true;
                var buffer = new byte[128 * 1024];
                long received = 0;
                var timer = Stopwatch.StartNew();
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
                {
                    received += read;
                    if (received > file.Bytes)
                    {
                        throw new InvalidDataException("Server posílá více dat, než povoluje manifest.");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (timer.ElapsedMilliseconds >= 200)
                    {
                        report(received);
                        timer.Restart();
                    }
                }
                if (received != file.Bytes)
                {
                    throw new InvalidDataException("Stažení je neúplné.");
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
                report(received);
            }
            verifying();
            if (!await VerifyExistingAsync(temporary, file, cancellationToken))
            {
                throw new IOException("Stažený dočasný soubor není dostupný.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            VerifyDirectoryChain(parent, allowMissing: false);
            File.Move(temporary, destination, overwrite: false);
            ownsTemporary = false;
        }
        finally
        {
            if (ownsTemporary)
            {
                VerifyDirectoryChain(parent, allowMissing: false);
                File.Delete(temporary);
            }
        }
    }
}
