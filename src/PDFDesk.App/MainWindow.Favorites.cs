using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PDFDesk.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PDFDesk;

[Microsoft.UI.Xaml.Data.Bindable]
public sealed record FavoriteLocation
{
    public FavoriteLocation(Guid? id, string name, string glyph, string count, bool endsGroup = false) =>
        (Id, Name, Glyph, Count, EndsGroup) = (id, name, glyph, count, endsGroup);
    public Guid? Id { get; }
    public string Name { get; }
    public string Glyph { get; }
    public string Count { get; }
    private bool EndsGroup { get; }
    public Visibility DividerVisibility => EndsGroup ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FolderVisibility => Id.HasValue && Id != Guid.Empty ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => FolderVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    public override string ToString() => Name;
}

public sealed partial class MainWindow
{
    private bool _refreshingLocations;
    private const string FolderGlyph = "\uE8B7";

    private void FilterFavorites()
    {
        var filter = _folderFilter;
        if (filter.HasValue && filter != Guid.Empty && !_state.FavoriteFolders.ContainsKey(filter.Value))
            filter = _folderFilter = null;

        var paths = _state.Recents.Where(entry => entry.IsFavorite &&
                (filter == null || (entry.FavoriteFolderId ?? Guid.Empty) == filter))
            .Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        FavoriteThumbs.ItemsSource = RecentThumbs.Items.OfType<RecentCard>().Where(card => paths.Contains(card.Path)).ToList();
    }

    private void SaveFolderChanges()
    {
        _store.Save(_state);
        FilterFavorites();
        UpdateShelfSection();
    }

    private void SetFavorite(RecentCard card, bool favorite, Guid? folder)
    {
        if (_state.FindRecent(card.Path) is not { } entry) return;
        card.IsFavorite = entry.IsFavorite = favorite;
        entry.FavoriteFolderId = favorite ? folder : null;
        SaveFolderChanges();
    }

    private static MenuFlyoutItem FolderMenuItem(string label, Symbol icon, Action action)
    {
        var item = new MenuFlyoutItem { Text = label, Icon = new SymbolIcon(icon) };
        item.Click += (_, _) => action();
        return item;
    }

    private IEnumerable<KeyValuePair<Guid, string>> OrderedFolders() =>
        _state.FavoriteFolders.OrderBy(pair => pair.Value, StringComparer.CurrentCultureIgnoreCase);

    private void AddFavoriteActions(MenuFlyout menu, RecentCard card)
    {
        var ui = UiText.Current;
        menu.Items.Add(FolderMenuItem(card.IsFavorite ? ui.MoveFavorite : ui.AddToFavorites,
            Symbol.Favorite, () => _ = ChooseFavoriteAsync(card)));
        if (card.IsFavorite)
            menu.Items.Add(FolderMenuItem(ui.RemoveFromFavorites, Symbol.UnFavorite, () => SetFavorite(card, false, null)));
    }

    private List<FavoriteLocation> Locations(bool sidebar)
    {
        var ui = UiText.Current;
        var counts = _state.Recents.Where(r => r.IsFavorite).CountBy(r => r.FavoriteFolderId ?? Guid.Empty)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var rows = new List<FavoriteLocation>();
        if (sidebar)
            rows.Add(new(null, ui.AllFavorites, "\uE734", counts.Values.Sum().ToString()));
        rows.Add(new(Guid.Empty, sidebar ? ui.UnfiledFavorites : ui.FavoriteDirectly, "\uE8B7", sidebar ? counts.GetValueOrDefault(Guid.Empty).ToString() : "",
            endsGroup: sidebar && _state.FavoriteFolders.Count > 0));
        foreach (var (id, name) in OrderedFolders())
            rows.Add(new(id, name, FolderGlyph, sidebar ? counts.GetValueOrDefault(id).ToString() : ""));
        return rows;
    }

