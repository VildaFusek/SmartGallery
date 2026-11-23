using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SmartGallery.Application.Catalog;

namespace SmartGallery;

public sealed partial class MainPage
{
    private CancellationTokenSource? _fileRenameCancellation;
    private Task? _fileRenameOperation;

    private async void OnFileNameClick(object sender, RoutedEventArgs e)
    {
        var file = ReferenceEquals(sender, VideoSelectionTitle)
            ? VideoSelectionPicker.SelectedItem as GalleryFile
            : (sender as FrameworkElement)?.DataContext as GalleryFile;
        if (!_mediaUiActive || !ViewModel.CanChangeFilters || _videoSelectionOperation is not null ||
            _fileRenameCancellation is not null || file is null)
        {
            return;
        }

        var extension = Path.GetExtension(file.Name);
        var input = new TextBox
        {
            Header = $"Nový název (přípona {extension} zůstane)",
            Text = Path.GetFileNameWithoutExtension(file.Name)
        };
        var dialog = new ContentDialog
        {
            Title = "Přejmenovat kopii v knihovně",
            Content = input,
            PrimaryButtonText = "Přejmenovat",
            CloseButtonText = "Storno",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Opened += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        if (await ShowConfirmedDialogAsync(dialog))
        {
            var name = input.Text.Trim() + extension;
            if (name != file.Name)
            {
                using var request = new CancellationTokenSource();
                _fileRenameCancellation = request;
                try
                {
                    _fileRenameOperation = RenameAsync();
                    await _fileRenameOperation;
                }
                finally
                {
                    _fileRenameOperation = null;
                    _fileRenameCancellation = null;
                }

                async Task RenameAsync()
                {
                    if (_tileVideoFile?.Id == file.Id)
                    {
                        StopTileVideo();
                    }
                    if (VideoSelectionPicker.SelectedItem is GalleryFile selected && selected.Id == file.Id)
                    {
                        _videoSelectionProbe?.Cancel();
                        await (_videoSelectionProbeTask ?? Task.CompletedTask);
                    }
                    if (_mediaUiActive && !request.IsCancellationRequested)
                    {
                        await ViewModel.RenameFileAsync(file, name, request.Token);
                        if (_mediaUiActive && _selectedVideoInfo is null && VideoSelectionPicker.SelectedItem is not null)
                        {
                            RefreshSelectedVideo();
                        }
                    }
                }
            }
        }
    }
}
