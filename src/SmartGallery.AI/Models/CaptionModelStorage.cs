namespace SmartGallery.AI.Models;

internal static class CaptionModelStorage
{
    internal static string Directory
    {
        get
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
            {
                throw new InvalidOperationException("Nelze určit místní složku modelu.");
            }
            return Path.Combine(localData, "SmartGallery", "models", "florence-2-base-ft-fp32");
        }
    }
}
