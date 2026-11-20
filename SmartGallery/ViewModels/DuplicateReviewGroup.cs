using System.Collections.ObjectModel;
using SmartGallery.Application.Catalog;

namespace SmartGallery.ViewModels;

public sealed class DuplicateReviewGroup(PhotoDuplicateSelection selection, int number) :
    ReadOnlyCollection<DuplicateReviewTile>(selection.Group.Copies
        .Select(file => new DuplicateReviewTile(file, file.Id == selection.Keep.Id)).ToArray())
{
    public string Label => $"Skupina {number} · kopií: {Count}";
}

public sealed record DuplicateReviewTile(GalleryFile File, bool Keep)
{
    public string Status => Keep ? "Ponechat" : "Do koše";
    public string AutomationLabel => $"{Status}: {File.RelativePath}";
}
