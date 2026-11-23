using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SmartGallery.Infrastructure.Video;

public sealed record VideoProgress(string Message, double? Percent = null)
{
    public TimeSpan? Processed { get; init; }
    public string? FileDetail { get; init; }
    public double? FilePercent { get; init; }
}

public sealed record FfmpegInstallation(string Directory)
{
    public string Ffmpeg => Path.Combine(Directory, "ffmpeg.exe");
    public string Ffprobe => Path.Combine(Directory, "ffprobe.exe");
    public string Ffplay => Path.Combine(Directory, "ffplay.exe");
}

public sealed class FfmpegTools
{
    private const string Source = "https://www.gyan.dev/ffmpeg/builds/";
    private const string Package = "ffmpeg-release-essentials.zip";
    private const long MaxArchive = 512L * 1024 * 1024;
    private const long MaxExpanded = 2L * 1024 * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private static readonly string[] Executables = ["ffmpeg.exe", "ffprobe.exe", "ffplay.exe"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartGallery", "Tools", "FFmpeg");

    public static bool IsSupported => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

    public Task<FfmpegInstallation?> LoadAsync(CancellationToken token) => Task.Run(async () =>
    {
        var pointer = Path.Combine(_root, "current.txt");
        InspectPath(pointer);
        if (!File.Exists(pointer))
        {
            return null;
        }
        var id = await File.ReadAllTextAsync(pointer, token);
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new InvalidDataException("Ukazatel instalace FFmpeg je neplatný.");
        }
        var version = Path.Combine(_root, "versions", id);
        var manifest = Path.Combine(version, "sha256.txt");
        InspectPath(manifest);
        var hashes = await File.ReadAllLinesAsync(manifest, token);
        if (hashes.Length != Executables.Length)
        {
            throw new InvalidDataException("Chybí kontrolní součty místních nástrojů.");
        }
        for (var index = 0; index < Executables.Length; index++)
        {
            var path = Path.Combine(version, "bin", Executables[index]);
            InspectPath(path);
            if (!string.Equals(await HashAsync(path, token), hashes[index], StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Kontrolní součet místního FFmpeg nesouhlasí. Stáhněte novou instalaci.");
            }
        }
        return new FfmpegInstallation(Path.Combine(version, "bin"));
    }, token);

    public Task<FfmpegInstallation> InstallAsync(IProgress<VideoProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            if (!IsSupported)
            {
                throw new PlatformNotSupportedException("Stažení balíčku FFmpeg je dostupné pouze pro Windows x64.");
            }
            await _gate.WaitAsync(cancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(30));
                try
                {
                    return await InstallCoreAsync(progress, timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Vypršel časový limit instalace FFmpeg.");
                }
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken);

    private async Task<FfmpegInstallation> InstallCoreAsync(IProgress<VideoProgress> progress, CancellationToken token)
    {
        var versions = Path.Combine(_root, "versions");
        var lockPath = Path.Combine(_root, "setup.lock");
        InspectPath(versions);
        InspectPath(lockPath);
        Directory.CreateDirectory(versions);
        using var installationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var id = Guid.NewGuid().ToString("N");
        var work = Path.Combine(_root, "staging-" + id);
        var expanded = Path.Combine(work, "expanded");
        var target = Path.Combine(versions, id);
        var pointer = Path.Combine(_root, "current.txt");
        var pointerTemp = Path.Combine(work, "current.tmp");
        var activated = false;
        Directory.CreateDirectory(expanded);
        try
        {
            progress.Report(new("Zjišťuji stabilní verzi FFmpeg z Gyan.dev…"));
            var version = await ReadReleaseValueAsync(".ver", @"^\d+\.\d+(?:\.\d+)?$", token);
            var hash = await ReadReleaseValueAsync(".sha256", @"^[a-fA-F0-9]{64}$", token);
            var archive = Path.Combine(work, "package.zip");
            using (var response = await GetAsync(Package, token))
            {
                var total = response.Content.Headers.ContentLength;
                if (total is > MaxArchive)
                {
                    throw new InvalidDataException("Archiv FFmpeg je příliš velký.");
                }
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = NewFile(archive);
                var lastReport = Environment.TickCount64 - 150;
                await CopyBoundedAsync(input, output, MaxArchive, total, token, bytes =>
                {
                    if (Environment.TickCount64 - lastReport >= 150)
                    {
                        lastReport = Environment.TickCount64;
                        progress.Report(new($"Stahuji FFmpeg… {bytes / 1048576d:F1} MiB",
                            total is > 0 ? 100d * bytes / total.Value : null));
                    }
                });
            }
            progress.Report(new("Ověřuji SHA-256 staženého balíčku…"));
            if (!string.Equals(await HashAsync(archive, token), hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Stažený balíček neodpovídá SHA-256 zveřejněnému zdrojem.");
            }
            progress.Report(new("Bezpečně rozbaluji FFmpeg včetně licencí…"));
            var packageRoot = await ExtractAsync(archive, expanded, token);
            var hashes = new string[Executables.Length];
            for (var index = 0; index < Executables.Length; index++)
            {
                var path = Path.Combine(packageRoot, "bin", Executables[index]);
                hashes[index] = await HashAsync(path, token);
            }
            await File.WriteAllLinesAsync(Path.Combine(packageRoot, "sha256.txt"), hashes, token);
            Directory.Move(packageRoot, target);
            var installation = new FfmpegInstallation(Path.Combine(target, "bin"));
            progress.Report(new("Ověřuji spuštění všech tří nástrojů…"));
            foreach (var name in Executables)
            {
                var tool = Path.GetFileNameWithoutExtension(name);
                using var check = CancellationTokenSource.CreateLinkedTokenSource(token);
                check.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var output = await VideoToolProcess.RunAsync(Path.Combine(installation.Directory, name),
                        ["-version"], check.Token);
                    if (!Regex.IsMatch(output, $@"\A{tool} version {Regex.Escape(version)}(?:[-\s]|$)"))
                    {
                        throw new InvalidDataException($"Nesouhlasí verze nástroje {tool}.");
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException($"Nástroj {tool} včas neodpověděl.");
                }
            }

            await File.WriteAllTextAsync(pointerTemp, id, token);
            InspectPath(pointer);
            token.ThrowIfCancellationRequested();
            if (File.Exists(pointer))
            {
                File.Replace(pointerTemp, pointer, Path.Combine(_root, "previous-" + id + ".txt"));
            }
            else
            {
                File.Move(pointerTemp, pointer);
            }
            activated = true;
            return installation;
        }
        finally
        {
            Cleanup(work);
            if (!activated)
            {
                Cleanup(target);
            }
        }
    }

    private static async Task<string> ExtractAsync(string archive, string destination, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count is 0 or > 10000)
        {
            throw new InvalidDataException("Neplatný počet položek v archivu FFmpeg.");
        }
        string? root = null;
        long total = 0;
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            var parts = name.TrimEnd('/').Split('/');
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (parts.Any(part => part.Length == 0 || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    part.EndsWith('.') || part.EndsWith(' ') ||
                    Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³]|CONIN\$|CONOUT\$)(\.|$)",
                        RegexOptions.IgnoreCase)) ||
                unixType is not (0 or 0x8000 or 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Archiv obsahuje nepovolenou položku.");
            }
            root ??= parts[0];
            if (!parts[0].Equals(root, StringComparison.Ordinal) || (!directory && parts.Length < 2))
            {
                throw new InvalidDataException("Archiv nemá očekávanou adresářovou strukturu.");
            }
            var path = Path.Combine(destination, Path.Combine(parts));
            if (!entries.Add(path) || entry.Length > MaxExpanded - total || (directory && entry.Length != 0))
            {
                throw new InvalidDataException("Archiv obsahuje duplicitní položku nebo je příliš velký.");
            }
            total += entry.Length;
            if (directory)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = NewFile(path);
            await CopyBoundedAsync(input, output, entry.Length, entry.Length, token);
        }
        var packageRoot = Path.Combine(destination, root!);
        foreach (var name in Executables)
        {
            var matches = Directory.GetFiles(packageRoot, name, SearchOption.AllDirectories);
            if (matches.Length != 1 || !matches[0].Equals(Path.Combine(packageRoot, "bin", name),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Balíček neobsahuje očekávanou trojici FFmpeg, FFprobe a FFplay.");
            }
        }
        return packageRoot;
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, long? expected,
        CancellationToken token, Action<long>? progress = null)
    {
        var buffer = new byte[128 * 1024];
        long bytes = 0;
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            bytes += count;
            if (bytes > limit || bytes > expected)
            {
                throw new InvalidDataException("Velikost dat překročila povolený limit.");
            }
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            progress?.Invoke(bytes);
        }
        if (expected.HasValue && bytes != expected.Value)
        {
            throw new InvalidDataException("Přenesená data nejsou úplná.");
        }
    }

    private static async Task<string> ReadReleaseValueAsync(string suffix, string pattern, CancellationToken token)
    {
        using var response = await GetAsync(Package + suffix + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var buffer = new char[1025];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), token);
        var value = new string(buffer, 0, count).Trim();
        return count <= 1024 && Regex.IsMatch(value, pattern)
            ? value : throw new InvalidDataException("Zdroj vrátil neplatnou verzi nebo kontrolní součet.");
    }

    private static async Task<HttpResponseMessage> GetAsync(string resource, CancellationToken token)
    {
        var uri = new Uri(Source + resource);
        for (var redirects = 0; redirects < 8; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Host is not ("www.gyan.dev" or "gyan.dev" or "github.com" or
                    "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            {
                throw new IOException("Přesměrování mimo povolené HTTPS servery bylo zablokováno.");
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("SmartGallery/1.0");
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                using (response)
                {
                    uri = new Uri(uri, response.Headers.Location ?? throw new IOException("Chybí adresa přesměrování."));
                }
                continue;
            }
            try
            {
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        throw new IOException("Příliš mnoho přesměrování při stahování.");
    }

    private static FileStream NewFile(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    internal static void InspectPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Cesta obsahuje symbolický odkaz nebo junction: {current}");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void Cleanup(string directory)
    {
        try
        {
            InspectPath(directory);
            if (Directory.Exists(directory))
            {
                DeleteTree(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Nelze uklidit pracovní složku FFmpeg {0}: {1}", directory, exception.Message);
        }
    }

    private static void DeleteTree(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Pracovní složka obsahuje odkaz.");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteTree(path);
            }
            else
            {
                File.Delete(path);
            }
        }
        Directory.Delete(directory);
    }
}
