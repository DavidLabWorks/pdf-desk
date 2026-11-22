using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PDFDesk.Controls;
using PDFDesk.Engine;
using PDFDesk.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PDFDesk;

public sealed partial class MainWindow
{
    // ---------------------------------------------------------------- drag & drop

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFile file &&
                file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                OpenOrActivate(file.Path);
            }
        }
    }

    // ---------------------------------------------------------------- recents

    private readonly ThumbnailCache _thumbnails = new();

    private void ShelfSection_Checked(object sender, RoutedEventArgs args)
    {
        if (StartPage?.IsLoaded != true) return;
        RecentSelectionMode.IsChecked = false;
        bool favorites = ReferenceEquals(sender, FavoritesTab);
        if (_state.Settings.FavoritesTabSelected != favorites)
        {
            _state.Settings.FavoritesTabSelected = favorites;
            _store.Save(_state);
        }
        UpdateShelfSection();
    }

    private IEnumerable<RecentCard> RecentCards => RecentThumbs.Items.OfType<RecentCard>();

    private IEnumerable<RecentCard> VisibleShelfCards => FavoritesTab.IsChecked == true
        ? FavoriteThumbs.Items.OfType<RecentCard>() : RecentCards;

    private void RecentSelectionMode_Click(object sender, RoutedEventArgs args) => UpdateShelfSection();

    private void RecentSelection_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox { DataContext: RecentCard card } box) return;
        card.IsSelected = box.IsChecked == true;
        UpdateRecentSelectionBar();
    }

    private void AllRecent_Click(object sender, RoutedEventArgs args) =>
        SetRecentSelection(VisibleShelfCards.Any(card => !card.IsSelected));

    private void SetRecentSelection(bool selected)
    {
        foreach (var card in VisibleShelfCards) card.IsSelected = selected;
        UpdateRecentSelectionBar();
    }

    private void AlsoDeleteRecentFiles_Click(object sender, RoutedEventArgs args) => UpdateRecentRemovalLabel();

    private void UpdateRecentRemovalLabel()
    {
        bool files = AlsoDeleteRecentFiles.IsChecked == true;
        var ui = UiText.Current;
        bool favorites = FavoritesTab.IsChecked == true;
        PDFDesk.Styles.DestructiveAction.SetIsEnabled(RemoveSelectedRecent, files || !favorites);
        RecentRemovalLabel.Text = files ? ui.DeleteFiles : favorites ? ui.Unfavorite : ui.RemovePages;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RemoveSelectedRecent, files ? ui.DeleteFiles : favorites ? ui.Unfavorite : ui.RemoveFromList);
        ToolTipService.SetToolTip(RemoveSelectedRecent, files ? ui.DeleteFiles : favorites ? ui.Unfavorite : ui.RemoveRecentHint);
    }

    private async void RemoveSelectedRecent_Click(object sender, RoutedEventArgs args)
    {
        var cards = VisibleShelfCards.Where(card => card.IsSelected).ToArray();
        if (cards.Length == 0) return;
        if (AlsoDeleteRecentFiles.IsChecked != true)
        {
            if (FavoritesTab.IsChecked == true) UnfavoriteCards(cards);
            else ForgetCards(cards);
            return;
        }

        var ui = UiText.Current;
        var dialog = new RecycleFilesDialog(cards);
        await ShowDialogAsync(dialog);
        if (!dialog.Confirmed) return;
        var removed = new List<RecentCard>();
        var failed = new List<string>();
        foreach (var card in cards)
        {
            try
            {
                if (MatchingTabs(new WindowsFileIdentity.NavigationKey(card.Path)).Any())
                    throw new IOException(ui.ClosePdfFirst);
                if (!RecycleBin.DeletePdf(card.Path, WinRT.Interop.WindowNative.GetWindowHandle(this))) break;
                removed.Add(card);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failed.Add($"{card.Path} — {ex.Message}");
            }
        }
        AlsoDeleteRecentFiles.IsChecked = false;
        UpdateRecentRemovalLabel();
        if (removed.Count > 0) ForgetCards(removed);
        if (failed.Count > 0) ShowError(string.Join(Environment.NewLine, failed));
    }

    private void UpdateRecentSelectionBar()
    {
        int count = VisibleShelfCards.Count(card => card.IsSelected);
        RemoveSelectedRecent.IsEnabled = AlsoDeleteRecentFiles.IsEnabled = count > 0;
        if (count == 0) AlsoDeleteRecentFiles.IsChecked = false;
        UpdateRecentRemovalLabel();
        AllRecentBox.IsChecked = count == 0 ? false : count == VisibleShelfCards.Count() ? true : null;
        RecentSelectionCount.Text = UiText.Current.RecentSelectedCount(count);
        SetSelectionBarExpanded(RecentSelectionMode.IsChecked == true);
    }

    private bool _selectionBarExpanded;
    private Storyboard? _selectionBarMotion;

    private void SetSelectionBarExpanded(bool expanded)
    {
        if (_selectionBarExpanded == expanded) return;
        _selectionBarExpanded = expanded;
        var reveal = RecentSelectionReveal;
        double width = reveal.ActualWidth;
        double opacity = reveal.Visibility == Visibility.Visible ? reveal.Opacity : 0;
        _selectionBarMotion?.Stop();
        _selectionBarMotion = null;
        reveal.Visibility = Visibility.Visible;
        RecentSelectionBar.IsHitTestVisible = expanded;
        void Finish()
        {
            reveal.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            reveal.Width = expanded ? double.NaN : 0;
            reveal.Opacity = 1;
        }
        if (!reveal.IsLoaded || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            Finish();
            return;
        }

        RecentSelectionBar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var motion = new Storyboard();
        void Animate(string property, double from, double to, int milliseconds)
        {
            var animation = new DoubleAnimation
            {
                From = from, To = to, Duration = TimeSpan.FromMilliseconds(milliseconds),
                EnableDependentAnimation = property == "Width",
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, reveal);
            Storyboard.SetTargetProperty(animation, property);
            motion.Children.Add(animation);
        }
        Animate("Width", width, expanded ? RecentSelectionBar.DesiredSize.Width : 0, expanded ? 190 : 150);
        Animate("Opacity", opacity, expanded ? 1 : 0, expanded ? 180 : 95);
        _selectionBarMotion = motion;
        motion.Completed += (_, _) =>
        {
            if (_selectionBarMotion != motion) return;
            motion.Stop();
            _selectionBarMotion = null;
            Finish();
        };
        motion.Begin();
    }

    private void UnfavoriteCards(IReadOnlyList<RecentCard> cards)
    {
        var paths = cards.Select(card => card.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _state.Recents.Where(entry => paths.Contains(entry.Path)))
        {
            entry.IsFavorite = false;
            entry.FavoriteFolderId = null;
        }
        _store.Save(_state);
        foreach (var window in App.Readers)
        {
            foreach (var card in window.RecentCards.Where(card => paths.Contains(card.Path)))
                card.IsFavorite = false;
            window.RemoveShelfCards(window.FavoriteThumbs, paths);
            window.UpdateShelfSection();
        }
    }

    private void ForgetCards(IReadOnlyList<RecentCard> cards)
    {
        foreach (var card in cards)
        {
            _state.ForgetRecent(card.Path);
            _thumbnails.Forget(card.Path);
        }
        _store.Save(_state);
        var paths = cards.Select(card => card.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var window in App.Readers)
        {
            window.RemoveShelfCards(window.RecentThumbs, paths);
            window.RemoveShelfCards(window.FavoriteThumbs, paths);
            window.UpdateShelfSection();
        }
    }

    private void RemoveShelfCards(ListViewBase view, HashSet<string> paths)
    {
        if (view.ItemsSource is not ObservableCollection<RecentCard> items) return;
        for (int i = items.Count - 1; i >= 0; i--)
            if (paths.Contains(items[i].Path)) items.RemoveAt(i);
    }

    private void PopulateRecents()
    {
        var selected = RecentCards.Where(card => card.IsSelected).Select(card => card.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cards = _state.Recents
            .Select(r => new RecentCard(r.Path, r.DisplayName, r.LastOpenedUtc, r.IsFavorite) { IsSelected = selected.Contains(r.Path) }).ToList();
        RecentThumbs.ItemsSource = new ObservableCollection<RecentCard>(OrderShelfCards(cards));
        ShelfList.ItemsSource = RecentThumbs.ItemsSource;
        FilterFavorites();
        UpdateShelfSection();
    }

    private void ShelfSortMenu_Opening(object sender, object args)
    {
        var settings = _state.Settings;
        SortName.IsChecked = settings.ShelfListSortByName;
        SortOpened.IsChecked = !SortName.IsChecked;
        SortAscending.IsChecked = settings.ShelfListSortAscending;
        SortDescending.IsChecked = !SortAscending.IsChecked;
        SortAscending.Text = UiText.Current.ShelfSortDirection(SortName.IsChecked, true);
        SortDescending.Text = UiText.Current.ShelfSortDirection(SortName.IsChecked, false);
    }

    private void ShelfSort_Click(object sender, RoutedEventArgs args)
    {
        var settings = _state.Settings;
        if (ReferenceEquals(sender, SortName) || ReferenceEquals(sender, SortOpened))
        {
            bool byName = ReferenceEquals(sender, SortName);
            if (settings.ShelfListSortByName == byName) return;
            settings.ShelfListSortByName = byName;
            settings.ShelfListSortAscending = byName;
        }
        else
        {
            bool ascending = ReferenceEquals(sender, SortAscending);
            if (settings.ShelfListSortAscending == ascending) return;
            settings.ShelfListSortAscending = ascending;
        }
        _store.Save(_state);
        foreach (var window in App.Readers)
        {
            window.ApplyShelfOrder(window.RecentThumbs);
            window.ApplyShelfOrder(window.FavoriteThumbs);
            bool favorites = window.FavoritesTab.IsChecked == true;
            ListViewBase view = window.ShelfListMode.IsChecked == true
                ? (favorites ? window.FavoriteList : window.ShelfList)
                : (favorites ? window.FavoriteThumbs : window.RecentThumbs);
            if (window.StartPage.Visibility == Visibility.Visible && view.Items.Count > 0)
            {
                view.UpdateLayout();
                view.ScrollIntoView(view.Items[0], ScrollIntoViewAlignment.Leading);
            }
        }
    }

    private IEnumerable<RecentCard> OrderShelfCards(IEnumerable<RecentCard> source)
    {
        var settings = _state.Settings;
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        var ordered = settings.ShelfListSortByName
            ? (settings.ShelfListSortAscending ? source.OrderBy(c => c.DisplayName, comparer) : source.OrderByDescending(c => c.DisplayName, comparer))
            : (settings.ShelfListSortAscending ? source.OrderBy(c => c.LastOpenedUtc) : source.OrderByDescending(c => c.LastOpenedUtc));
        return ordered.ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase);
    }

    private void ApplyShelfOrder(ListViewBase view)
    {
        if (view.ItemsSource is not ObservableCollection<RecentCard> items) return;
        var desired = OrderShelfCards(items).ToArray();
        for (int i = 0; i < desired.Length; i++)
            if (items[i] != desired[i]) items.Move(items.IndexOf(desired[i]), i);
    }

    private void UpdateShelfSection()
    {
        bool any = RecentThumbs.Items.Count > 0;
        bool favorites = FavoritesTab.IsChecked == true;
        bool list = favorites ? _state.Settings.FavoritesUseList : _state.Settings.RecentUsesList;
        var scope = VisibleShelfCards.ToHashSet();
        RecentSelectionGroup.Visibility = scope.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (scope.Count == 0) RecentSelectionMode.IsChecked = false;
        bool selecting = RecentSelectionMode.IsChecked == true;
        foreach (var card in RecentCards)
        {
            card.CanSelect = selecting && scope.Contains(card);
            if (!card.CanSelect) card.IsSelected = false;
        }
        UpdateRecentSelectionBar();
        FolderBar.Visibility = favorites ? Visibility.Visible : Visibility.Collapsed;
        if (favorites) RefreshFolderLocations();
        (list ? ShelfListMode : ShelfCardMode).IsChecked = true;
        RecentsHeading.Visibility = Visibility.Visible;
        EmptyState.Visibility = !any && !favorites ? Visibility.Visible : Visibility.Collapsed;
        RecentThumbs.Visibility = _retainedRecentView == RecentThumbs || (!list && !favorites && any) ? Visibility.Visible : Visibility.Collapsed;
        FavoriteThumbs.Visibility = !list && favorites ? Visibility.Visible : Visibility.Collapsed;
        bool showRecentList = !favorites && list && any;
        bool showFavoriteList = favorites && list;
        ShelfListSurface.Visibility = showRecentList || _retainedRecentView == ShelfListSurface ? Visibility.Visible : Visibility.Collapsed;
        FavoriteListSurface.Visibility = showFavoriteList ? Visibility.Visible : Visibility.Collapsed;
        ShelfSortButton.IsEnabled = (favorites ? FavoriteThumbs.Items.Count : RecentThumbs.Items.Count) > 1;
        if (favorites && FavoriteThumbs.Items.Count == 0)
        {
            bool filtered = _folderFilter.HasValue;
            FavoriteEmptyTitle.Text = filtered ? UiText.Current.EmptyFavoriteFolder : UiText.Current.NoFavoritesYet;
            FavoritesHint.Text = filtered ? UiText.Current.EmptyFolderHint :
                any ? UiText.Current.SelectTheStarOnAPDFInRecent : UiText.Current.OpenAPDFThenStarItInRecent;
        }
        NoFavorites.Visibility = favorites && FavoriteThumbs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShelfViewMode_Checked(object sender, RoutedEventArgs e)
    {
        bool list = ReferenceEquals(sender, ShelfListMode);
        bool favorites = FavoritesTab.IsChecked == true;
        if ((favorites ? _state.Settings.FavoritesUseList : _state.Settings.RecentUsesList) == list) return;

        if (favorites)
            _state.Settings.FavoritesUseList = list;
        else
            _state.Settings.RecentUsesList = list;
        _store.Save(_state);
        UpdateShelfSection();
    }

    private void FavoriteStar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: RecentCard card } star) return;
        star.IsChecked = card.IsFavorite;
        _ = ChooseFavoriteAsync(card);
    }

    private void RecentMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Parent: FrameworkElement row } button)
            FlyoutBase.GetAttachedFlyout(row)?.ShowAt(button, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            });
    }

    private void RecentCard_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not RecentCard card || card.PreviewRequested) return;
        card.PreviewRequested = true;
        _ = card.LoadFileSizeAsync();
        _ = LoadThumbnailAsync(card);
    }

    private async Task LoadThumbnailAsync(RecentCard card)
    {
        byte[]? png = await _thumbnails.GetAsync(card.Path);
        if (png is null)
        {
            return;
        }
        // Decode on the UI thread into a BitmapImage.
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
        {
            using (var writer = new Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
        }
        card.Thumbnail = bitmap;
    }

    private void RecentName_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is TextBlock label && label.DataContext is RecentCard card)
            FitRecentName(label, card.DisplayName);
    }

    private void RecentName_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
    {
        if (sender is TextBlock label && e.NewValue is RecentCard card)
            FitRecentName(label, card.DisplayName);
    }

    private static void FitRecentName(TextBlock label, string name)
    {
        if (label.ActualWidth <= 0)
        {
            label.Text = name;
            return;
        }

        var probe = new TextBlock
        {
            FontFamily = label.FontFamily, FontSize = label.FontSize, FontWeight = label.FontWeight,
            LineHeight = label.LineHeight, TextWrapping = label.TextWrapping, Text = "A\nA"
        };
        var available = new Windows.Foundation.Size(label.ActualWidth, double.PositiveInfinity);
        probe.Measure(available);
        double twoLines = probe.DesiredSize.Height;
        // Reserve the same title space for every card, including one-line names.
        label.Height = Math.Ceiling(twoLines);
        bool Fits(string text)
        {
            probe.Text = text;
            probe.Measure(available);
            return probe.DesiredSize.Height <= twoLines;
        }

        label.Text = DocumentNameFormatter.Fit(name, Fits);
    }

    private sealed class MissingFileDialog : ContentDialog
    {
        public MissingFileDialog()
        {
            // Keep initial keyboard focus on Cancel without applying the default-button accent.
            Opened += (_, _) => (GetTemplateChild("CloseButton") as Button)?.Focus(FocusState.Programmatic);
        }
    }

    private async void RecentOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedDrag != null) return;
        if (sender is Button { DataContext: RecentCard card })
        {
            if (card.CanSelect)
            {
                card.IsSelected = !card.IsSelected;
                UpdateRecentSelectionBar();
                return;
            }
            if (File.Exists(card.Path))
            {
                OpenOrActivate(card.Path);
            }
            else
            {
                bool fromFavorites = FavoritesTab.IsChecked == true;
                var ui = UiText.Current;
                var dialog = new MissingFileDialog
                {
                    Title = ui.FileMovedOrRenamed,
                    Content = UiText.Format("Locate {0} to keep its reading position and bookmarks.", card.DisplayName)
                        + "\n\n" + (fromFavorites ? ui.MissingUnfavoriteHint : ui.RemoveRecentHint),
                    PrimaryButtonText = ui.LocatePDF,
                    SecondaryButtonText = fromFavorites ? ui.Unfavorite : ui.RemoveRecord,
                    CloseButtonText = ui.Cancel,
                    PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
                };
                if (!fromFavorites)
                    dialog.SecondaryButtonStyle = (Style)Application.Current.Resources["DestructiveDialogButtonStyle"];

                var result = await ShowDialogAsync(dialog);
                if (result == ContentDialogResult.Secondary)
                {
                    if (fromFavorites) UnfavoriteCards([card]);
                    else ForgetCards([card]);
                    return;
                }
                if (result != ContentDialogResult.Primary) return;
                var picked = await FilePickerHost.PickOpenAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), ".pdf");
                if (picked.Failed) { ShowError(FilePickerHost.FailureMessage); return; }
                if (picked.File is not { } file || _state.FindRecent(card.Path) is not { } entry) return;
                if (_state.FindRecent(file.Path) is { } other && other != entry)
                {
                    ShowError(UiText.Translate("That PDF already has a shelf entry. Choose the original document."));
                    return;
                }
                _thumbnails.Forget(card.Path);
                entry.Path = file.Path;
                entry.DisplayName = file.Name;
                entry.IsLibraryCopy = _library.Contains(file.Path);
                _state.RelocateSessionPath(card.Path, file.Path);
                _store.Save(_state);
                PopulateRecents();
                OpenOrActivate(file.Path);
            }
        }
    }

    /// <summary>
    /// Opens the recent document's actions in its native context menu.
    ///
    /// Removal also deletes the cached first-page thumbnail. That PNG is a
    /// picture of the document, so leaving it on disk would keep the contents of
    /// a file the user just asked PDFDesk to forget.
    /// </summary>
    private void RecentMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout { Target.DataContext: RecentCard card } flyout)
        {
            return;
        }
        bool managed = _library.Contains(card.Path) || _state.FindRecent(card.Path)?.IsLibraryCopy == true;
        var location = new MenuFlyoutItem { Text = managed ? UiText.Current.OpenCopyLocation : UiText.Current.OpenFileLocation, Icon = new FontIcon { Glyph = "\uE838" } }; // FolderOpen
        location.Click += (_, _) => _ = OpenFileLocationAsync(card.Path);

        var copy = new MenuFlyoutItem { Text = UiText.Current.CopyFilePath, Icon = new SymbolIcon(Symbol.Copy) };
        copy.Click += (_, _) => _ = CopyFilePathAsync(card.Path);

        var forget = new MenuFlyoutItem { Text = UiText.Current.RemoveFromList, Icon = new SymbolIcon(Symbol.Delete) };
        PDFDesk.Styles.DestructiveAction.SetIsEnabled(forget, true);
        // Bookmarks hang off the recent entry, so forgetting the file forgets
        // them. Say so rather than discovering it later.
        int bookmarks = _state.FindRecent(card.Path)?.Bookmarks.Count ?? 0;
        if (bookmarks > 0)
        {
            forget.Text = bookmarks == 1
                ? UiText.Translate("Remove from list (discards 1 bookmark)")
                : UiText.Format("Remove from list (discards {0} bookmarks)", bookmarks);
        }
        forget.Click += (_, _) => ForgetCards([card]);

        flyout.Items.Clear();
        AddFavoriteActions(flyout, card);
        flyout.Items.Add(new MenuFlyoutSeparator());
        if (!managed)
        {
            var import = new MenuFlyoutItem { Text = UiText.Current.CopyToLibrary, Icon = new SymbolIcon(Symbol.Library) };
            import.Click += async (_, _) => await OpenLibraryCopyAsync(card.Path);
            flyout.Items.Add(import);
        }
        var compress = new MenuFlyoutItem { Text = UiText.Current.CompressExport, Icon = new SymbolIcon(Symbol.Download) };
        compress.Click += (_, _) => _ = CompressExportAsync(card.Path);
        flyout.Items.Add(compress);
        flyout.Items.Add(location);
        flyout.Items.Add(copy);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(forget);
    }
}
