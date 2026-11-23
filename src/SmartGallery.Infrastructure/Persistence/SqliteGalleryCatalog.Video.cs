using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using SmartGallery.Infrastructure.Importing;

namespace SmartGallery.Infrastructure.Persistence;

public sealed partial class SqliteGalleryCatalog
{
    public Task<string> AddConvertedVideoAsync(Library library, GalleryFile source, string temporaryVideo,
        CancellationToken cancellationToken) =>
        PublishConvertedMediaAsync(library, source, temporaryVideo, MediaKind.Video, false, cancellationToken);

    public Task<string> ReplaceConvertedVideoAsync(Library library, GalleryFile source, string temporaryVideo,
        CancellationToken cancellationToken) =>
        PublishConvertedMediaAsync(library, source, temporaryVideo, MediaKind.Video, true, cancellationToken);

    public Task<string> PublishConvertedPhotoAsync(Library library, GalleryFile source, string temporaryPhoto,
        bool replaceOriginal, CancellationToken cancellationToken) =>
        PublishConvertedMediaAsync(library, source, temporaryPhoto, MediaKind.Photo, replaceOriginal, cancellationToken);

    private Task<string> PublishConvertedMediaAsync(Library library, GalleryFile source, string temporaryVideo,
        MediaKind kind, bool replaceOriginal, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var paths = await GetMutationPathsAsync(context, library, cancellationToken);
        var original = await FilesForLibrary(context, library.Id)
            .SingleOrDefaultAsync(file => file.Id == source.Id, cancellationToken);
        if (original is null || original.RelativePath != source.RelativePath || source.Kind != kind ||
            FolderImportPreviewService.GetMediaKind(Path.GetExtension(original.RelativePath)) != kind)
        {
            throw new IOException("Zdrojový soubor už není v knihovně.");
        }
        var input = ResolvePath(library.RootPath, original.RelativePath);
        InspectFile(input);
        var work = Path.GetDirectoryName(temporaryVideo)!;
        var temporaryName = Path.GetFileName(temporaryVideo);
        var prefix = kind == MediaKind.Photo ? ".smartgallery-photo-" : ".smartgallery-video-";
        var workName = Path.GetFileName(work);
        if (!string.Equals(Path.GetDirectoryName(work), Path.GetDirectoryName(input), StringComparison.OrdinalIgnoreCase) ||
            !workName.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(workName[prefix.Length..], "N", out _) ||
            (kind == MediaKind.Photo ? temporaryName is not ("photo.jpg" or "photo.png" or "photo.webp") :
                temporaryName is not ("video.mp4" or "video.mkv" or "remux.mkv" or "video.webm" or "video.mov")))
        {
            throw new IOException("Dočasný soubor převodu neleží v očekávaném umístění.");
        }
        InspectFile(temporaryVideo);
        if (replaceOriginal)
        {
            if (original.SizeBytes != source.SizeBytes || original.LastWriteTimeUtc != source.LastWriteTimeUtc)
            {
                throw new IOException("Zdrojový soubor se od zápisu do katalogu změnil; aplikace ho nenahradí.");
            }
            if (await FilesForLibrary(context, library.Id).AnyAsync(file => file.Id != original.Id &&
                EF.Functions.Collate(file.RelativePath, "NOCASE") == original.RelativePath, cancellationToken))
            {
                throw new IOException("Na zdrojový soubor odkazuje více záznamů katalogu.");
            }
            var extension = Path.GetExtension(temporaryName);
            var originalExtension = Path.GetExtension(original.RelativePath);
            var sameExtension = string.Equals(originalExtension, extension, StringComparison.OrdinalIgnoreCase) ||
                (extension == ".jpg" && originalExtension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase));
            var relativeOutput = sameExtension ? original.RelativePath : Path.ChangeExtension(original.RelativePath, extension);
            var output = ResolvePath(library.RootPath, relativeOutput);
            if (!sameExtension && (paths.Contains(relativeOutput) || File.Exists(output) || Directory.Exists(output)))
            {
                throw new IOException($"Název {relativeOutput} už existuje. Žádný soubor nebyl nahrazen.");
            }
            if (kind == MediaKind.Photo)
            {
                await context.PhotoAnalyses.Where(item => item.FileId == original.Id).ExecuteDeleteAsync(cancellationToken);
            }
            await ReplaceMediaFileAsync(context, transaction, original, input, relativeOutput, output,
                temporaryVideo, sameExtension, cancellationToken);
            return relativeOutput;
        }
        File.SetLastWriteTimeUtc(temporaryVideo, File.GetLastWriteTimeUtc(input));
        var info = new FileInfo(temporaryVideo);
        var outputSuffix = temporaryName switch
        {
            "photo.png" => "_PNG.png",
            "photo.webp" => "_WebP.webp",
            "photo.jpg" => "_JPEG.jpg",
            "video.mkv" => "_Archiv.mkv",
            "remux.mkv" => "_MKV.mkv",
            "video.webm" => "_WebM.webm",
            "video.mov" => "_MOV.mov",
            _ => "_MP4.mp4"
        };
        var outputStem = Path.GetFileNameWithoutExtension(outputSuffix);
        var outputExtension = Path.GetExtension(outputSuffix);
        var stem = Path.GetFileNameWithoutExtension(source.Name);
        // Leave room for the suffix on filesystems with 255 UTF-16 units per name.
        if (stem.Length > 220)
        {
            stem = stem[..(char.IsHighSurrogate(stem[219]) ? 219 : 220)];
        }
        string relative;
        string destination;
        var suffix = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = suffix == 0 ? stem + outputSuffix : $"{stem}{outputStem}_{suffix}{outputExtension}";
            relative = Path.Combine(source.Folder, name);
            destination = ResolvePath(library.RootPath, relative);
            suffix++;
        }
        while (paths.Contains(relative) || File.Exists(destination) || Directory.Exists(destination));

        var asset = new Asset(Guid.NewGuid(), library.Id, DateTimeOffset.UtcNow);
        context.Assets.Add(asset);
        context.AssetFiles.Add(new AssetFile(Guid.NewGuid(), asset.Id, relative, info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc)));
        await context.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        InspectDirectoryChain(Path.GetDirectoryName(destination)!);
        InspectFile(temporaryVideo);
        File.Move(temporaryVideo, destination, overwrite: false);
        try
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            // A commit failure can be ambiguous; retain the verified copy instead of deleting it.
            throw new IOException($"Katalog nepotvrdil zápis. Výsledek zůstal v souboru {destination}.", exception);
        }
        return relative;
    }, cancellationToken);

    private static async Task ReplaceMediaFileAsync(CatalogDbContext context,
        IDbContextTransaction transaction, AssetFile original, string input,
        string relativeOutput, string output, string temporaryVideo, bool sameExtension, CancellationToken token)
    {
        File.SetLastWriteTimeUtc(temporaryVideo, original.LastWriteTimeUtc.UtcDateTime);
        var info = new FileInfo(temporaryVideo);
        var snapshot = original with { };
        var entry = context.Entry(original);
        entry.Property(file => file.RelativePath).CurrentValue = relativeOutput;
        entry.Property(file => file.SizeBytes).CurrentValue = info.Length;
        entry.Property(file => file.LastWriteTimeUtc).CurrentValue = new DateTimeOffset(info.LastWriteTimeUtc);
        await context.SaveChangesAsync(token);

        token.ThrowIfCancellationRequested();
        InspectFile(temporaryVideo);
        RequireUnchanged(input, snapshot);
        var backup = Path.Combine(Path.GetDirectoryName(temporaryVideo)!, "puvodni" + Path.GetExtension(input));
        if (sameExtension)
        {
            try
            {
                File.Replace(temporaryVideo, input, backup);
            }
            catch (IOException exception) when (File.Exists(backup))
            {
                throw new IOException($"Výměna souboru selhala. Původní kopie je v souboru {backup}.", exception);
            }
        }
        else
        {
            File.Move(input, backup, overwrite: false);
            try
            {
                File.Move(temporaryVideo, output, overwrite: false);
            }
            catch (Exception operationError)
            {
                try { File.Move(backup, input, overwrite: false); }
                catch (Exception recoveryError)
                {
                    throw new IOException($"Původní kopii se nepodařilo vrátit. Zůstala v souboru {backup}.",
                        new AggregateException(operationError, recoveryError));
                }
                throw;
            }
        }
        try
        {
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new IOException($"Katalog nepotvrdil náhradu. Původní kopie: {backup}. Výstup převodu: {output}.", exception);
        }
        try
        {
            InspectFile(backup);
            File.Delete(backup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Nelze odstranit zálohu nahrazeného videa {0}: {1}", backup, exception.Message);
        }
    }
}
