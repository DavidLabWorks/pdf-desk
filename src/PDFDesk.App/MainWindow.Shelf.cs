using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
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
        bool favorites = ReferenceEquals(sender, FavoritesTab);
        if (_state.Settings.FavoritesTabSelected != favorites)
        {
            _state.Settings.FavoritesTabSelected = favorites;
            _store.Save(_state);
        }
        UpdateShelfSection();
    }

    private void PopulateRecents()
    {
        var cards = _state.Recents
            .Select(r => new RecentCard(r.Path, r.DisplayName, r.LastOpenedUtc, r.IsFavorite)).ToList();
        RecentThumbs.ItemsSource = cards;
        FilterFavorites();
        UpdateShelfSection();
    }

    private void UpdateShelfSection()
    {
        bool any = RecentThumbs.Items.Count > 0;
        bool favorites = FavoritesTab.IsChecked == true;
        bool list = favorites ? _state.Settings.FavoritesUseList : _state.Settings.RecentUsesList;
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
        if (showRecentList) ShelfList.ItemsSource = RecentThumbs.ItemsSource;
        if (showFavoriteList) FavoriteList.ItemsSource = FavoriteThumbs.ItemsSource;
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

    private async void RecentOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedDrag != null) return;
        if (sender is Button { DataContext: RecentCard card })
        {
            if (File.Exists(card.Path))
            {
                OpenOrActivate(card.Path);
            }
            else
            {
                var result = await ShowDialogAsync(new ContentDialog
                {
                    Title = UiText.Current.FileMovedOrRenamed,
                    Content = UiText.Format("Locate {0} to keep its reading position and bookmarks.", card.DisplayName),
                    PrimaryButtonText = UiText.Current.LocatePDF, CloseButtonText = UiText.Current.Cancel
                });
                if (result != ContentDialogResult.Primary) return;
                var picked = await FilePickerHost.PickOpenAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), ".pdf");
                if (picked.Failed) { ShowError(FilePickerHost.FailureMessage); return; }
                if (picked.File is not { } file || _state.FindRecent(card.Path) is not { } entry) return;
                if (_state.FindRecent(file.Path) is { } other && other != entry)
                {
                    ShowError(UiText.Translate("That PDF already has a shelf entry. Choose the original document."));
                    return;
                }
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

        var forget = new MenuFlyoutItem { Text = UiText.Current.RemoveFromList, Icon = new FontIcon { Glyph = "\uECC9" } }; // RemoveFrom
        // Bookmarks hang off the recent entry, so forgetting the file forgets
        // them. Say so rather than discovering it later.
        int bookmarks = _state.FindRecent(card.Path)?.Bookmarks.Count ?? 0;
        if (bookmarks > 0)
        {
            forget.Text = bookmarks == 1
                ? "Remove from list (discards 1 bookmark)"
                : UiText.Format("Remove from list (discards {0} bookmarks)", bookmarks);
        }
        forget.Click += (_, _) =>
        {
            _state.ForgetRecent(card.Path);
            _thumbnails.Forget(card.Path);
            _store.Save(_state);
            PopulateRecents();
        };

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
