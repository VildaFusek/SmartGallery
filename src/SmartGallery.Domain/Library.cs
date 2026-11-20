namespace SmartGallery.Domain;

public sealed record Library(Guid Id, string Name, string RootPath, DateTimeOffset CreatedAt)
{
    public LibraryFolderLayout FolderLayout { get; init; } = LibraryFolderLayout.YearMonth;

    public LibraryFileNaming FileNaming { get; init; } = LibraryFileNaming.KeepOriginal;
}
