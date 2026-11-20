using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using SmartGallery.ViewModels;
using Windows.System;

namespace SmartGallery;

public sealed partial class MainPage
{
    private async void OnRestoreTrashClick(object sender, RoutedEventArgs e)
    {
        var selected = TrashGrid.SelectedItems.OfType<TrashReviewRow>().Select(row => row.Item).ToArray();
        var request = new GalleryTrashRequest(false, selected);
        if (ViewModel.CanRestoreTrash && ViewModel.ManageTrashCommand.CanExecute(request))
        {
            await ViewModel.ManageTrashCommand.ExecuteAsync(request);
        }
    }

    private async void OnEmptyTrashClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is not { } root || !ViewModel.CanEmptyTrash)
        {
            return;
        }
        var snapshot = ViewModel.TrashItems;
        var request = new GalleryTrashRequest(true, snapshot.Select(row => row.Item).ToArray());
        var confirmation = new ContentDialog
        {
            XamlRoot = root,
            Title = "Vysypat koš galerie?",
            Content = new TextBlock
            {
                Text = $"Všech {snapshot.Count} zobrazených položek se trvale odstraní. " +
                       "Nepřesunou se do koše Windows a z galerie je už nepůjde obnovit.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Trvale odstranit",
            CloseButtonText = "Zrušit",
            DefaultButton = ContentDialogButton.Close
        };
        if (await ConfirmGalleryChangeAsync(confirmation) && ReferenceEquals(snapshot, ViewModel.TrashItems) &&
            ViewModel.CanEmptyTrash && ViewModel.ManageTrashCommand.CanExecute(request))
        {
            await ViewModel.ManageTrashCommand.ExecuteAsync(request);
        }
    }

    private async void OnTrashTileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var tile = FindAncestor<GridViewItem>(e.OriginalSource as DependencyObject);
        if (tile?.Content is TrashReviewRow { Item.Kind: MediaKind.Photo } row && !ViewModel.IsBusy)
        {
            e.Handled = true;
            await ShowTrashPhotoAsync(row, tile);
        }
    }

    private async void OnTrashKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var tile = FindAncestor<GridViewItem>(e.OriginalSource as DependencyObject);
        if (e.Key == VirtualKey.Enter && !ViewModel.IsBusy &&
            (tile?.Content ?? TrashGrid.SelectedItem) is TrashReviewRow { Item.Kind: MediaKind.Photo } row)
        {
            e.Handled = true;
            await ShowTrashPhotoAsync(row, tile);
        }
    }

    private Task ShowTrashPhotoAsync(TrashReviewRow row, Control? tile) =>
        ShowPhotoAsync(row.Name, tile, token => ViewModel.GetTrashReadPathAsync(row.Item, token));

    private void OnReviewTileChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (GetReviewThumbnail(e) is { } image)
        {
            ResetFileVisual(image);
        }
        if (!e.InRecycleQueue)
        {
            e.RegisterUpdateCallback(OnReviewTilePhase);
        }
    }

    private void OnReviewTilePhase(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.InRecycleQueue || GetReviewThumbnail(e) is not { } image || ReferenceEquals(image.Tag, e.Item))
        {
            return;
        }
        ResetFileVisual(image);
        image.Tag = e.Item;
        var isPhoto = e.Item is DuplicateReviewTile or TrashReviewRow { Item.Kind: MediaKind.Photo };
        switch (e.Item)
        {
            case DuplicateReviewTile tile:
                _ = LoadFileVisualAsync(image, Load, tile.File);
                break;
            case TrashReviewRow row:
                _ = LoadFileVisualAsync(image, Load, row.Item.RelativePath,
                    token => ViewModel.GetTrashReadPathAsync(row.Item, token));
                break;
        }

        async Task Load(Windows.Storage.StorageFile storageFile, CancellationToken token)
        {
            await LoadThumbnailAsync(image, storageFile, token, fallbackToPhoto: isPhoto);
            if (image.Source is not null)
            {
                SetReviewPlaceholder(image, Visibility.Collapsed);
            }
        }
    }

    private static Image? GetReviewThumbnail(ContainerContentChangingEventArgs e) =>
        (e.ItemContainer.ContentTemplateRoot as FrameworkElement)?.FindName("ReviewThumbnail") as Image;

    private static void SetReviewPlaceholder(Image image, Visibility visibility)
    {
        if ((image.Parent as FrameworkElement)?.FindName("ReviewPlaceholder") is FrameworkElement placeholder)
        {
            placeholder.Visibility = visibility;
        }
    }
}
