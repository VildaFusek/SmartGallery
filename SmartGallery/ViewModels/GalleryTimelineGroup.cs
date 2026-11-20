using System.Collections.ObjectModel;
using System.Globalization;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;

namespace SmartGallery.ViewModels;

internal enum GalleryAnalysisFilter { All, Analyzed, NotAnalyzed, PendingName }

public sealed class GalleryTimelineGroup : ReadOnlyCollection<GalleryFile>
{
    private static readonly CultureInfo Czech = CultureInfo.GetCultureInfo("cs-CZ");

    private GalleryTimelineGroup(string yearKey, string yearLabel, string sectionPath, string sectionLabel,
        bool startsYear, IList<GalleryFile> files) : base(files)
    {
        YearKey = yearKey;
        YearLabel = yearLabel;
        SectionPath = sectionPath;
        SectionLabel = sectionLabel;
        StartsYear = startsYear;
    }

    public string YearKey { get; }
    public string YearLabel { get; }
    public string SectionPath { get; }
    public string SectionLabel { get; }
    public bool StartsYear { get; }

    internal static IReadOnlyList<GalleryTimelineGroup> Build(
        IReadOnlyList<GalleryFile> files, CancellationToken cancellationToken)
    {
        var dated = files.Select(file =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = GetTimelineDate(file);
            var section = file.Kind == MediaKind.Video ? 0 : date?.Month ?? 13;
            return (File: file, Date: date, Year: date?.Year ?? 0, Section: section);
        });

        var groups = new List<GalleryTimelineGroup>();
        var previousYear = -1;
        foreach (var group in dated.GroupBy(item => (item.Year, item.Section))
                     .OrderByDescending(group => group.Key.Year).ThenByDescending(group => group.Key.Section))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (year, section) = group.Key;
            var yearKey = year == 0 ? "BezData" : year.ToString("D4", CultureInfo.InvariantCulture);
            var sectionKey = section switch
            {
                0 => "Video",
                13 => "Photo",
                _ => section.ToString("D2", CultureInfo.InvariantCulture)
            };
            var label = section switch
            {
                0 => "Videa",
                13 => "Fotografie",
                _ => Czech.TextInfo.ToTitleCase(Czech.DateTimeFormat.GetMonthName(section))
            };
            var orderedFiles = group.OrderByDescending(item => item.Date)
                .ThenBy(item => item.File.RelativePath, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => item.File).ToArray();
            groups.Add(new GalleryTimelineGroup(yearKey, year == 0 ? "Bez data" : yearKey,
                Path.Combine(yearKey, sectionKey), label, year != previousYear, orderedFiles));
            previousYear = year;
        }
        return groups;
    }

    internal static (IReadOnlyList<GalleryTimelineGroup> Groups, IReadOnlyList<GalleryFile> Files) Filter(
        IReadOnlyList<GalleryTimelineGroup> timeline, string search, MediaKind? kind,
        GalleryAnalysisFilter analysisFilter, CancellationToken cancellationToken)
    {
        var terms = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var groups = new List<GalleryTimelineGroup>();
        var files = new List<GalleryFile>();
        string? previousYear = null;
        foreach (var group in timeline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kind is not null && group[0].Kind != kind)
            {
                continue;
            }
            IList<GalleryFile> matches = terms.Length == 0 && analysisFilter == GalleryAnalysisFilter.All ? group : group.Where(file =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matchesState = analysisFilter switch
                {
                    GalleryAnalysisFilter.Analyzed => file.Analysis is not null,
                    GalleryAnalysisFilter.NotAnalyzed => file is { Kind: MediaKind.Photo, Analysis: null },
                    GalleryAnalysisFilter.PendingName => file.HasPendingName,
                    _ => true
                };
                return matchesState && terms.All(term =>
                    Contains(file.RelativePath, term) || Contains(file.Analysis?.Caption, term) ||
                    Contains(file.Analysis?.SuggestedName, term) || Contains(group.YearLabel, term) ||
                    Contains(group.SectionLabel, term));
            }).ToArray();
            if (matches.Count > 0)
            {
                var startsYear = previousYear != group.YearKey;
                groups.Add(ReferenceEquals(group, matches) && startsYear == group.StartsYear ? group :
                    new GalleryTimelineGroup(group.YearKey, group.YearLabel, group.SectionPath,
                        group.SectionLabel, startsYear, matches));
                files.AddRange(matches);
                previousYear = group.YearKey;
            }
        }
        return (groups, files);
    }

    private static bool Contains(string? text, string term) => text is not null &&
        Czech.CompareInfo.IndexOf(text, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private static DateOnly? GetTimelineDate(GalleryFile file)
    {
        var modified = file.LastWriteTimeUtc.Year > 1601
            ? DateOnly.FromDateTime(file.LastWriteTimeUtc.LocalDateTime) : (DateOnly?)null;
        var parts = file.Folder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (parts is ["BezData"] || file.Kind == MediaKind.Video && parts is ["BezData", "Video"])
        {
            return null;
        }
        if (parts is [var yearText, var period] && yearText.Length == 4 &&
            int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
            year is >= 1 and <= 9999)
        {
            if (file.Kind == MediaKind.Video && period == "Video")
            {
                return modified is { } date && date.Year == year ? date : new DateOnly(year, 1, 1);
            }
            if (file.Kind == MediaKind.Photo)
            {
                if (DateOnly.TryParseExact(period, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var day) && day.Year == year)
                {
                    return day;
                }
                if (period.Length == 2 && int.TryParse(period, NumberStyles.None, CultureInfo.InvariantCulture, out var month)
                    && month is >= 1 and <= 12)
                {
                    return modified is { } date && date.Year == year && date.Month == month
                        ? date : new DateOnly(year, month, 1);
                }
            }
        }
        return modified;
    }
}
