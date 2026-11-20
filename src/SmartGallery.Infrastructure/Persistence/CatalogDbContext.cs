using Microsoft.EntityFrameworkCore;
using SmartGallery.Domain;

namespace SmartGallery.Infrastructure.Persistence;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Library> Libraries => Set<Library>();

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<AssetFile> AssetFiles => Set<AssetFile>();

    internal DbSet<PhotoAnalysisRecord> PhotoAnalyses => Set<PhotoAnalysisRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var libraries = modelBuilder.Entity<Library>();
        libraries.ToTable("Libraries");
        libraries.HasKey(library => library.Id);
        libraries.Property(library => library.Id).ValueGeneratedNever();
        libraries.Property(library => library.Name).IsRequired();
        libraries.Property(library => library.RootPath).IsRequired();
        libraries.Property(library => library.FolderLayout)
            .HasConversion<string>()
            .HasDefaultValue(LibraryFolderLayout.YearMonth);
        libraries.Property(library => library.FileNaming)
            .HasConversion<string>()
            .HasDefaultValue(LibraryFileNaming.KeepOriginal);
        libraries.Property(library => library.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        libraries.HasIndex(library => library.RootPath);

        var assets = modelBuilder.Entity<Asset>();
        assets.ToTable("Assets");
        assets.HasKey(asset => asset.Id);
        assets.Property(asset => asset.Id).ValueGeneratedNever();
        assets.Property(asset => asset.ImportedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        assets.HasOne<Library>()
            .WithMany()
            .HasForeignKey(asset => asset.LibraryId)
            .OnDelete(DeleteBehavior.Restrict);

        var files = modelBuilder.Entity<AssetFile>();
        files.ToTable("AssetFiles");
        files.HasKey(file => file.Id);
        files.Property(file => file.Id).ValueGeneratedNever();
        files.Property(file => file.RelativePath).IsRequired();
        files.Property(file => file.LastWriteTimeUtc)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        files.HasIndex(file => new { file.AssetId, file.RelativePath }).IsUnique();
        files.HasOne<Asset>()
            .WithMany()
            .HasForeignKey(file => file.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        var analyses = modelBuilder.Entity<PhotoAnalysisRecord>();
        analyses.ToTable("PhotoAnalyses");
        analyses.HasKey(analysis => analysis.FileId);
        analyses.Property(analysis => analysis.FileId).ValueGeneratedNever();
        analyses.HasOne<AssetFile>().WithOne()
            .HasForeignKey<PhotoAnalysisRecord>(analysis => analysis.FileId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
