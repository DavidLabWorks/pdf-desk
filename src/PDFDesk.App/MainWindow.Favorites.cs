using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
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
        var filter = _state.Settings.FavoriteFolderFilter;
        if (filter.HasValue && filter != Guid.Empty && !_state.FavoriteFolders.ContainsKey(filter.Value))
            filter = _state.Settings.FavoriteFolderFilter = null;

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
                .FirstOrDefault(row => favorites && row.Id == _state.Settings.FavoriteFolderFilter);
        }
        finally { _refreshingLocations = false; }
    }

    private void FolderLocation_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_refreshingLocations || FolderLocations.SelectedItem is not FavoriteLocation location) return;
        _state.Settings.FavoriteFolderFilter = location.Id;
        FavoritesTab.IsChecked = true;
        SaveFolderChanges();
    }

    private RecentCard? _dragCard;
    private uint _dragPointer;
    private Windows.Foundation.Point _shelfDragStart;
    private Border? _hoverFolder;

    private void AttachShelfDragHandlers()
    {
        // Shelf buttons capture the pointer. Listen to handled events as well so a
        // release over the folder can be resolved even during a fast mouse gesture.
        StartPage.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ShelfPointerPressed), true);
        StartPage.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(ShelfPointerMoved), true);
        StartPage.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ShelfPointerReleased), true);
        StartPage.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => EndShelfDrag()), true);
        StartPage.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) =>
        {
            if (_dragCard is not { } card || e.Pointer.PointerId != _dragPointer) return;
            // A button may release capture before the routed release reaches StartPage.
            DispatcherQueue.TryEnqueue(() => { if (ReferenceEquals(_dragCard, card)) EndShelfDrag(); });
        }), true);
    }

    private void ShelfPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        EndShelfDrag();
        if (FolderBar.Visibility != Visibility.Visible || !e.GetCurrentPoint(StartPage).Properties.IsLeftButtonPressed) return;
        for (var node = e.OriginalSource as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button)
            {
                if (button is Button && button.DataContext is RecentCard card && button.Tag as string == "ShelfDocument")
                {
                    _dragPointer = e.Pointer.PointerId;
                    _shelfDragStart = e.GetCurrentPoint(ShelfDragLayer).Position;
                    _dragCard = card;
                }
                break;
            }
        }
    }

    private Border? FolderAt(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RootGrid).Position;
        return VisualTreeHelper.FindElementsInHostCoordinates(point, FolderLocations)
            .OfType<Border>().FirstOrDefault(border => border.AllowDrop && border.DataContext is FavoriteLocation);
    }

    private void ShelfPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragCard == null || e.Pointer.PointerId != _dragPointer) return;
        var point = e.GetCurrentPoint(ShelfDragLayer);
        if (!point.Properties.IsLeftButtonPressed) { EndShelfDrag(); return; }
        var position = point.Position;
        if (ShelfDragPreview.Visibility != Visibility.Visible)
        {
            if (Math.Abs(position.X - _shelfDragStart.X) < 5 && Math.Abs(position.Y - _shelfDragStart.Y) < 5) return;
            ShelfDragName.Text = _dragCard.DisplayName;
            ShelfDragCover.Source = _dragCard.Thumbnail;
            ShelfDragFileIcon.Visibility = _dragCard.HasThumbnail ? Visibility.Collapsed : Visibility.Visible;
            ShelfDragPreview.Visibility = Visibility.Visible;
        }
        ShelfDragOffset.X = PreviewPosition(position.X, ShelfDragPreview.Width, ShelfDragLayer.ActualWidth);
        ShelfDragOffset.Y = PreviewPosition(position.Y, ShelfDragPreview.Height, ShelfDragLayer.ActualHeight);
        var target = FolderAt(e);
        if (target == _hoverFolder) return;
        _hoverFolder?.ClearValue(Border.BorderBrushProperty);
        _hoverFolder = target;
        bool blocked = target?.DataContext is FavoriteLocation location && !_state.CanDropFavorite(_dragCard.Path, location.Id);
        ShelfDragBlocked.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        if (target != null && !blocked) HighlightFolder(target);
    }

    private static double PreviewPosition(double pointer, double size, double available) =>
        Math.Clamp(pointer + size + 14 <= available ? pointer + 14 : pointer - size - 14, 0, Math.Max(0, available - size));

    private void EndShelfDrag()
    {
        ShelfDragBlocked.Visibility = Visibility.Collapsed;
        ShelfDragPreview.Visibility = Visibility.Collapsed;
        ShelfDragCover.Source = null;
        _hoverFolder?.ClearValue(Border.BorderBrushProperty);
        _hoverFolder = null;
        _dragCard = null;
    }

    private void ShelfPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragCard == null || e.Pointer.PointerId != _dragPointer) return;
        var card = _dragCard;
        var target = FolderAt(e);
        EndShelfDrag();
        if (target?.DataContext is not FavoriteLocation location) return;
        e.Handled = true;
        FileDroppedFavorites([card.Path], location);
    }

    private static void HighlightFolder(Border target) =>
        target.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    private void FileDroppedFavorites(IEnumerable<string> paths, FavoriteLocation location)
    {
        if (!_state.AddDroppedFavorites(paths, location.Id)) return;
        _state.Settings.FavoriteFolderFilter = location.Id;
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
            bool duplicate = _state.FavoriteFolders.Any(p => p.Key != id && string.Equals(p.Value, name, StringComparison.OrdinalIgnoreCase));
            error.Visibility = duplicate ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = name.Length > 0 && !duplicate;
        };
        return (new StackPanel { Spacing = 6, MinWidth = 290, Children = { box, error } }, box);
    }

    private async Task EditFolderAsync(Guid? id)
    {
        var ui = UiText.Current;
        var dialog = FavoriteDialog(id.HasValue ? ui.RenameFolder : ui.NewFavoriteFolder, FolderGlyph, id.HasValue ? ui.Save : ui.Create);
        var (panel, box) = FolderNameEditor(dialog, id);
        dialog.Content = panel;
        dialog.IsPrimaryButtonEnabled = false;
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        Guid folder = id ?? Guid.NewGuid();
        _state.FavoriteFolders[folder] = box.Text.Trim();
        _state.Settings.FavoriteFolderFilter = folder;
        FavoritesTab.IsChecked = true;
        SaveFolderChanges();
    }

    private async Task ChooseFavoriteAsync(RecentCard card)
    {
        var ui = UiText.Current;
        var dialog = FavoriteDialog(card.IsFavorite ? ui.MoveFavorite : ui.AddToFavorites, "\uE734", ui.Save);
        if (card.IsFavorite) dialog.SecondaryButtonText = ui.RemoveFromFavorites;
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
            folder = Guid.NewGuid();
            _state.FavoriteFolders[folder.Value] = box.Text.Trim();
        }
        SetFavorite(card, true, folder == Guid.Empty ? null : folder);
    }

    private async Task DeleteFolderAsync(Guid id)
    {
        var ui = UiText.Current;
        var dialog = new ContentDialog
        {
            Title = ui.DeleteFolder,
            Content = ui.ConfirmDeleteFolder(_state.FavoriteFolders[id]),
            PrimaryButtonText = ui.Delete,
            CloseButtonText = ui.Cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        _state.RemoveFavoriteFolder(id);
        SaveFolderChanges();
    }
}
