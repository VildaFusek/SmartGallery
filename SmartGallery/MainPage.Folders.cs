using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using SmartGallery.ViewModels;

namespace SmartGallery;

public sealed partial class MainPage
{
    private readonly Dictionary<string, TreeViewNode> _folderNodes = new(StringComparer.OrdinalIgnoreCase);
    private const string TrashFolderLabel = "Koš galerie";
    private TreeViewNode? _trashNode;
    private ScrollViewer? _galleryScrollViewer;
    private double _galleryVerticalOffset;
    private bool _timelineSyncQueued;

    private void UpdateTimelineSource() =>
        ((CollectionViewSource)Resources["TimelineSource"]).Source = ViewModel.TimelineGroups;

    private void OnGalleryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.IsGalleryOpen))
        {
            SyncVideoSelection();
        }
        else if (e.PropertyName == nameof(GalleryViewModel.IsBusy))
        {
            UpdateSelectedVideoButtons();
        }
        if (_videoFile is not null && e.PropertyName is (nameof(GalleryViewModel.CanChangeFilters) or nameof(GalleryViewModel.VisibleFiles)))
        {
            UpdateVideoButtons();
        }
        if (e.PropertyName == nameof(GalleryViewModel.RootFolder))
        {
            RebuildFolderTree();
        }
        else if (e.PropertyName == nameof(GalleryViewModel.TimelineGroups))
        {
            EndMarquee();
            UpdateTimelineSource();
        }
        else if (e.PropertyName is nameof(GalleryViewModel.IsTrashOpen) or nameof(GalleryViewModel.TrashItems) ||
                 (e.PropertyName == nameof(GalleryViewModel.CanNavigateLibrary) && !ViewModel.CanNavigateLibrary))
        {
            EndMarquee();
        }
    }

    private void StartTimelineTracking()
    {
        GalleryGrid.ApplyTemplate();
        _galleryScrollViewer = FindVisualChild<ScrollViewer>(GalleryGrid);
        if (_galleryScrollViewer is { } scrollViewer)
        {
            _galleryVerticalOffset = scrollViewer.VerticalOffset;
            scrollViewer.ViewChanged += OnGalleryViewChanged;
        }
    }

    private void StopTimelineTracking()
    {
        if (_galleryScrollViewer is { } scrollViewer)
        {
            scrollViewer.ViewChanged -= OnGalleryViewChanged;
            _galleryScrollViewer = null;
        }
    }

    private void RestoreGalleryScrollPosition(double position)
    {
        if (!ViewModel.IsGalleryOpen || _galleryScrollViewer is not { } scrollViewer)
        {
            return;
        }

        GalleryWorkspace.UpdateLayout();
        scrollViewer.ChangeView(null, Math.Min(position, scrollViewer.ScrollableHeight), null, disableAnimation: true);
    }

    private void OnGalleryViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || _galleryVerticalOffset == scrollViewer.VerticalOffset)
        {
            return;
        }

        _galleryVerticalOffset = scrollViewer.VerticalOffset;
        if (!_timelineSyncQueued && ViewModel.IsGalleryOpen)
        {
            _timelineSyncQueued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _timelineSyncQueued = false;
                if (_galleryScrollViewer == scrollViewer && ViewModel.IsGalleryOpen)
                {
                    SyncFolderToViewport();
                }
            });
        }
    }

    private void SyncFolderToViewport()
    {
        if (GalleryGrid.ItemsPanelRoot is not ItemsWrapGrid panel ||
            ViewModel.GetTimelineGroupAt(panel.FirstVisibleIndex) is not { } group ||
            !_folderNodes.TryGetValue(group.YearKey, out var year))
        {
            return;
        }

        ExpandFolderNode(year);
        if (_folderNodes.TryGetValue(group.SectionPath, out var section) && FolderTree.SelectedNode != section)
        {
            ViewModel.TrackTimelineSection(group);
            FolderTree.SelectedNode = section;
            FindVisualChild<ListViewBase>(FolderTree)?.ScrollIntoView(section);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }
            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }
        return null;
    }

    private void RebuildFolderTree()
    {
        var expanded = _folderNodes.Where(pair => pair.Value.IsExpanded)
            .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = ViewModel.CurrentFolder;
        for (var parent = Path.GetDirectoryName(active); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            expanded.Add(parent);
        }
        FolderTree.RootNodes.Clear();
        _trashNode = null;
        _folderNodes.Clear();
        if (ViewModel.RootFolder is not { } library)
        {
            return;
        }

        var years = ViewModel.GetChildFolders(library);
        if (years.Count == 0)
        {
            FolderTree.RootNodes.Add(CreateFolderNode(library));
        }
        foreach (var year in years)
        {
            var node = CreateFolderNode(year);
            FolderTree.RootNodes.Add(node);
            RestoreExpandedFolders(node, expanded);
        }
        _trashNode = new TreeViewNode { Content = new GalleryFolder(TrashFolderLabel, "") };
        FolderTree.RootNodes.Add(_trashNode);
        FolderTree.SelectedNode = ViewModel.IsTrashOpen ? _trashNode : _folderNodes.GetValueOrDefault(active);
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

    private void ExpandFolderNode(TreeViewNode node)
    {
        if (!node.IsExpanded)
        {
            FillFolderNode(node);
            node.IsExpanded = true;
        }
    }

    private async void OnFolderInvoked(TreeView sender, TreeViewItemInvokedEventArgs e)
    {
        var item = e.InvokedItem is TreeViewNode node ? node.Content : e.InvokedItem;
        if (_trashNode is { } trash && ReferenceEquals(item, trash.Content))
        {
            if (ViewModel.OpenTrashCommand.CanExecute(null))
            {
                await ViewModel.OpenTrashCommand.ExecuteAsync(null);
            }
            sender.SelectedNode = ViewModel.IsTrashOpen ? _trashNode :
                _folderNodes.GetValueOrDefault(ViewModel.CurrentFolder);
        }
        else if (item is GalleryFolder folder && ViewModel.CanNavigateLibrary)
        {
            ViewModel.OpenFolder(folder);
            if (ViewModel.GetTimelineAnchor() is { } file)
            {
                GalleryGrid.ScrollIntoView(file, ScrollIntoViewAlignment.Leading);
            }
        }
    }
}
