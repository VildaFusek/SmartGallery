using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartGallery.Application.Importing;
using SmartGallery.Domain;
using SmartGallery.Infrastructure.Persistence;

namespace SmartGallery.Infrastructure.Importing;

public sealed class FolderImportService(
    IImportPreviewService planner,
    IDbContextFactory<CatalogDbContext> contextFactory,
    ILogger<FolderImportService> logger) : IFolderImportService
{
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    public Task<FolderImportResult> ImportAsync(
        Guid libraryId, string sourceDirectory, IProgress<ImportOperationProgress> progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => ImportCoreAsync(libraryId, sourceDirectory, progress, cancellationToken), cancellationToken);

    private async Task<FolderImportResult> ImportCoreAsync(
        Guid libraryId, string sourceDirectory, IProgress<ImportOperationProgress> progress,
        CancellationToken cancellationToken)
    {
        long imported = 0;
        long failed = 0;
        long skipped = 0;
        long uncataloged = 0;
        long discovered = 0;
        var stopped = false;
        var reportInterval = Stopwatch.StartNew();
        void Report(string phase, string? message = null, long bytes = 0, long total = 0, bool force = false)
        {
            if (!force && reportInterval.ElapsedMilliseconds < 100)
            {
                return;
            }
            reportInterval.Restart();
            progress.Report(new ImportOperationProgress(
                phase, ProgressCount(discovered), ProgressCount(imported), ProgressCount(failed),
                ProgressCount(skipped), bytes, total, message));
        }

        Report("Analýza", "Načítám zdroje a metadata. Originály zůstanou beze změny.", force: true);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var library = await context.Libraries.AsNoTracking().SingleAsync(
            candidate => candidate.Id == libraryId, cancellationToken);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(library.RootPath));
        EnsureDirectoryChain(root, root, create: false);

        using var spool = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = "", Pooling = false }.ToString());
        var plan = new FolderImportPlan(spool, root);
        var (media, warning) = await plan.BuildAsync(planner, library, sourceDirectory, (inspected, found, unsupported) =>
        {
            discovered = found;
            Report("Analýza", $"Prohlédnuto: {inspected}; ostatní: {unsupported}.");
        }, cancellationToken);
        discovered = media;
        if (warning is not null)
        {
            Report("Zastaveno", warning, force: true);
            return new FolderImportResult(0, 0, 0, 0, false);
        }

        var current = await context.Libraries.AsNoTracking().SingleAsync(
            candidate => candidate.Id == libraryId, cancellationToken);
        if (current.RootPath != library.RootPath || current.FolderLayout != library.FolderLayout ||
            current.FileNaming != library.FileNaming)
        {
            throw new InvalidOperationException("Během analýzy se změnila knihovna nebo její pravidla. Spusť import znovu.");
        }

        Report("Kopírování", "Analýza dokončena, začínám kopírovat.", force: true);
        foreach (var item in plan.ReadItems(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.State == ImportPreviewState.Blocked || item.SizeBytes is null || item.LastWriteTimeUtc is null)
            {
                skipped++;
                Report("Kopírování", $"Vynecháno: {item.SourcePath}. {item.Note}");
                continue;
            }

            string? publishedPath = null;
            try
            {
                var date = item.OrganizationDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                    ?? "bez data";
                Report("Kopírování", $"{Path.GetFileName(item.SourcePath)} — {date} · {item.DateSource}");
                publishedPath = await CopyVerifiedAsync(
                    item, root, plan,
                    (bytes, total) => Report("Kopírování", bytes: bytes, total: total),
                    cancellationToken);

                // Once published, finish the short catalog transaction even if the user cancels.
                await RegisterCopyAsync(library, publishedPath, item.SizeBytes.Value, item.LastWriteTimeUtc.Value);
                imported++;
                Report("Kopírování", $"Uloženo a ověřeno SHA-256: {publishedPath}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                             DbUpdateException or DbException or InvalidOperationException)
            {
                failed++;
                logger.LogError(exception, "Import souboru {SourcePath} selhal.", item.SourcePath);
                if (publishedPath is not null)
                {
                    uncataloged++;
                    Report("Kopírování",
                        $"Kopie zůstala na disku, ale není v katalogu: {publishedPath}. {exception.Message}", force: true);
                    stopped = true;
                    break;
                }
                if (exception is IOException { HResult: DiskFull or HandleDiskFull })
                {
                    Report("Zastaveno", $"Na disku došlo místo: {item.SourcePath}. {exception.Message}", force: true);
                    stopped = true;
                    break;
                }
                Report("Kopírování", $"Chyba: {item.SourcePath}. {exception.Message}");
            }
        }

        Report(stopped ? "Zastaveno" : "Dokončeno",
            $"Importováno: {imported}; chyby: {failed}; vynecháno: {skipped}; nezařazené kopie: {uncataloged}.", force: true);
        return new FolderImportResult(
            ProgressCount(imported), ProgressCount(failed), ProgressCount(skipped), ProgressCount(uncataloged), !stopped);
    }

    private static int ProgressCount(long count) => (int)Math.Min(count, int.MaxValue);

    private async Task RegisterCopyAsync(
        Library expected, string fullPath, long size, DateTimeOffset modified)
    {
        await using var context = await contextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, CancellationToken.None);
        var current = await context.Libraries.AsNoTracking().SingleAsync(
            candidate => candidate.Id == expected.Id);
        if (current.RootPath != expected.RootPath || current.FolderLayout != expected.FolderLayout ||
            current.FileNaming != expected.FileNaming)
        {
            throw new InvalidOperationException("Během kopírování se změnila knihovna nebo její pravidla.");
        }
        var asset = new Asset(Guid.NewGuid(), expected.Id, DateTimeOffset.UtcNow);
        var file = new AssetFile(Guid.NewGuid(), asset.Id,
            Path.GetRelativePath(expected.RootPath, fullPath), size, modified);
        context.Assets.Add(asset);
        context.AssetFiles.Add(file);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task<string> CopyVerifiedAsync(
        ImportPreviewItem item, string root, FolderImportPlan plan,
        Action<long, long> report, CancellationToken cancellationToken)
    {
        RequireWithin(item.DestinationPath, root);
        var parent = Path.GetDirectoryName(item.DestinationPath)!;
        EnsureDirectoryChain(parent, root, create: true);
        RejectSourceReparsePoints(item.SourcePath);
        await using var source = new FileStream(item.SourcePath, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        if (source.Length != item.SizeBytes ||
            File.GetLastWriteTimeUtc(item.SourcePath).Ticks != item.LastWriteTimeUtc!.Value.UtcTicks)
        {
            throw new IOException("Zdroj se od analýzy změnil; importuj jej znovu.");
        }

        var temporary = Path.Combine(parent, $".sg-import-{Guid.NewGuid():N}.tmp");
        var ownsTemporary = false;
        try
        {
            await using (var output = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }))
            {
                ownsTemporary = true;
                using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
                long copied = 0;
                var timer = Stopwatch.StartNew();
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
                    {
                        sourceHash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        copied += read;
                        if (timer.ElapsedMilliseconds >= 200)
                        {
                            report(copied, source.Length);
                            timer.Restart();
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
                await output.FlushAsync(cancellationToken);
                output.Position = 0;
                var copiedHash = await SHA256.HashDataAsync(output, cancellationToken);
                if (copied != source.Length ||
                    !CryptographicOperations.FixedTimeEquals(sourceHash.GetHashAndReset(), copiedHash))
                {
                    throw new IOException("Kontrola délky nebo SHA-256 kopie selhala.");
                }
                output.Flush(flushToDisk: true);
                report(copied, source.Length);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDirectoryChain(parent, root, create: false);
            File.SetLastWriteTimeUtc(temporary, item.LastWriteTimeUtc!.Value.UtcDateTime);
            var stem = Path.GetFileNameWithoutExtension(item.DestinationPath);
            var extension = Path.GetExtension(item.DestinationPath);
            const int numberedAttempts = 16;
            for (var attempt = 0; ; attempt = Math.Min(attempt + 1, numberedAttempts))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = attempt switch
                {
                    0 => Path.GetFileName(item.DestinationPath),
                    < numberedAttempts => $"{stem}_{attempt:D4}{extension}",
                    _ => MakeUniqueName(stem, extension)
                };
                if (name.Length > 255)
                {
                    name = MakeUniqueName(stem, extension);
                }
                var candidate = Path.Combine(parent, name);
                if (File.Exists(candidate) || Directory.Exists(candidate) || plan.IsReservedDirectory(candidate))
                {
                    continue;
                }
                try
                {
                    File.Move(temporary, candidate, overwrite: false);
                    ownsTemporary = false;
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    // Another importer reserved this name; never overwrite it.
                }
            }
        }
        finally
        {
            if (ownsTemporary)
            {
                // Only this operation's freshly created temporary file may be removed.
                EnsureDirectoryChain(parent, root, create: false);
                File.Delete(temporary);
            }
        }
    }

    private static string MakeUniqueName(string stem, string extension)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return stem.Length + 1 + suffix.Length + extension.Length <= 255
            ? $"{stem}_{suffix}{extension}"
            : suffix + extension;
    }

    internal static void RequireWithin(string path, string root)
    {
        var full = Path.GetFullPath(path);
        if (!IsChild(full, root))
        {
            throw new IOException("Cíl není uvnitř spravované knihovny.");
        }
    }

    private static void EnsureDirectoryChain(string directory, string root, bool create)
    {
        var ancestors = new Stack<string>();
        for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
        {
            ancestors.Push(current.FullName);
        }
        while (ancestors.TryPop(out var path))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) == 0)
                {
                    throw new IOException("Cílová cesta obsahuje odkaz nebo blokující soubor.");
                }
            }
            catch (DirectoryNotFoundException) when (create && IsChild(path, root))
            {
                Directory.CreateDirectory(path);
                EnsureDirectoryChain(path, root, create: false);
            }
            catch (FileNotFoundException) when (create && IsChild(path, root))
            {
                Directory.CreateDirectory(path);
                EnsureDirectoryChain(path, root, create: false);
            }
        }
    }

    private static bool IsChild(string path, string root) =>
        path.Length > root.Length &&
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void RejectSourceReparsePoints(string file)
    {
        if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new IOException("Zdroj není běžný soubor.");
        }
        for (var current = new DirectoryInfo(Path.GetDirectoryName(file)!); current is not null; current = current.Parent)
        {
            if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Zdrojová cesta obsahuje odkaz.");
            }
        }
    }
}
