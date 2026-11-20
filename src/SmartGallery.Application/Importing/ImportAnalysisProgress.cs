namespace SmartGallery.Application.Importing;

public sealed record ImportAnalysisProgress(int InspectedEntries, int MediaFiles, int UnsupportedFiles);
