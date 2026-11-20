using System.Security.Cryptography;
using System.Text.Json;
using SmartGallery.Application.AI;

namespace SmartGallery.AI.Models;

internal sealed record VerifiedModelFile(
    string Path, long Bytes, long CreationTicks, long WriteTicks, string Sha256);

internal sealed record CaptionVerificationRecord(
    int Version, string ManifestHash, string Root, List<VerifiedModelFile> Files,
    string? RuntimeContract = null, CaptionRuntimeValidation? Runtime = null);

public sealed class CaptionModelVerificationCache : IDisposable
{
    private const string ContractVersion = "florence-fp32-cpu-v1";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CaptionVerificationRecord? _record;
    private CaptionModelManifest? _manifest;
    private string CachePath => Path.Combine(CaptionModelStorage.Directory, ".smartgallery-verified.json");

    internal async Task<bool> IsCurrentAsync(CaptionModelFile file, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var current = ReadStamp(file);
            return current is not null && _record!.Files.FirstOrDefault(entry => entry.Path == file.Path) == current;
        }
        finally { _gate.Release(); }
    }

    internal async Task RememberAsync(CaptionModelFile file, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            var stamp = ReadStamp(file) ?? throw new IOException("Nelze zaznamenat ověřený soubor.");
            var previous = _record!.Files.FirstOrDefault(entry => entry.Path == file.Path);
            if (previous == stamp)
            {
                return;
            }
            _record.Files.RemoveAll(entry => entry.Path == file.Path);
            _record.Files.Add(stamp);
            _record = _record with { Runtime = null, RuntimeContract = null };
            await SaveAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    internal async Task<CaptionRuntimeValidation?> GetRuntimeAsync(
        string runtimeVersion, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            if (_record!.RuntimeContract != ContractVersion || _record.Runtime is not { } runtime ||
                runtime.RuntimeVersion != runtimeVersion || runtime.Graphs is null || runtime.Graphs.Count != 4)
            {
                return null;
            }
            foreach (var file in _manifest!.Files)
            {
                var stamp = ReadStamp(file);
                if (stamp is null || _record.Files.FirstOrDefault(entry => entry.Path == file.Path) != stamp)
                {
                    return null;
                }
            }
            return runtime;
        }
        finally { _gate.Release(); }
    }

    internal async Task RememberRuntimeAsync(CaptionRuntimeValidation result, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            _record = _record! with { RuntimeContract = ContractVersion, Runtime = result };
            await SaveAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    internal async Task ForgetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(cancellationToken);
            _record!.Files.Clear();
            _record = _record with { Runtime = null, RuntimeContract = null };
            await SaveAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_record is not null)
        {
            return;
        }
        using var resource = typeof(CaptionModelVerificationCache).Assembly
            .GetManifestResourceStream("SmartGallery.AI.ModelManifest.json")
            ?? throw new InvalidOperationException("Chybí manifest modelu.");
        using var bytes = new MemoryStream();
        await resource.CopyToAsync(bytes, cancellationToken);
        var manifestHash = Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
        bytes.Position = 0;
        _manifest = await JsonSerializer.DeserializeAsync(
            bytes, CaptionModelManifestJsonContext.Default.CaptionModelManifest, cancellationToken)
            ?? throw new InvalidDataException("Prázdný manifest.");
        CaptionVerificationRecord? saved = null;
        try
        {
            var info = new FileInfo(CachePath);
            if (info.Exists && info.Length <= 256 * 1024)
            {
                VerifyParents(CachePath);
                await using var stream = File.OpenRead(CachePath);
                saved = await JsonSerializer.DeserializeAsync(
                    stream, CaptionModelManifestJsonContext.Default.CaptionVerificationRecord, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged application cache is not proof of model integrity; verify from scratch.
        }
        _record = saved is { Version: 1, Files: not null } && saved.Files.Count <= 32 &&
            saved.Files.All(file => file is not null && !string.IsNullOrWhiteSpace(file.Path)) &&
            saved.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() == saved.Files.Count &&
            saved.ManifestHash == manifestHash && saved.Root == CaptionModelStorage.Directory
            ? saved : new CaptionVerificationRecord(1, manifestHash, CaptionModelStorage.Directory, []);
        if (_record.Runtime is { } runtime &&
            (runtime.Graphs is null || runtime.Graphs.Any(graph => graph is null ||
                graph.Inputs is null || graph.Outputs is null ||
                graph.Inputs.Concat(graph.Outputs).Any(tensor => tensor is null || tensor.Dimensions is null))))
        {
            _record = _record with { Runtime = null, RuntimeContract = null };
        }
    }

    private static VerifiedModelFile? ReadStamp(CaptionModelFile file)
    {
        try
        {
            var path = Path.Combine(CaptionModelStorage.Directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            VerifyParents(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Bytes)
            {
                return null;
            }
            return new VerifiedModelFile(file.Path, info.Length,
                info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks, file.Sha256);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void VerifyParents(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Záznam nebo soubor modelu nesmí být odkaz.");
        }
        for (DirectoryInfo? parent = new(Path.GetDirectoryName(path)!);
             parent is not null; parent = parent.Parent)
        {
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Cesta modelu obsahuje odkaz.");
            }
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveCoreAsync(cancellationToken);
        }
        catch
        {
            // Never reuse an in-memory success that failed to reach the persistent record.
            _record = null;
            _manifest = null;
            throw;
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        // This is an application-owned cache, never an original photograph or model weight.
        VerifyParents(CachePath);
        Directory.CreateDirectory(CaptionModelStorage.Directory);
        VerifyParents(CachePath);
        var temporary = CachePath + $".{Guid.NewGuid():N}.tmp";
        var created = false;
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                created = true;
                await JsonSerializer.SerializeAsync(
                    stream, _record!, CaptionModelManifestJsonContext.Default.CaptionVerificationRecord, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            VerifyParents(CachePath);
            File.Move(temporary, CachePath, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                File.Delete(temporary);
            }
        }
    }

    public void Dispose() => _gate.Dispose();
}
