using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SmartGallery.Application.Catalog;
using SmartGallery.Domain;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace SmartGallery;

public sealed partial class MainPage
{
    private static readonly IValueConverter SelectionFrameOpacity = new SelectionFrameOpacityConverter();
    private Pointer? _marqueePointer;
    private MarqueeSurface? _marqueeSurface;
    private Point _marqueeOrigin;
    private bool _marqueeActive;
    private bool _marqueeCaptured;
    private bool _updatingMarquee;
    private VirtualKeyModifiers _marqueeModifiers;
    private HashSet<object> _marqueeBase = [];
    private HashSet<object> _marqueeHits = [];

    private sealed record MarqueeSurface(Grid Host, GridView Items, Rectangle Outline);

    private sealed class SelectionFrameOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is true ? 1d : 0d;

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    private void OnSelectionFrameLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border frame && FindAncestor<GridViewItem>(frame) is { } item)
        {
            frame.SetBinding(UIElement.OpacityProperty, new Binding
            {
                Source = item,
                Path = new PropertyPath(nameof(GridViewItem.IsSelected)),
                Mode = BindingMode.OneWay,
                Converter = SelectionFrameOpacity
            });
        }
    }

    private void OnSelectionFrameUnloaded(object sender, RoutedEventArgs e) =>
        ((Border)sender).ClearValue(UIElement.OpacityProperty);

    private void InitializeGalleryInteraction()
    {
        GalleryWorkspace.AddHandler(KeyDownEvent, new KeyEventHandler(OnWorkspaceKeyDown), true);
        GalleryGrid.AddHandler(KeyDownEvent, new KeyEventHandler(OnGalleryKeyDown), true);
        GalleryGrid.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(OnGalleryDoubleTapped), true);
        TrashGrid.AddHandler(KeyDownEvent, new KeyEventHandler(OnTrashKeyDown), true);
        TrashGrid.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(OnTrashTileDoubleTapped), true);
        RegisterMarqueeHost(GalleryHost);
        RegisterMarqueeHost(TrashHost);
        PhotoViewer.AddHandler(KeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
    }

    private void RegisterMarqueeHost(Grid host)
    {
        host.AddHandler(PointerPressedEvent, new PointerEventHandler(OnMarqueePointerPressed), true);
        host.AddHandler(PointerMovedEvent, new PointerEventHandler(OnMarqueePointerMoved), true);
        host.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnMarqueePointerReleased), true);
        host.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnMarqueePointerCanceled), true);
        host.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnMarqueePointerCaptureLost), true);
        host.PointerExited += OnMarqueePointerExited;
    }

    private void SyncSelection(GridView grid)
    {
        var items = grid.SelectedItems;
        if (ReferenceEquals(grid, TrashGrid))
        {
            ViewModel.SetTrashSelectionCount(items.Count);
        }
        else
        {
            ViewModel.SetSelection(items.Count, items.Count == 1 ? items[0] as GalleryFile : null);
        }
    }

    private void OnMediaSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingMarquee)
        {
            SyncSelection((GridView)sender);
        }
    }

    private async void OnGalleryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && ViewModel.CanDelete)
        {
            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown)
            {
                await ConfirmDeleteSelectionAsync();
            }
            return;
        }
        if (!ViewModel.CanBrowse || e.Key != VirtualKey.Enter)
        {
            return;
        }

        var tile = FindAncestor<GridViewItem>(e.OriginalSource as DependencyObject);
        if ((tile?.Content ?? GalleryGrid.SelectedItem) is not GalleryFile file)
        {
            return;
        }
        e.Handled = true;
        if (IsKeyDown(VirtualKey.Menu))
        {
            SetDetailsMode(!_showFileDetails, file);
        }
        else if (file.Kind == MediaKind.Photo)
        {
            await ShowPhotoAsync(file, tile);
        }
    }

    private void OnWorkspaceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.CanNavigateLibrary || e.Key != VirtualKey.A || !IsKeyDown(VirtualKey.Control) ||
            FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }
        if (!e.Handled || FindAncestor<GridView>(e.OriginalSource as DependencyObject) is null)
        {
            (ViewModel.IsTrashOpen ? TrashGrid : GalleryGrid).SelectAll();
        }
        e.Handled = true;
    }

    private async void OnGalleryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var tile = FindAncestor<GridViewItem>(e.OriginalSource as DependencyObject);
        if (ViewModel.CanBrowse && tile?.Content is GalleryFile { Kind: MediaKind.Photo } file)
        {
            e.Handled = true;
            await ShowPhotoAsync(file, tile);
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private void OnMarqueePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var host = (Grid)sender;
        var inTrash = ReferenceEquals(host, TrashHost);
        var point = e.GetCurrentPoint(host);
        if (!(inTrash ? ViewModel.IsTrashOpen && ViewModel.CanNavigateLibrary : ViewModel.CanBrowse) ||
            e.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
            !point.Properties.IsLeftButtonPressed ||
            FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }
        EndMarquee();
        var surface = inTrash
            ? new MarqueeSurface(host, TrashGrid, TrashSelectionRectangle)
            : new MarqueeSurface(host, GalleryGrid, SelectionRectangle);
        _marqueeSurface = surface;
        _marqueePointer = e.Pointer;
        _marqueeOrigin = point.Position;
        _marqueeModifiers = e.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        _marqueeBase = _marqueeModifiers == VirtualKeyModifiers.None ? [] :
            surface.Items.SelectedItems.ToHashSet();
    }

    private void OnMarqueePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (GetMarqueeSurface(sender, e) is not { } surface)
        {
            return;
        }
        var point = e.GetCurrentPoint(surface.Host);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndMarquee();
            return;
        }

        if (!_marqueeActive)
        {
            const double startDistance = 5;
            if (Math.Abs(point.Position.X - _marqueeOrigin.X) < startDistance &&
                Math.Abs(point.Position.Y - _marqueeOrigin.Y) < startDistance)
            {
                return;
            }
            _marqueeActive = true;
            _marqueeCaptured = surface.Host.CapturePointer(e.Pointer);
            _updatingMarquee = true;
            try
            {
                if (_marqueeModifiers == VirtualKeyModifiers.None)
                {
                    surface.Items.SelectedItems.Clear();
                }
                else
                {
                    var current = surface.Items.SelectedItems.ToHashSet();
                    foreach (var item in current.Except(_marqueeBase))
                    {
                        surface.Items.SelectedItems.Remove(item);
                    }
                    foreach (var item in _marqueeBase.Except(current))
                    {
                        surface.Items.SelectedItems.Add(item);
                    }
                }
            }
            finally { _updatingMarquee = false; }
            if (FindAncestor<GridViewItem>(e.OriginalSource as DependencyObject) is null)
            {
                surface.Items.Focus(FocusState.Pointer);
            }
        }
        e.Handled = true;
        UpdateMarquee(surface, point.Position);
    }

    private void OnMarqueePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (GetMarqueeSurface(sender, e) is not { } surface)
        {
            return;
        }
        if (_marqueeActive)
        {
            e.Handled = true;
            UpdateMarquee(surface, e.GetCurrentPoint(surface.Host).Position);
        }
        EndMarquee();
    }

    private MarqueeSurface? GetMarqueeSurface(object sender, PointerRoutedEventArgs e) =>
        _marqueePointer?.PointerId == e.Pointer.PointerId && ReferenceEquals(_marqueeSurface?.Host, sender)
            ? _marqueeSurface : null;

    private void OnMarqueePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (GetMarqueeSurface(sender, e) is not null)
        {
            EndMarquee();
        }
    }

    private void OnMarqueePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeCaptured && ReferenceEquals(e.OriginalSource, sender))
        {
            OnMarqueePointerCanceled(sender, e);
        }
    }

    private void OnMarqueePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_marqueeCaptured && GetMarqueeSurface(sender, e) is { } surface)
        {
            var position = e.GetCurrentPoint(surface.Host).Position;
            if (position.X < 0 || position.Y < 0 ||
                position.X >= surface.Host.ActualWidth || position.Y >= surface.Host.ActualHeight)
            {
                EndMarquee();
            }
        }
    }

    private void UpdateMarquee(MarqueeSurface surface, Point pointer)
    {
        if (surface.Items.ItemsPanelRoot is not ItemsWrapGrid panel)
        {
            return;
        }
        var viewport = new Rect(0, 0, surface.Host.ActualWidth, surface.Host.ActualHeight);
        var x = Math.Clamp(pointer.X, 0, viewport.Width);
        var y = Math.Clamp(pointer.Y, 0, viewport.Height);
        var area = new Rect(Math.Min(x, _marqueeOrigin.X), Math.Min(y, _marqueeOrigin.Y),
            Math.Abs(x - _marqueeOrigin.X), Math.Abs(y - _marqueeOrigin.Y));
        Canvas.SetLeft(surface.Outline, area.X);
        Canvas.SetTop(surface.Outline, area.Y);
        surface.Outline.Width = Math.Max(1, area.Width);
        surface.Outline.Height = Math.Max(1, area.Height);
        surface.Outline.Visibility = Visibility.Visible;

        var hits = new HashSet<object>();
        var last = Math.Min(panel.LastVisibleIndex, surface.Items.Items.Count - 1);
        for (var index = Math.Max(panel.FirstVisibleIndex, 0); index <= last; index++)
        {
            if (surface.Items.ContainerFromIndex(index) is GridViewItem { Content: { } item } tile &&
                Intersects(area, tile.TransformToVisual(surface.Host)
                    .TransformBounds(new Rect(0, 0, tile.ActualWidth, tile.ActualHeight))))
            {
                hits.Add(item);
            }
        }

        _updatingMarquee = true;
        try
        {
            foreach (var item in _marqueeHits)
            {
                if (!hits.Contains(item))
                {
                    SetMarked(surface.Items, item, _marqueeBase.Contains(item));
                }
            }
            var toggle = (_marqueeModifiers & VirtualKeyModifiers.Control) != 0;
            foreach (var item in hits)
            {
                if (!_marqueeHits.Contains(item))
                {
                    SetMarked(surface.Items, item, !toggle || !_marqueeBase.Contains(item));
                }
            }
            _marqueeHits = hits;
        }
        finally
        {
            _updatingMarquee = false;
            SyncSelection(surface.Items);
        }
    }

    private static void SetMarked(GridView grid, object item, bool marked)
    {
        var selected = grid.SelectedItems;
        if (marked)
        {
            if (!selected.Contains(item))
            {
                selected.Add(item);
            }
        }
        else
        {
            selected.Remove(item);
        }
    }

    private void EndMarquee()
    {
        var pointer = _marqueePointer;
        var captured = _marqueeCaptured;
        var surface = _marqueeSurface;
        _marqueeSurface = null;
        _marqueePointer = null;
        _marqueeActive = false;
        _marqueeCaptured = false;
        if (surface is not null)
        {
            if (captured && pointer is not null)
            {
                surface.Host.ReleasePointerCapture(pointer);
            }
            surface.Outline.Visibility = Visibility.Collapsed;
        }
        _marqueeHits.Clear();
        _marqueeBase.Clear();
    }

    private static bool Intersects(Rect a, Rect b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
