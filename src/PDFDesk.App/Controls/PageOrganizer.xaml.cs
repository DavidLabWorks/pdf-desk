using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Hosting;
using PDFDesk.Engine;
using PDFDesk.Services;
using Microsoft.UI.Xaml.Automation;
using System.Numerics;
using Windows.UI.Core;

namespace PDFDesk.Controls;

public sealed partial class PageOrganizer : ContentDialog
{
    private readonly DocumentView _view;
    private readonly ObservableCollection<ThumbnailItem> _items = [];
    private readonly ThumbnailItem[] _sources;
    private readonly PageDraft _draft;
    private readonly Dictionary<int, Task> _renders = [];
    private bool _saved;
    private bool _promptOpen;
    private bool _discarded;
    private List<int> _dragged = [];
    private bool _busy;

    internal PageOrganizer(DocumentView view, ObservableCollection<ThumbnailItem> items, List<int> selected)
    {
        InitializeComponent();
        _view = view;
        _sources = [.. items];
        _draft = new PageDraft(items.Count);
        RefreshDraft();
        Pages.ContainerContentChanging += (sender, args) =>
        {
            if (!args.InRecycleQueue && args.Item is ThumbnailItem { IsRendered: false } item)
                _ = RenderDraftCardAsync(item);
        };
        Pages.ItemsSource = _items;
        Resize(view.XamlRoot);
        Opened += (_, _) =>
        {
            SelectPages(selected);
            UpdateCommands();
            view.XamlRoot.Changed += RootChanged;
        };
        Closing += (_, e) =>
        {
            e.Cancel = _busy || _promptOpen || (!_saved && !_discarded && _draft.Changed);
            if (!e.Cancel || _busy || _promptOpen) return;
            _promptOpen = true;
            EditorSurface.IsEnabled = false;
            ConfirmLayer.Visibility = Visibility.Visible;
            AutomationProperties.SetName(ConfirmLayer, UiText.Current.SavePageChanges);
            ConfirmSave.Focus(FocusState.Programmatic);
        };
        Closed += (_, _) => view.XamlRoot.Changed -= RootChanged;
    }

    private void ReturnToDraft()
    {
        _promptOpen = false;
        ConfirmLayer.Visibility = Visibility.Collapsed;
        EditorSurface.IsEnabled = true;
        Dismiss.Focus(FocusState.Programmatic);
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs args) => ReturnToDraft();

