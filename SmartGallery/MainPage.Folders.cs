using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using SmartGallery.ViewModels;

namespace SmartGallery;

public sealed partial class MainPage
{
    private readonly Dictionary<string, TreeViewNode> _folderNodes = new(StringComparer.OrdinalIgnoreCase);
    private const string TrashFolderLabel = "Koš galerie";
    private TreeViewNode? _trashNode;

    private void UpdateTimelineSource() =>
        ((CollectionViewSource)Resources["TimelineSource"]).Source = ViewModel.TimelineGroups;

    private void OnGalleryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.RootFolder))
        {
            RebuildFolderTree();
        }
        else if (e.PropertyName == nameof(GalleryViewModel.TimelineGroups))
        {
            EndMarquee();
            UpdateTimelineSource();
        }
        else if (e.PropertyName == nameof(GalleryViewModel.CurrentFolder) &&
                 ViewModel.GetTimelineAnchor() is { } file)
        {
            GalleryGrid.ScrollIntoView(file, ScrollIntoViewAlignment.Leading);
        }
        else if (e.PropertyName is nameof(GalleryViewModel.IsTrashOpen) or nameof(GalleryViewModel.TrashItems) ||
                 (e.PropertyName == nameof(GalleryViewModel.CanNavigateLibrary) && !ViewModel.CanNavigateLibrary))
        {
            EndMarquee();
        }
    }

    private void RebuildFolderTree()
    {
        var expanded = _folderNodes.Where(pair => pair.Value.IsExpanded)
            .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = ViewModel.CurrentFolder;
        for (var parent = Path.GetDirectoryName(active); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            expanded.Add(parent);
        }
        expanded.Add("");

        FolderTree.RootNodes.Clear();
        _trashNode = null;
        _folderNodes.Clear();
        if (ViewModel.RootFolder is not { } folder)
        {
            return;
        }

        var root = CreateFolderNode(folder);
        FolderTree.RootNodes.Add(root);
        RestoreExpandedFolders(root, expanded);
        _trashNode = new TreeViewNode { Content = TrashFolderLabel };
        FolderTree.RootNodes.Add(_trashNode);
        FolderTree.SelectedNode = ViewModel.IsTrashOpen ? _trashNode : _folderNodes.GetValueOrDefault(active, root);
    }

    private TreeViewNode CreateFolderNode(GalleryFolder folder)
    {
        var node = new TreeViewNode
        {
            Content = folder,
            HasUnrealizedChildren = ViewModel.GetChildFolders(folder).Count > 0
        };
        _folderNodes.Add(folder.RelativePath, node);
        return node;
    }

    private void RestoreExpandedFolders(TreeViewNode node, HashSet<string> expanded)
    {
        if (node.Content is not GalleryFolder folder || !expanded.Contains(folder.RelativePath) ||
            !node.HasUnrealizedChildren)
        {
            return;
        }
        FillFolderNode(node);
        foreach (var child in node.Children)
        {
            RestoreExpandedFolders(child, expanded);
        }
        node.IsExpanded = true;
    }

    private void FillFolderNode(TreeViewNode node)
    {
        if (!node.HasUnrealizedChildren || node.Content is not GalleryFolder folder)
        {
            return;
        }
        foreach (var child in ViewModel.GetChildFolders(folder))
        {
            node.Children.Add(CreateFolderNode(child));
        }
        node.HasUnrealizedChildren = false;
    }

    private void OnFolderExpanding(TreeView sender, TreeViewExpandingEventArgs e) => FillFolderNode(e.Node);

    private async void OnFolderInvoked(TreeView sender, TreeViewItemInvokedEventArgs e)
    {
        var item = e.InvokedItem is TreeViewNode node ? node.Content : e.InvokedItem;
        if (item is TrashFolderLabel)
        {
            if (ViewModel.OpenTrashCommand.CanExecute(null))
            {
                await ViewModel.OpenTrashCommand.ExecuteAsync(null);
            }
            sender.SelectedNode = ViewModel.IsTrashOpen ? _trashNode :
                _folderNodes.GetValueOrDefault(ViewModel.CurrentFolder);
        }
        else if (item is GalleryFolder folder)
        {
            ViewModel.OpenFolder(folder);
        }
    }
}