    private void RefreshFolderLocations()
    {
        var favorites = FavoritesTab.IsChecked == true;
        var rows = Locations(true);
        _refreshingLocations = true;
        try
        {
            if (FolderLocations.ItemsSource is not List<FavoriteLocation> existing || !rows.SequenceEqual(existing))
                FolderLocations.ItemsSource = rows;
            FolderLocations.SelectedItem = FolderLocations.Items.Cast<FavoriteLocation>()
                .FirstOrDefault(row => favorites && row.Id == _folderFilter);
        }
        finally { _refreshingLocations = false; }
    }

    private void FolderLocation_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_refreshingLocations || FolderLocations.SelectedItem is not FavoriteLocation location) return;
        _folderFilter = location.Id;
        FavoritesTab.IsChecked = true;
        SaveFolderChanges();
    }

    private RecentCard? _dragCard;
    private uint _dragPointer;
    private Windows.Foundation.Point _shelfDragStart;
    private Border? _hoverFolder;
    private Button? _dragSource;
    private Pointer? _capturedDrag;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _favoritesHoverTimer;
    private bool _hoveringFavorites;
    private FrameworkElement? _retainedRecentView;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _touchHoldTimer;
    private Pointer? _pendingTouch;

    private bool _shelfPressWasTouch;

    private void ShelfItem_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        var row = (FrameworkElement)sender;
        bool hasPosition = args.TryGetPosition(row, out var position);
        args.Handled = true;
        if (hasPosition && _shelfPressWasTouch) return;
        var flyout = FlyoutBase.GetAttachedFlyout(row);
        if (hasPosition) flyout.ShowAt(row, new FlyoutShowOptions { Position = position });
        else flyout.ShowAt(row);
    }

    private void ArmTouchDrag(Pointer pointer)
    {
        _pendingTouch = pointer;
        if (_touchHoldTimer == null)
        {
            _touchHoldTimer = DispatcherQueue.CreateTimer();
            _touchHoldTimer.IsRepeating = false;
            _touchHoldTimer.Interval = TimeSpan.FromMilliseconds(300);
            _touchHoldTimer.Tick += (_, _) =>
            {
                if (_pendingTouch is not { } touch || _dragSource == null) return;
                _dragSource.CancelDirectManipulations();
                BeginShelfDrag(touch, _shelfDragStart);
            };
        }
        _touchHoldTimer.Start();
    }

    private void AttachShelfDragHandlers()
    {
        // Shelf buttons capture the pointer. Listen to handled events as well so a
        // release over the folder can be resolved even during a fast mouse gesture.
        StartPage.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ShelfPointerPressed), true);
        StartPage.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ShelfPointerMoved), true);
        StartPage.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ShelfPointerReleased), true);
        StartPage.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Pointer.PointerId == _dragPointer) EndShelfDrag();
        }), true);
        StartPage.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) =>
        {
            if (_dragCard is not { } card || e.Pointer.PointerId != _dragPointer) return;
            // A button may release capture before the routed release reaches StartPage.
            var pointerId = e.Pointer.PointerId;
            bool lostShelfCapture = ReferenceEquals(e.OriginalSource, StartPage) ||
                                    ReferenceEquals(e.OriginalSource, _dragSource);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_dragPointer == pointerId && ReferenceEquals(_dragCard, card) && (_capturedDrag == null || lostShelfCapture)) EndShelfDrag();
            });
        }), true);
    }

    private void ShelfPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        bool touch = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch;
        if (_dragCard != null && e.Pointer.PointerId != _dragPointer) return;
        _shelfPressWasTouch = touch;
        EndShelfDrag();
        if (!(touch ? e.Pointer.IsInContact : e.GetCurrentPoint(StartPage).Properties.IsLeftButtonPressed)) return;
        for (var node = e.OriginalSource as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button)
            {
                if (button is Button source && button.DataContext is RecentCard card && button.Tag as string == "ShelfDocument")
                {
                    _dragSource = source;
                    _dragPointer = e.Pointer.PointerId;
                    _shelfDragStart = e.GetCurrentPoint(ShelfDragLayer).Position;
                    _dragCard = card;
                    if (touch)
                    {
                        ToolTipService.SetToolTip(source, null);
                        ArmTouchDrag(e.Pointer);
                    }
                }
                break;
            }
        }
    }

    private Border? FolderAt(Windows.Foundation.Point point)
    {
        return VisualTreeHelper.FindElementsInHostCoordinates(point, FolderLocations)
            .OfType<Border>().FirstOrDefault(border => border.AllowDrop && border.DataContext is FavoriteLocation);
    }

    private bool BeginShelfDrag(Pointer pointer, Windows.Foundation.Point position)
    {
        if (_dragCard == null) return false;
        _pendingTouch = null;
        // Keep the pressed button alive and captured until the drop completes.
        var owner = _dragSource;
        if (owner == null) return false;
        if (owner.PointerCaptures?.Any(p => p.PointerId == pointer.PointerId) != true && !owner.CapturePointer(pointer))
        {
            EndShelfDrag();
            return false;
        }
        _capturedDrag = pointer;
        ShelfDragName.Text = _dragCard.DisplayName;
        ShelfDragCover.Source = _dragCard.Thumbnail;
        ShelfDragFileIcon.Visibility = _dragCard.HasThumbnail ? Visibility.Collapsed : Visibility.Visible;
        ShelfDragOffset.X = PreviewPosition(position.X, ShelfDragPreview.Width, ShelfDragLayer.ActualWidth);
        ShelfDragOffset.Y = PreviewPosition(position.Y, ShelfDragPreview.Height, ShelfDragLayer.ActualHeight);
        ShelfDragPreview.Visibility = Visibility.Visible;
        return true;
    }

    private void ShelfPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragCard == null || e.Pointer.PointerId != _dragPointer) return;
        var point = e.GetCurrentPoint(ShelfDragLayer);
        if (!(e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch ? point.IsInContact : point.Properties.IsLeftButtonPressed)) { EndShelfDrag(); return; }
        var position = point.Position;
        if (ShelfDragPreview.Visibility != Visibility.Visible)
        {
            if (Math.Abs(position.X - _shelfDragStart.X) < 5 && Math.Abs(position.Y - _shelfDragStart.Y) < 5) return;
            // Moving before the hold completes leaves touch scrolling with the list.
            if (_pendingTouch != null)
            {
                UIElement.TryStartDirectManipulation(e.Pointer);
                EndShelfDrag();
                return;
            }
            if (!BeginShelfDrag(e.Pointer, position)) return;
        }
        UpdateShelfDrag(position);
    }

    private void UpdateShelfDrag(Windows.Foundation.Point position)
    {
        if (_dragCard == null) return;
        ShelfDragOffset.X = PreviewPosition(position.X, ShelfDragPreview.Width, ShelfDragLayer.ActualWidth);
        ShelfDragOffset.Y = PreviewPosition(position.Y, ShelfDragPreview.Height, ShelfDragLayer.ActualHeight);
        var tabPoint = ShelfDragLayer.TransformToVisual(FavoritesTab).TransformPoint(position);
        bool overTab = FavoritesTab.IsChecked != true && tabPoint.X >= 0 && tabPoint.X < FavoritesTab.ActualWidth &&
                       tabPoint.Y >= 0 && tabPoint.Y < FavoritesTab.ActualHeight;
        if (overTab) ArmFavoritesTab();
        else CancelFavoritesHover();
        var target = FolderBar.Visibility == Visibility.Visible ? FolderAt(ShelfDragLayer.TransformToVisual(RootGrid).TransformPoint(position)) : null;
        if (target == _hoverFolder) return;
        _hoverFolder?.ClearValue(Border.BorderBrushProperty);
        _hoverFolder = target;
        bool blocked = target?.DataContext is FavoriteLocation location && !_state.CanDropFavorite(_dragCard.Path, location.Id);
        ShelfDragBlocked.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        if (target != null && !blocked) HighlightFolder(target);
    }

    private static double PreviewPosition(double pointer, double size, double available) =>
        Math.Clamp(pointer + size + 14 <= available ? pointer + 14 : pointer - size - 14, 0, Math.Max(0, available - size));

    private void ArmFavoritesTab()
    {
        if (_hoveringFavorites) return;
        _hoveringFavorites = true;
        if (_favoritesHoverTimer == null)
        {
            _favoritesHoverTimer = DispatcherQueue.CreateTimer();
            _favoritesHoverTimer.Interval = TimeSpan.FromMilliseconds(350);
            _favoritesHoverTimer.IsRepeating = false;
            _favoritesHoverTimer.Tick += (_, _) =>
            {
                if (!_hoveringFavorites) return;
                if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled &&
                    VisualTreeHelper.GetChildrenCount(FavoritesTab) > 0 &&
                    VisualTreeHelper.GetChild(FavoritesTab, 0) is FrameworkElement template &&
                    template.FindName("DragTint") is Border tint)
                {
                    Storyboard.SetTarget(FavoritesTabPulse.Children[0], tint);
                    FavoritesTabPulse.Begin();
                }
                else ActivateHoveredFavorites();
            };
        }
        VisualStateManager.GoToState(FavoritesTab, "DragHover", false);
        _favoritesHoverTimer.Start();
    }

    private void FavoritesTabPulse_Completed(object sender, object e) => ActivateHoveredFavorites();

    private void ActivateHoveredFavorites()
    {
        if (!_hoveringFavorites) return;
        CancelFavoritesHover();
        if (_capturedDrag == null || !StartPage.IsLoaded) return;
        _retainedRecentView = ShelfListSurface.Visibility == Visibility.Visible ? ShelfListSurface : RecentThumbs;
        ShelfDragBackdrop.Visibility = Visibility.Visible;
        FavoritesTab.IsChecked = true;
    }

    private void CancelFavoritesHover()
    {
        if (!_hoveringFavorites) return;
        _hoveringFavorites = false;
        _favoritesHoverTimer?.Stop();
        FavoritesTabPulse.Stop();
        VisualStateManager.GoToState(FavoritesTab, "DragIdle", false);
    }

    private void EndShelfDrag()
    {
        CancelFavoritesHover();
        if (_retainedRecentView != null)
        {
            _retainedRecentView = null;
            UpdateShelfSection();
            ShelfDragBackdrop.Visibility = Visibility.Collapsed;
        }
        _touchHoldTimer?.Stop();
        _pendingTouch = null;
        var pointer = _capturedDrag;
        _capturedDrag = null;
        if (pointer != null) _dragSource?.ReleasePointerCapture(pointer);
        if (_shelfPressWasTouch && _dragSource is { DataContext: RecentCard sourceCard } source)
            ToolTipService.SetToolTip(source, sourceCard.Path);
        _dragSource = null;
        _dragCard = null;

        ShelfDragBlocked.Visibility = Visibility.Collapsed;
        ShelfDragPreview.Visibility = Visibility.Collapsed;
        ShelfDragCover.Source = null;
        _hoverFolder?.ClearValue(Border.BorderBrushProperty);
        _hoverFolder = null;
    }

    private void ShelfPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragCard == null || e.Pointer.PointerId != _dragPointer) return;
        var card = _dragCard;
        bool dragging = _capturedDrag != null;
        var target = dragging && FolderBar.Visibility == Visibility.Visible ? FolderAt(e.GetCurrentPoint(RootGrid).Position) : null;
        EndShelfDrag();
        if (dragging) e.Handled = true;
        if (target?.DataContext is not FavoriteLocation location) return;
        FileDroppedFavorites([card.Path], location);
    }

    private static void HighlightFolder(Border target) =>
        target.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    private void FileDroppedFavorites(IEnumerable<string> paths, FavoriteLocation location)
    {
        if (!_state.AddDroppedFavorites(paths, location.Id)) return;
        _folderFilter = location.Id;
        FavoritesTab.IsChecked = true;
        _store.Save(_state);
        PopulateRecents();
    }

    private static async Task<string[]> ExternalPdfPathsAsync(DataPackageView data)
    {
        var items = await data.GetStorageItemsAsync();
        return items.OfType<StorageFile>().Select(file => file.Path).ToArray();
    }

    private async void Folder_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        var target = (Border)sender;

        if (target.DataContext is not FavoriteLocation location ||
            !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var pending = target.Tag as Task<string[]> ?? ExternalPdfPathsAsync(e.DataView);
            target.Tag = pending;
            var paths = await pending;
            if (!ReferenceEquals(target.Tag, pending) || !Equals(target.DataContext, location)) return;
            if (!paths.Any(path => _state.CanDropFavorite(path, location.Id))) return;
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = location.Name;
            HighlightFolder(target);
        }
        catch (Exception) { e.AcceptedOperation = DataPackageOperation.None; }
        finally { deferral.Complete(); }
    }

    private void Folder_DragLeave(object sender, DragEventArgs e)
    {
        ((Border)sender).Tag = null;
        ((Border)sender).ClearValue(Border.BorderBrushProperty);
        e.Handled = true;
    }

    private async void Folder_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = (Border)sender;
        target.Tag = null;
        target.ClearValue(Border.BorderBrushProperty);
        if (target.DataContext is not FavoriteLocation location) return;

        var deferral = e.GetDeferral();
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            FileDroppedFavorites(items.OfType<StorageFile>().Select(file => file.Path), location);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { deferral.Complete(); }
    }

    private MenuFlyout FolderActionsMenu(Guid id)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(FolderMenuItem(UiText.Current.RenameFolder, Symbol.Rename, () => _ = EditFolderAsync(id)));
        menu.Items.Add(FolderMenuItem(UiText.Current.DeleteFolder, Symbol.Delete, () => _ = DeleteFolderAsync(id)));
        return menu;
    }

    private void FolderLocation_Prepared(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.ContextFlyout = !args.InRecycleQueue && args.Item is FavoriteLocation { Id: Guid id } && id != Guid.Empty
            ? FolderActionsMenu(id) : null;
    }

    private void NewFavoriteFolder_Click(object sender, RoutedEventArgs args) => _ = EditFolderAsync(null);

    private static ContentDialog FavoriteDialog(string title, string glyph, string primary)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        heading.Children.Add(new FontIcon { Glyph = glyph, FontSize = 22, VerticalAlignment = VerticalAlignment.Center });
        heading.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var dialog = new ContentDialog
        {
            Title = heading, PrimaryButtonText = primary, CloseButtonText = UiText.Current.Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetName(dialog, title);
        return dialog;
    }

    private (StackPanel Panel, TextBox Box) FolderNameEditor(ContentDialog dialog, Guid? id)
    {
        var ui = UiText.Current;
        var box = new TextBox { Header = ui.FolderName, PlaceholderText = ui.FolderNameExample, MaxLength = 80,
            Text = id.HasValue ? _state.FavoriteFolders[id.Value] : "" };
        var error = new TextBlock { Text = ui.FolderNameTaken, Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["ValidationErrorTextStyle"] };
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        box.TextChanged += (_, _) =>
        {
            var name = box.Text.Trim();
            bool duplicate = _state.FolderNameTaken(id, name);
            error.Visibility = duplicate ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = name.Length > 0 && !duplicate;
        };
        return (new StackPanel { Spacing = 6, MinWidth = 290, Children = { box, error } }, box);
    }

    private async Task EditFolderAsync(Guid? id)
    {
        var ui = UiText.Current;
        if (id.HasValue && !_state.FavoriteFolders.ContainsKey(id.Value)) return;
        var dialog = FavoriteDialog(id.HasValue ? ui.RenameFolder : ui.NewFavoriteFolder, FolderGlyph, id.HasValue ? ui.Save : ui.Create);
        var (panel, box) = FolderNameEditor(dialog, id);
        dialog.Content = panel;
        dialog.IsPrimaryButtonEnabled = false;
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        if (!TrySaveFolderName(id, box.Text, out Guid folder)) return;
        _folderFilter = folder;
        FavoritesTab.IsChecked = true;
        SaveFolderChanges();
    }

    private bool TrySaveFolderName(Guid? existing, string name, out Guid folder)
    {
        if (_state.TrySetFolderName(existing, name, out folder)) return true;
        ShowError(existing.HasValue && !_state.FavoriteFolders.ContainsKey(folder)
            ? UiText.Current.FolderRemoved : UiText.Current.FolderNameTaken);
        return false;
    }

    private async Task ChooseFavoriteAsync(RecentCard card)
    {
        var ui = UiText.Current;
        var dialog = FavoriteDialog(card.IsFavorite ? ui.MoveFavorite : ui.AddToFavorites, "\uE734", ui.Save);
        if (card.IsFavorite) dialog.SecondaryButtonText = ui.Unfavorite;
        var current = _state.FindRecent(card.Path)?.FavoriteFolderId ?? Guid.Empty;
        var rows = Locations(false);
        var create = new FavoriteLocation(null, ui.NewFavoriteFolder, "\uE710", "");
        rows.Add(create);
        var list = new ListView
        {
            ItemsSource = rows, SelectedItem = rows.FirstOrDefault(r => r.Id == current) ?? rows[0],
            ItemTemplate = (DataTemplate)RootGrid.Resources["FavoriteLocationTemplate"],
            ItemContainerStyle = (Style)RootGrid.Resources["FavoriteLocationStyle"],
            MaxHeight = 260, CornerRadius = new CornerRadius(6)
        };
        AutomationProperties.SetName(list, ui.FavoriteFoldersLabel);
        var (editor, box) = FolderNameEditor(dialog, null);
        editor.Visibility = Visibility.Collapsed;
        list.SelectionChanged += (_, _) =>
        {
            bool creating = Equals(list.SelectedItem, create);
            editor.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
            if (creating)
            {
                box.Text = "";
                box.Focus(FocusState.Programmatic);
            }
            dialog.IsPrimaryButtonEnabled = !creating;
        };
        var filename = new TextBlock { Text = card.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        ToolTipService.SetToolTip(filename, card.DisplayName);
        dialog.Content = new StackPanel { MinWidth = 310, Spacing = 12, Children = { filename, list, editor } };
        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Secondary) SetFavorite(card, false, null);
        if (result != ContentDialogResult.Primary || list.SelectedItem is not FavoriteLocation location) return;
        Guid? folder = location.Id;
        if (location == create)
        {
            if (!TrySaveFolderName(null, box.Text, out var created)) return;
            folder = created;
        }
        else if (folder is Guid selected && selected != Guid.Empty && !_state.FavoriteFolders.ContainsKey(selected))
        {
            ShowError(ui.FolderRemoved);
            return;
        }
        SetFavorite(card, true, folder == Guid.Empty ? null : folder);
    }

    private async Task DeleteFolderAsync(Guid id)
    {
        var ui = UiText.Current;
        if (!_state.FavoriteFolders.TryGetValue(id, out var name)) return;
        var dialog = new ContentDialog
        {
            Title = ui.DeleteFolder,
            Content = ui.ConfirmDeleteFolder(name),
            PrimaryButtonText = ui.Delete,
            CloseButtonText = ui.Cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        _state.RemoveFavoriteFolder(id);
        SaveFolderChanges();
    }
}
