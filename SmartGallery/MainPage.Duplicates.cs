using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using SmartGallery.Application.Catalog;
using SmartGallery.ViewModels;

namespace SmartGallery;

public sealed partial class MainPage
{
    private async void OnFindDuplicatesClick(object sender, RoutedEventArgs e)
    {
        Func<PhotoDuplicateScan, Task<IReadOnlyList<PhotoDuplicateSelection>?>> review = ReviewDuplicatesAsync;
        if (ViewModel.FindDuplicatesCommand.CanExecute(review))
        {
            await ViewModel.FindDuplicatesCommand.ExecuteAsync(review);
        }
    }

    private async Task<IReadOnlyList<PhotoDuplicateSelection>?> ReviewDuplicatesAsync(PhotoDuplicateScan scan)
    {
        if (XamlRoot is not { } root)
        {
            return null;
        }
        var selections = scan.Groups.Select(group => new PhotoDuplicateSelection(group, group.Copies[0])).ToArray();
        var groupedSource = new CollectionViewSource
        {
            IsSourceGrouped = true,
            Source = selections.Select((selection, index) => new DuplicateReviewGroup(selection, index + 1)).ToArray()
        };
        var tiles = new GridView
        {
            MaxHeight = 380,
            SelectionMode = ListViewSelectionMode.None,
            ItemsPanel = (ItemsPanelTemplate)Resources["DuplicateReviewPanel"],
            ItemTemplate = (DataTemplate)Resources["DuplicateReviewTemplate"]
        };
        tiles.GroupStyle.Add(new GroupStyle { HeaderTemplate = (DataTemplate)Resources["DuplicateReviewGroupHeader"] });
        tiles.ContainerContentChanging += OnReviewTileChanging;
        tiles.ItemsSource = groupedSource.View;
        var content = new StackPanel { Spacing = 6 };
        var removalCount = selections.Sum(selection => selection.Group.Copies.Count - 1);
        content.Children.Add(new TextBlock
        {
            Text = $"Skupin: {selections.Length} · kopií k přesunu: {removalCount}" +
                   (scan.Skipped > 0 ? $" · přeskočeno: {scan.Skipped}" : "") +
                   "\nZkontroluj miniatury. V každé skupině zůstane kopie označená „Ponechat“. " +
                   "Ostatní se přesunou do koše až po potvrzení níže.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(tiles);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Shodné fotografie",
            Content = content,
            PrimaryButtonText = "Potvrdit přesun do koše",
            CloseButtonText = "Zavřít bez změn",
            DefaultButton = ContentDialogButton.Close
        };
        return await ConfirmGalleryChangeAsync(dialog) ? selections : null;
    }
}