    private void Confirm_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        if (!_busy) ReturnToDraft();
    }

    private void ConfirmDiscard_Click(object sender, RoutedEventArgs args)
    {
        _discarded = true;
        _promptOpen = false;
        Hide();
    }

    private async void ConfirmSave_Click(object sender, RoutedEventArgs args)
    {
        ConfirmSave.IsEnabled = ConfirmDiscard.IsEnabled = ConfirmCancel.IsEnabled = false;
        await EditAsync(SaveChangesAsync);
        ConfirmSave.IsEnabled = ConfirmDiscard.IsEnabled = ConfirmCancel.IsEnabled = true;
        if (_saved)
        {
            _promptOpen = false;
            Hide();
        }
        else ReturnToDraft();
    }

    private async Task SaveChangesAsync()
    {
        try
        {
            await _view.SavePageDraftAsync(_draft);
            _saved = true;
        }
        catch (Exception ex)
        {
            _error = UiText.Format("Save failed: {0}", UiText.Translate(ex.Message));
        }
    }

    private void RefreshDraft()
    {
        while (_items.Count > _draft.Count) _items.RemoveAt(_items.Count - 1);
        for (int i = 0; i < _draft.Count; i++)
        {
            var source = _sources[_draft.SourceAt(i)];
            if (i == _items.Count)
                _items.Add(new ThumbnailItem(i, source.PageWidthPt, source.PageHeightPt, _view.Viewer.ViewRotation) { Image = source.Image });
            else
                _items[i].UpdateSlot(i, source.PageWidthPt, source.PageHeightPt, source.Image);
        }
        if (Pages.ItemsPanelRoot is not { } panel) return;
        foreach (var card in panel.Children.OfType<GridViewItem>())
            if (Pages.ItemFromContainer(card) is ThumbnailItem { IsRendered: false } item)
                _ = RenderDraftCardAsync(item);
    }

    private async Task RenderDraftCardAsync(ThumbnailItem item)
    {
        int slot = item.PageIndex;
        if (slot >= _draft.Count) return;
        int index = _draft.SourceAt(slot);
        var source = _sources[index];
        if (!source.IsRendered)
        {
            if (!_renders.TryGetValue(index, out var render))
            {
                render = _view.RenderThumbnailAsync(source);
                _renders[index] = render;
            }
            await render;
            if (_renders.GetValueOrDefault(index) == render) _renders.Remove(index);
        }
        if (slot < _items.Count && ReferenceEquals(_items[slot], item) && _draft.SourceAt(slot) == index)
            item.Image = source.Image;
    }

    private void Dismiss_Click(object sender, RoutedEventArgs args) => Hide();

    private string? _error;
    private void RootChanged(XamlRoot root, XamlRootChangedEventArgs args) => Resize(root);
    private void Resize(XamlRoot root)
    {
        Body.Width = Math.Clamp(root.Size.Width - 96, 160, 1000);
        bool compact = Body.Width < 560;
        Grid.SetRow(ToolsSurface, compact ? 1 : 0);
        Grid.SetColumn(ToolsSurface, compact ? 0 : 1);
        Grid.SetColumnSpan(ToolsSurface, compact ? 2 : 1);
        Grid.SetColumnSpan(SelectionSummary, compact ? 2 : 1);
        ToolsSurface.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Body.Height = Math.Clamp(root.Size.Height - 120, 120, 690);
    }

    private List<int> Selection() =>
        [.. Pages.SelectedItems.Cast<ThumbnailItem>().Select(item => item.PageIndex).Order()];

    private void SelectPages(IEnumerable<int> indices, bool scroll = true)
    {
        var wanted = indices.Where(i => i >= 0 && i < _items.Count).Select(i => _items[i]).ToHashSet();
        foreach (var current in Pages.SelectedItems.Cast<ThumbnailItem>().ToArray())
            if (!wanted.Remove(current)) Pages.SelectedItems.Remove(current);
        foreach (var item in wanted) Pages.SelectedItems.Add(item);
        if (scroll && Pages.SelectedItems.FirstOrDefault() is ThumbnailItem first) Pages.ScrollIntoView(first);
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_items is not null && !_busy) UpdateCommands();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs args)
    {
        if (_busy) return;
        if (Pages.SelectedItems.Count == _items.Count) Pages.SelectedItems.Clear();
        else Pages.SelectAll();
    }

    private static bool IsContiguous(List<int> pages) =>
        pages.Count > 0 && pages[^1] - pages[0] + 1 == pages.Count;

    private void UpdateCommands()
    {
        var pages = Selection();
        bool any = pages.Count > 0;
        SelectAllBox.IsChecked = !any ? false : pages.Count == _items.Count ? true : null;
        Remove.IsEnabled = any && pages.Count < _items.Count;
        Duplicate.IsEnabled = any;
        bool contiguous = IsContiguous(pages);
        Earlier.IsEnabled = contiguous && pages[0] > 0;
        Later.IsEnabled = contiguous && pages[^1] < _items.Count - 1;
        Undo.IsEnabled = _draft.CanUndo;
        Redo.IsEnabled = _draft.CanRedo;
        SaveDraft.IsEnabled = _draft.Changed;
        Status.Text = _error ?? UiText.Format("{0} selected · {1} pages", pages.Count, _items.Count);
    }

    private static bool KeyDownNow(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private async void Body_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = KeyDownNow(VirtualKey.Control);
        bool shift = KeyDownNow(VirtualKey.Shift);
        bool alt = KeyDownNow(VirtualKey.Menu);
        if (ctrl && !shift && !alt && e.Key == VirtualKey.A)
        {
            e.Handled = true;
            if (!_busy) Pages.SelectAll();
            return;
        }
        Button? command = (e.Key, ctrl, shift, alt) switch
        {
            (VirtualKey.S, true, false, false) => SaveDraft,
            (VirtualKey.D, true, false, false) => Duplicate,
            (VirtualKey.Delete, false, false, false) => Remove,
            (VirtualKey.Left, true, false, false) => Earlier,
            (VirtualKey.Right, true, false, false) => Later,
            (VirtualKey.Z, true, false, false) => Undo,
            (VirtualKey.Z, true, true, false) or (VirtualKey.Y, true, false, false) => Redo,
            _ => null
        };
        if (command is null) return;
        e.Handled = true;
        if (command.IsEnabled && !e.KeyStatus.WasKeyDown) await ExecuteCommandAsync(command);
    }

    private async void Command_Click(object sender, RoutedEventArgs args) =>
        await ExecuteCommandAsync((Button)sender);

    private async Task ExecuteCommandAsync(Button button)
    {
        var pages = Selection();
        await EditAsync(async () =>
        {
            if (button == SaveDraft)
            {
                if (_draft.Changed) await SaveChangesAsync();
                return;
            }
            if (button == Undo || button == Redo)
            {
                if (button == Undo) _draft.Undo(); else _draft.Redo();
                RefreshDraft();
            }
            else if (pages.Count == 0) return;
            else if (button == Duplicate)
            {
                _draft.Copy(pages);
                RefreshDraft();
                SelectPages(Enumerable.Range(pages[^1] + 1, pages.Count));
            }
            else if (button == Remove && pages.Count < _items.Count)
            {
                _draft.Remove(pages);
                Pages.SelectedItems.Clear();
                RefreshDraft();
            }
            else if ((button == Earlier || button == Later) && IsContiguous(pages))
            {
                int at = pages[0] + (button == Earlier ? -1 : 1);
                if (at < 0 || at > _items.Count - pages.Count) return;
                Move(pages, at);
            }
        });
        if (_saved) Hide();
    }

    private async Task EditAsync(Func<Task> edit)
    {
        if (_busy) return;
        _busy = true;
        Body.IsHitTestVisible = false;
        _error = null;
        Pages.IsHitTestVisible = SelectAllBox.IsEnabled = Dismiss.IsEnabled = false;
        try { await edit(); }
        finally
        {
            _busy = false;
            Body.IsHitTestVisible = true;
            Pages.IsHitTestVisible = SelectAllBox.IsEnabled = Dismiss.IsEnabled = true;
            UpdateCommands();
        }
    }

    private void Move(List<int> pages, int destination)
    {
        bool animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        var origins = new Dictionary<int, Point>();
        if (animate)
        {
            var map = BookmarkRemap.MovePermutation(_items.Count, pages, destination);
            foreach (var card in Pages.ItemsPanelRoot.Children.OfType<GridViewItem>())
            {
                int index = Pages.IndexFromContainer(card);
                if (index >= 0 && map[index] != index)
                    origins[map[index]] = card.TransformToVisual(Pages).TransformPoint(new Point());
            }
        }

        _draft.Move(pages, destination);
        RefreshDraft();
        SelectPages(Enumerable.Range(destination, pages.Count), scroll: false);
        foreach (var (index, from) in origins)
        {
            if (Pages.ContainerFromIndex(index) is not GridViewItem card ||
                card.ContentTemplateRoot is not FrameworkElement content) continue;
            var to = card.TransformToVisual(Pages).TransformPoint(new Point());
            var delta = new Vector3((float)(from.X - to.X), (float)(from.Y - to.Y), 0);
            if (delta.LengthSquared() < 1) continue;
            ElementCompositionPreview.SetIsTranslationEnabled(content, true);
            var visual = ElementCompositionPreview.GetElementVisual(content);
            var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
            slide.Duration = TimeSpan.FromMilliseconds(190);
            slide.InsertKeyFrame(0, delta);
            slide.InsertKeyFrame(1, Vector3.Zero,
                visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1)));
            visual.StartAnimation("Translation", slide);
        }
    }

    private void Pages_DragStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragged = [.. e.Items.OfType<ThumbnailItem>().Select(item => item.PageIndex).Order()];
        e.Cancel = _busy || _dragged.Count == 0;
        e.Data.RequestedOperation = DataPackageOperation.Move;
        if (_dragged.Count > 1)
        {
            BundleCount.Text = UiText.Format("{0} pages", _dragged.Count);
            BundleImage.Source = _dragged.Select(i => _items[i].Image).FirstOrDefault(image => image is not null);
            ThirdSheet.Visibility = _dragged.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void DragCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        _dragged = [];
        BundleImage.Source = null;
        HideDragFeedback();
    }

    private void HideDragFeedback()
    {
        DragBundle.Visibility = Visibility.Collapsed;
        InsertionLine.Visibility = Visibility.Collapsed;
    }

    private void Pages_DragLeave(object sender, DragEventArgs e) => HideDragFeedback();

    private readonly record struct DropTarget(int Slot, bool Before, double Edge, double Top, double Height);

    // Use realized cards only. Whitespace at the end of a row belongs to
    // the closest card in that row, including the tail of the final row.
    private DropTarget? FindDropTarget(Point pointer)
    {
        double nearest = double.PositiveInfinity;
        DropTarget? target = null;
        foreach (var card in Pages.ItemsPanelRoot.Children.OfType<GridViewItem>())
        {
            int index = Pages.IndexFromContainer(card);
            if (index < 0) continue;
            var origin = card.TransformToVisual(Pages).TransformPoint(new Point());
            double top = Math.Max(0, origin.Y);
            double bottom = Math.Min(Pages.ActualHeight, origin.Y + card.ActualHeight);
            if (bottom <= top) continue;
            double dx = pointer.X - Math.Clamp(pointer.X, origin.X, origin.X + card.ActualWidth);
            double dy = pointer.Y - Math.Clamp(pointer.Y, origin.Y, origin.Y + card.ActualHeight);
            double distance = dx * dx + dy * dy;
            if (distance >= nearest) continue;
            nearest = distance;
            bool before = pointer.X < origin.X + card.ActualWidth / 2;
            target = new(before ? index : index + 1, before, before ? origin.X : origin.X + card.ActualWidth, top, bottom - top);
        }
        if (target is not { } hit) return null;

        // Merge edges only when both cards flank the same gap on screen.
        // Across a wrap, keep the edge of the card under the pointer.
        if (hit.Slot > 0 && hit.Slot < _items.Count &&
            Pages.ContainerFromIndex(hit.Slot - 1) is GridViewItem previous &&
            Pages.ContainerFromIndex(hit.Slot) is GridViewItem next)
        {
            var left = previous.TransformToVisual(Pages).TransformPoint(new Point());
            var right = next.TransformToVisual(Pages).TransformPoint(new Point());
            if (Math.Abs(left.Y - right.Y) < 1)
                return hit with { Edge = (left.X + previous.ActualWidth + right.X) / 2, Before = true };
        }
        return hit;
    }

    private void Pages_DragOver(object sender, DragEventArgs e)
    {
        if (_dragged.Count == 0 || _busy) return;
        var pointer = e.GetPosition(Pages);
        if (FindDropTarget(pointer) is not { } target)
        {
            HideDragFeedback();
            return;
        }
        Canvas.SetLeft(InsertionLine, target.Edge - InsertionLine.Width / 2);
        Canvas.SetTop(InsertionLine, target.Top);
        InsertionLine.Height = target.Height;
        InsertionLine.Visibility = Visibility.Visible;
        e.AcceptedOperation = DataPackageOperation.Move;
        bool bundle = _dragged.Count > 1;
        e.DragUIOverride.IsContentVisible = !bundle;
        e.DragUIOverride.IsGlyphVisible = !bundle;
        e.DragUIOverride.IsCaptionVisible = !bundle;
        if (bundle)
        {
            double gap = (double)Application.Current.Resources["SpaceL"];
            double x = pointer.X + gap + DragBundle.Width <= Pages.ActualWidth
                ? pointer.X + gap : pointer.X - gap - DragBundle.Width;
            double y = pointer.Y + gap + DragBundle.Height <= Pages.ActualHeight
                ? pointer.Y + gap : pointer.Y - gap - DragBundle.Height;
            Canvas.SetLeft(DragBundle, Math.Max(0, x));
            Canvas.SetTop(DragBundle, Math.Max(0, y));
            BundleDestination.Text = target.Before ? UiText.Format("Before page {0}", target.Slot + 1)
                : UiText.Format("After page {0}", target.Slot);
            DragBundle.Visibility = Visibility.Visible;
        }
        e.DragUIOverride.Caption = target.Before
            ? UiText.Format("Move {0} pages before page {1}", _dragged.Count, target.Slot + 1)
            : UiText.Format("Move {0} pages after page {1}", _dragged.Count, target.Slot);
        e.Handled = true;
    }

    private async void Pages_Drop(object sender, DragEventArgs e)
    {
        if (_dragged.Count == 0 || _busy) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
        HideDragFeedback();
        if (FindDropTarget(e.GetPosition(Pages)) is not { } target) return;
        var pages = _dragged;
        int slot = target.Slot;
        int destination = slot - pages.Count(i => i < slot);
        await EditAsync(() => { Move(pages, destination); return Task.CompletedTask; });
    }
}
