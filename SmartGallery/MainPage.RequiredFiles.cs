using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SmartGallery.ViewModels;

namespace SmartGallery;

public sealed partial class MainPage
{
    private bool _mediaUiActive;

    private async void OnAiDownloadClick(object sender, RoutedEventArgs e)
    {
        if (FooterViewModel.RetryCommand.CanExecute(null) &&
            await ConfirmRequiredActionAsync("Připravit model AI?",
                "Povolíte stažení chybějících souborů Florence-2 z Hugging Face. Celý balíček má přibližně 1,09 GB. " +
                "Fotografie se nikam neodesílají.", "Povolit stažení") && FooterViewModel.RetryCommand.CanExecute(null))
        {
            FooterViewModel.RetryCommand.Execute(null);
            ShowRequiredFiles();
        }
    }

    private async void OnVideoInstallClick(object sender, RoutedEventArgs e)
    {
        var command = FooterViewModel.DownloadVideoToolsCommand;
        if (command.CanExecute(null) &&
            await ConfirmRequiredActionAsync("Stáhnout nástroje pro video?",
                "Stáhnout FFmpeg, FFprobe a FFplay pro Windows x64 z Gyan.dev (GPLv3)? " +
                "Aplikace ověří SHA-256 balíčku. Videa se nikam neodesílají.", "Stáhnout") &&
            command.CanExecute(null))
        {
            var download = command.ExecuteAsync(null);
            ShowRequiredFiles();
            await download;
        }
    }

    private void OnCancelVideoToolsClick(object sender, RoutedEventArgs e)
    {
        FooterViewModel.CheckVideoToolsCommand.Cancel();
        FooterViewModel.DownloadVideoToolsCommand.Cancel();
    }

    private void ShowRequiredFiles()
    {
        if (_mediaUiActive && GalleryWorkspace.Visibility == Visibility.Visible)
        {
            RequiredFilesButton.Flyout?.ShowAt(RequiredFilesButton);
        }
    }

    private Task<bool> ConfirmRequiredActionAsync(string title, string message, string primary) =>
        ShowConfirmedDialogAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "Storno",
            DefaultButton = ContentDialogButton.Close
        });

    private async Task<bool> ShowConfirmedDialogAsync(ContentDialog dialog)
    {
        if (!_mediaUiActive || _galleryConfirmation is not null || XamlRoot is not { } root)
        {
            return false;
        }
        RequiredFilesButton.Flyout?.Hide();
        dialog.XamlRoot = root;
        _galleryConfirmation = dialog;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary &&
                _mediaUiActive && ReferenceEquals(_galleryConfirmation, dialog);
        }
        finally
        {
            if (ReferenceEquals(_galleryConfirmation, dialog))
            {
                _galleryConfirmation = null;
            }
        }
    }

    private async void OnRequiredFilesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_mediaUiActive)
        {
            return;
        }
        if (e.PropertyName == nameof(ModelFooterViewModel.VideoSetupActive))
        {
            RefreshSelectedVideo();
            if (_videoFile is { } file && _videoTools is null && _videoOperation is null)
            {
                await LoadVideoDetailAsync(file);
            }
        }
        else if (e.PropertyName == nameof(ModelFooterViewModel.VideoSetupMessage) && FooterViewModel.VideoTools is null)
        {
            if (_videoFile is not null && _videoOperation is null)
            {
                VideoStatus.Text = FooterViewModel.VideoSetupMessage;
            }
            if (_selectedVideos.Length > 0 && _videoSelectionOperation is null)
            {
                VideoSelectionStatus.Text = FooterViewModel.VideoSetupMessage;
            }
        }
        if (e.PropertyName is nameof(ModelFooterViewModel.VideoSetupActive) or
            nameof(ModelFooterViewModel.VideoSetupIndeterminate) or nameof(ModelFooterViewModel.VideoSetupProgress))
        {
            UpdateVideoDetailSetupProgress();
        }
    }
}
