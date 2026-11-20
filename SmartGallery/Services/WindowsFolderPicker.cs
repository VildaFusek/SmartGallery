using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace SmartGallery.Services;

public sealed class WindowsFolderPicker(Func<Window> getWindow) : IFolderPicker
{
    public async Task<string?> PickAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        picker.FileTypeFilter.Add("*");
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(getWindow());
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

        var folder = await picker.PickSingleFolderAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return folder?.Path;
    }
}
