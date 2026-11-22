using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using PDFDesk.Engine;
using PDFDesk.PdfiumInterop;
using PDFDesk.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PDFDesk.Controls;

/// <summary>
/// One open document: the <see cref="PdfViewer"/> plus a collapsible sidebar
/// (thumbnails + outline). Owns the <see cref="PdfDocument"/> lifetime and
/// loads it lazily so background tabs cost nothing until first shown
/// (SumatraPDF-style lazy tabs).
/// </summary>
public sealed partial class DocumentView : UserControl
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ObservableCollection<ThumbnailItem> _thumbnails = [];

    private PdfDocument? _document;
    private Task? _loadTask;
    private Task? _saveTask;
    private bool _closed;
    private bool _syncingSelection;

    private void SetReadOnlyView(bool readOnly)
    {
        IsSnapshot = readOnly;
        UpdateThumbnailEditing();
    }

    private void UpdateThumbnailEditing()
    {
        ThumbList.CanReorderItems = ThumbList.CanDragItems = ThumbList.AllowDrop = !IsReadOnlyView;
    }

    internal bool SourceMissing { get; private set; }

    internal static bool IsSourceMissing(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return false;
    }

    internal void UpdateSourceNotice(bool missing, bool saving)
    {
        if (missing != SourceMissing)
        {
            SourceMissing = missing;
            _document?.SetSourceMissing(missing);
            UpdateThumbnailEditing();
        }
        if (missing)
        {
            MissingSourceBar.Title = UiText.Format("Cannot find {0} at its original location", DisplayName);
            MissingSourceBar.Message = IsDirty || Viewer.HasPendingFormEdit || Viewer.IsEditingText
                ? UiText.Translate("Your changes are unsaved. Save a copy to keep them.")
                : UiText.Translate("The file may have been moved or deleted. You can still read this document.");
            ToolTipService.SetToolTip(MissingSourceBar, FilePath);
            SaveMissingCopy.Content = UiText.Current.SaveCopyAction;
            SaveMissingCopy.IsEnabled = !saving;
        }
        MissingSourceBar.IsOpen = missing;
    }

    private async void SaveMissingCopy_Click(object sender, RoutedEventArgs e) =>
        await HostWindow.SaveMissingCopyAsync(this);

    internal void RefreshSnapshotBar()
    {
        var text = UiText.Current;
        SnapshotStatus.Text = NewVersionAvailable ? text.SnapshotUpdated : string.Empty;
        SnapshotBar.Visibility = NewVersionAvailable ? Visibility.Visible : Visibility.Collapsed;
        ReloadSnapshot.Content = text.ReloadVersion;
    }

    private async void ReloadSnapshot_Click(object sender, RoutedEventArgs e) => await HostWindow.ReloadSnapshotAsync(this, false);

    internal MainWindow HostWindow { get; set; } = null!;
    internal bool IsSnapshot { get; private set; }
    internal bool IsReadOnlyView => IsSnapshot || SourceMissing;
    internal string? LatestSavedDigest { get; set; }
    internal bool NewVersionAvailable => LatestSavedDigest is { } latest && _document is { } document && latest != document.SavedDigest;
    internal WindowsFileIdentity.NavigationKey NavigationKey { get; set; }
    public string FilePath { get; }
    public string DisplayName => Path.GetFileName(FilePath);
    public PdfViewer Viewer => ViewerControl;
    internal Grid ReaderLayout => ReadingColumn;
    public bool IsDocumentLoaded => _document is not null;
    public string? LoadError { get; private set; }


    public event EventHandler? Loaded2;

    internal DocumentView(WindowsFileIdentity.NavigationKey key, bool readOnly)
    {
        InitializeComponent();
        NavigationKey = key;
        FilePath = key.Path;
        SetReadOnlyView(readOnly);
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        ThumbList.ItemsSource = _thumbnails;
        ViewerControl.PageMenuOpening += AddBookmarkMenuAction;
        ViewerControl.ScreenshotFinished += error => ShowNotice(null, error == null
            ? UiText.Current.ScreenshotCopied : $"{UiText.Current.ScreenshotFailed} · {error}",
            error == null ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        ViewerControl.OrganizeRequested += page => OpenOrganizer([page]);
        BookmarkList.ItemsSource = _bookmarks;

        ViewerControl.CurrentPageChanged += (_, page) => SyncThumbnailSelection(page);
        ViewerControl.RotationChanged += (_, rotation) => OnViewRotated(rotation);
        ViewerControl.NightModeChanged += (_, _) => RefreshThumbnails();
        ViewerControl.AnnotationEdited += (_, e) => PushEdit(new DocumentEdit
        {
            Label = e.Label,
            IsPageMutation = false,
            PageIndex = e.PageIndex,
            InkPages = e.InkPages,
            SnapshotBytes = e.SnapshotBytes,
            UndoAction = e.UndoAction,
            RedoAction = e.RedoAction,
        });
    }

    private bool _paneRequested;
    private bool _paneHiddenForFocus;

    // Persist the document preference even while focus reading temporarily hides the pane.
    public bool IsPaneOpen
    {
        get => _paneRequested;
        set
        {
            _paneRequested = value;
            Split.IsPaneOpen = value && !_paneHiddenForFocus;
        }
    }

    internal void SetPaneHiddenForFocus(bool hidden)
    {
        _paneHiddenForFocus = hidden;
        Split.IsPaneOpen = _paneRequested && !hidden;
    }

    /// <summary>
    /// Shows a notice floating over this document's pages. Returns false when
    /// the user has already dismissed a notice with the same key.
    /// </summary>
    public bool ShowNotice(string? title, string message, InfoBarSeverity severity, string? dismissKey = null, params NoticeAction[] actions)
        => Notice.Show(title, message, severity, dismissKey, actions);

    public void ClearNotice() => Notice.Clear();

    /// <summary>
    /// Runs PDFium work on the render thread — the one thread every PDFium call
    /// must funnel through (see <see cref="PdfViewer.WorkQueue"/>).
    ///
    /// The fallback covers one narrow race: closing a tab disposes the viewer's
    /// scheduler on Unloaded, cancelling anything still queued. If that happens
    /// the render thread has already been joined, so nothing else can be inside
    /// PDFium and finishing the work off-thread is safe. Without the fallback a
    /// tab closed at the wrong moment would leak the native document.
    /// </summary>
    internal async Task<T> RunPdfAsync<T>(PdfWorkPriority priority, Func<T> work)
    {
        try
        {
            return await Viewer.RunOnRenderThreadAsync(priority, work);
        }
        catch (OperationCanceledException)
        {
            return await Task.Run(work);
        }
    }

    private Task RunPdfAsync(PdfWorkPriority priority, Action work)
        => RunPdfAsync(priority, () =>
        {
            work();
            return true;
        });

    /// <summary>Opens the document (once) and populates the view. Safe to await repeatedly.</summary>
    public Task EnsureLoadedAsync(RecentFile? restore) => _loadTask ??= LoadAsync(restore);

    private async Task LoadAsync(RecentFile? restore)
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        try
        {
            // Let the selected tab and its loading surface lay out before opening the PDF.
            var ready = new TaskCompletionSource();
            if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => ready.SetResult())) return;
            await ready.Task;
            if (_closed) return;

            // Opening sees all earlier saves. Keep notifications arriving during the open.
            LatestSavedDigest = null;
            var reservedKey = NavigationKey;
            var (document, openedKey) = await RunPdfAsync(PdfWorkPriority.Interactive, () =>
            {
                var opened = IsReadOnlyView ? PdfDocument.OpenReadOnly(FilePath) : PdfDocument.Open(FilePath);
                return (opened, opened.GetOpenedNavigationKey(reservedKey));
            });
            if (_closed)
            {
                await RunPdfAsync(PdfWorkPriority.Interactive, document.Dispose);
                return;
            }
            // A save notification during the open may already have advanced the path identity.
            if (ReferenceEquals(reservedKey, NavigationKey)) NavigationKey = openedKey;
            if (!IsReadOnlyView && MainWindow.HasOtherEditor(this))
            {
                document.MakeReadOnly();
                SetReadOnlyView(true);
            }
            _document = document;
            Viewer.SetPageColumns(restore?.LayoutColumns ?? 1);
            Viewer.SetDocument(document);
            if (restore is not null)
                Viewer.RestoreView(restore.Zoom, restore.Rotation, restore.ScrollFraction);
            IsPaneOpen = restore?.SidebarOpen ?? true;

            PopulateThumbnails(document);
            SyncThumbnailSelection(Viewer.CurrentPage);
            _ = PopulateOutlineAsync(document);
            _ = ReadSignatureCountAsync(document);
        }
        catch (Exception ex) when (ex is PdfiumException or IOException or UnauthorizedAccessException)
        {
            LoadError = ex.Message;
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        Loaded2?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Digital signatures in this document. Read once at load on the render
    /// thread and cached, because the toolbar consults it on every tab switch
    /// and PDFium must never be touched from the UI thread.
    /// </summary>
    public int SignatureCount { get; private set; }

    public bool HasSignatures => SignatureCount > 0;

    private async Task ReadSignatureCountAsync(PdfDocument document)
    {
        try
        {
            int count = await RunPdfAsync(PdfWorkPriority.Background, () => document.SignatureCount);
            if (_document == document)
            {
                SignatureCount = count;
                SignaturesRead?.Invoke(this, EventArgs.Empty);
            }
        }
        catch
        {
            // Signature reporting is informational; a failure just leaves it off.
        }
    }

    /// <summary>Raised once the signature count is known, so the shell can refresh its notice.</summary>
    public event EventHandler? SignaturesRead;

    public void Close()
    {
        _closed = true;
        ClearUndoHistory();
        ThumbList.ContainerContentChanging -= ThumbList_ContainerContentChanging;
        ThumbList.SelectionChanged -= ThumbList_SelectionChanged;
        ThumbList.DragItemsCompleted -= ThumbList_DragItemsCompleted;
        ThumbList.DragOver -= ThumbList_DragOver;
        ThumbList.Drop -= ThumbList_Drop;
        ThumbList.RightTapped -= ThumbList_RightTapped;
        ThumbList.KeyDown -= ThumbList_KeyDown;
        OutlineTree.ItemInvoked -= OutlineTree_ItemInvoked;
        BookmarkList.ItemClick -= BookmarkList_ItemClick;
        BookmarkList.RightTapped -= BookmarkList_RightTapped;
        BookmarkList.KeyDown -= BookmarkList_KeyDown;
        ThumbsTab.Checked -= SidebarTab_Checked;
        OutlineTab.Checked -= SidebarTab_Checked;
        BookmarksTab.Checked -= SidebarTab_Checked;
        Notice.Close();
        Viewer.CloseCanvas();
        ThumbList.ItemsSource = null;
        _thumbnails.Clear();
        BookmarkList.ItemsSource = null;
        ThumbList.ItemTemplate = null;
        OutlineTree.ItemTemplate = null;
        BookmarkList.ItemTemplate = null;
        Split.Pane = null;
        Split.Content = null;
        Content = null;
        var document = _document;
        _document = null;
        if (document is not null)
        {
            // Dispose takes the global PDFium lock; never block the UI thread
            // on it (a tile render can hold it for tens of milliseconds).
            _ = RunPdfAsync(PdfWorkPriority.Interactive, document.Dispose);
        }
    }

    public bool IsDirty => _document?.IsDirty == true;

    /// <summary>
    /// Saves the open document and shares the same operation with other save callers.
    /// </summary>
    public Task SaveInPlaceAsync()
    {
        if (_saveTask is { IsCompleted: false })
        {
            return _saveTask;
        }

        return _document is { } document
            ? _saveTask = SaveInPlaceCoreAsync(document)
            : Task.CompletedTask;
    }

    internal async Task CommitPendingEditsAsync()
    {
        await Viewer.FinishEraserAsync();
        await Viewer.CommitTextAsync();
        if (Viewer.IsEditingText)
        {
            throw new InvalidOperationException(UiText.Current.TextCommitFailed);
        }
        await Viewer.CommitFormFocusAsync();
    }

    private async Task SaveInPlaceCoreAsync(PdfDocument document)
    {
        await CommitPendingEditsAsync();
        if (!document.IsDirty) return;
        await RunPdfAsync(PdfWorkPriority.Interactive, () =>
        {
            document.SaveInPlace();
            return true;
        });
        if (_document == document)
        {
            if (!document.IsDirty)
            {
                ClearUndoHistory();
                if (_bookmarkMappingPending)
                {
                    SetBookmarks(FinishBookmarkMapping(), saved: true);
                }
                else NotifyBookmarksChanged(saved: true);
            }
            RefreshThumbnails();
        }
    }

    // ---------------------------------------------------------------- thumbnails

    /// <summary>
    /// Rebuilds the strip, giving each item its page's dimensions so every box
    /// is correctly shaped before any bitmap arrives. <c>GetPageSize</c> is a
    /// pure array read (no PDFium call), so this is safe on the UI thread — but
    /// it must run *after* the viewer has re-read page metrics following a page
    /// mutation, or it will index past the end.
    /// </summary>
    private void PopulateThumbnails(PdfDocument document)
    {
        int rotation = Viewer.ViewRotation;
        _thumbnails.Clear();
        for (int i = 0; i < document.PageCount; i++)
        {
            var (ptWidth, ptHeight) = document.GetPageSize(i);
            _thumbnails.Add(new ThumbnailItem(i, ptWidth, ptHeight, rotation));
        }
    }

    /// <summary>Re-shapes and re-renders thumbnails after the view is rotated.</summary>
    private void OnViewRotated(int rotation)
    {
        foreach (var item in _thumbnails)
        {
            item.SetRotation(rotation); // resizes the box and clears the stale render
        }
        // Only re-render what's on screen; the rest come back through
        // virtualization as the user scrolls to them.
        foreach (var item in _thumbnails)
        {
            if (ThumbList.ContainerFromItem(item) is not null)
            {
                _ = RenderThumbnailAsync(item);
            }
        }
    }

    // Lazily render a thumbnail as its container is realized (list virtualization).
    internal void ThumbList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ThumbnailItem item || item.IsRendered || _document is null)
        {
            return;
        }
        _ = RenderThumbnailAsync(item);
    }

    /// <summary>
    /// Renders one thumbnail on the render thread at Thumbnail priority —
    /// visible tiles always win, so scrolling the sidebar can't make the
    /// document stutter.
    /// </summary>
    private int _thumbnailGeneration;

    internal async Task RenderThumbnailAsync(ThumbnailItem item)
    {
        if (_document is not { } document)
        {
            return;
        }

        int generation = _thumbnailGeneration;
        int pageIndex = item.PageIndex;
        int rotation = Viewer.ViewRotation;
        try
        {
            var bmp = await Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Thumbnail, () =>
            {
                // Render at 1.5x the 168-DIP display width so thumbnails stay
                // crisp on the typical 125-150% display scale. Scale off the
                // rotated width so a rotated page still fills the box.
                var (ptWidth, ptHeight) = document.GetPageSize(pageIndex);
                float acrossPt = ViewRotationMath.SwapsAxes(rotation) ? ptHeight : ptWidth;
                float scale = 252f / Math.Max(1f, acrossPt);
                return document.RenderPage(pageIndex, scale, rotation);
            });
            // Drop the result if the document or rotation moved on while we waited.
            if (_document == document && Viewer.ViewRotation == rotation && generation == _thumbnailGeneration)
            {
                item.Image = ToBitmap(bmp, Viewer.NightMode);
            }
            bmp.Return();
        }
        catch
        {
            // Skip unrenderable thumbnails (also covers doc-swap cancellation).
        }
    }

    /// <summary>
    /// Copies a rendered page into a bitmap, optionally inverting it for night
    /// mode. The viewer inverts on the GPU with a Win2D effect, but an Image in
    /// a virtualized ListView has no equivalent — and leaving thumbnails bright
    /// beside inverted pages was a long-standing visual bug. A 168x220 DIP
    /// thumbnail is ~55k pixels, so inverting during the copy is free.
    /// </summary>
    private static WriteableBitmap ToBitmap(PageBitmap page, bool invert)
    {
        var bitmap = new WriteableBitmap(page.Width, page.Height);
        int byteCount = page.Stride * page.Height;

        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            if (!invert)
            {
                // Copy exactly one image's worth of bytes (the pooled buffer may be larger).
                stream.Write(page.Pixels, 0, byteCount);
            }
            else
            {
                // BGRA: invert colour, leave alpha alone.
                var inverted = new byte[byteCount];
                for (int i = 0; i < byteCount; i += 4)
                {
                    inverted[i] = (byte)(255 - page.Pixels[i]);
                    inverted[i + 1] = (byte)(255 - page.Pixels[i + 1]);
                    inverted[i + 2] = (byte)(255 - page.Pixels[i + 2]);
                    inverted[i + 3] = page.Pixels[i + 3];
                }
                stream.Write(inverted, 0, byteCount);
            }
        }
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// Re-renders realized thumbnails without rebuilding the thumbnail list.
    /// </summary>
    private void RefreshThumbnails()
    {
        foreach (var item in _thumbnails)
        {
            if (ThumbList.ContainerFromItem(item) is not null)
            {
                _ = RenderThumbnailAsync(item);
            }
            else
            {
                item.Image = null;
            }
        }
    }

    private void ThumbList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var removed in e.RemovedItems.OfType<ThumbnailItem>()) removed.IsSelected = false;
        foreach (var added in e.AddedItems.OfType<ThumbnailItem>()) added.IsSelected = true;

        // Navigate only on a plain single selection — Ctrl/Shift multi-select
        // (for page editing) must not yank the view around.
        if (_syncingSelection || ThumbList.SelectedItems.Count != 1 ||
            ThumbList.SelectedItem is not ThumbnailItem item)
        {
            return;
        }
        Viewer.GoToPage(item.PageIndex, recordHistory: true);
    }

    private void SyncThumbnailSelection(int pageIndex)
    {
        // The bold page number marks the page being read regardless of what is selected for
        // page editing, so it updates even when the early-return below fires.
        for (int i = 0; i < _thumbnails.Count; i++)
        {
            _thumbnails[i].IsCurrent = i == pageIndex;
        }

        // Never collapse an in-progress multi-selection just because the
        // viewer scrolled to another page.
        if (pageIndex < 0 || pageIndex >= _thumbnails.Count || ThumbList.SelectedItems.Count > 1)
        {
            return;
        }
        _syncingSelection = true;
        ThumbList.SelectedIndex = pageIndex;
        if (Split.IsPaneOpen && ThumbList.Visibility == Visibility.Visible)
        {
            ThumbList.ScrollIntoView(_thumbnails[pageIndex]);
        }
        _syncingSelection = false;
    }

    // ---------------------------------------------------------------- outline

    private bool _hasOutline;

    private async Task PopulateOutlineAsync(PdfDocument document)
    {
        IReadOnlyList<OutlineItem> outline;
        try
        {
            outline = await RunPdfAsync(PdfWorkPriority.Background, document.GetOutline);
        }
        catch
        {
            outline = [];
        }

        if (_document != document)
        {
            return;
        }

        // Bind data objects directly: the DataTemplate's x:DataType is
        // OutlineNode, and its TreeViewItem.ItemsSource="{x:Bind Children}"
        // supplies the hierarchy. (Populating RootNodes with TreeViewNode
        // wrappers instead crashes template realization with a type mismatch.)
        var nodes = outline.Select(item => new OutlineNode(item)).ToList();
        _hasOutline = nodes.Count > 0;
        OutlineTree.ItemsSource = nodes;
    }

    private void OutlineTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is OutlineNode node && node.PageIndex >= 0)
        {
            Viewer.GoToPage(node.PageIndex, recordHistory: true);
        }
    }

    // ---------------------------------------------------------------- page editing

    private bool _pageOpRunning;
    internal bool HasPendingOperation => HasOpenOrganizer || Viewer.IsEditingText ||
        Viewer.HasPendingFormEdit || _pageOpRunning || _saveTask is { IsCompleted: false };

    /// <summary>Raised after any page delete/move/insert (dirty marker + toolbar refresh).</summary>
    public event EventHandler? PagesEdited;
    internal event Action? SearchInvalidated;
    internal event Action? SearchRefreshRequested;

    /// <summary>Raised when a page operation fails with a user-relevant message.</summary>
    public event EventHandler<string>? PageOpFailed;

    /// <summary>
    /// Raised when the user asks to extract the selection. The shell handles it
    /// because the save picker needs the window handle.
    /// </summary>
    public event EventHandler? ExtractRequested;

    private List<int> SelectedPageIndices() =>
        [.. ThumbList.SelectedItems.OfType<ThumbnailItem>().Select(t => t.PageIndex).OrderBy(i => i)];

    /// <summary>How many pages the thumbnail sidebar has selected, for the shell's menu state.</summary>
    public int SelectedPageCount => ThumbList.SelectedItems.Count;

    /// <summary>
    /// The selected pages as a filename fragment: "pages 2-5" for a run,
    /// "pages 1, 4, 9" for a scattered pick, capped so a 200-page selection
    /// cannot produce a filename the filesystem rejects.
    /// </summary>
    public string SelectedPageRangeLabel()
    {
        var pages = SelectedPageIndices();
        if (pages.Count == 0)
        {
            return "pages";
        }
        if (pages.Count == 1)
        {
            return $"page {pages[0] + 1}";
        }
        // Contiguous runs read far better as a range than as a list.
        bool contiguous = pages[^1] - pages[0] == pages.Count - 1;
        if (contiguous)
        {
            return $"pages {pages[0] + 1}-{pages[^1] + 1}";
        }
        var shown = pages.Take(6).Select(p => (p + 1).ToString());
        return "pages " + string.Join(", ", shown) + (pages.Count > 6 ? $" +{pages.Count - 6}" : "");
    }

    /// <summary>
    /// Writes the selected pages out as a new PDF, leaving this document
    /// untouched. Reuses <c>ExportPages</c>, the same engine call that backs the
    /// page clipboard, so extract is a save destination rather than new
    /// machinery. Returns true when the file was written.
    /// </summary>
    public async Task<bool> ExtractSelectedPagesAsync(string path)
    {
        var pages = SelectedPageIndices();
        if (pages.Count == 0 || _document is not { } document || _pageOpRunning)
        {
            return false;
        }

        try
        {
            await Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, () =>
            {
                document.ExportPagesToFile(pages, path);
                return true;
            });
            return true;
        }
        catch (Exception ex) when (ex is PDFDesk.PdfiumInterop.PdfiumException or IOException or UnauthorizedAccessException)
        {
            PageOpFailed?.Invoke(this, UiText.Format("Could not extract pages: {0}", ex.Message));
            return false;
        }
    }

    /// <summary>
    /// Runs one page mutation on the render thread, then rebuilds every
    /// page-derived cache and remaps bookmarks. One op at a time — the
    /// sidebar is a live view of the document and re-entrancy would race it.
    /// Returns true when the op ran without error.
    /// </summary>
    private async Task<bool> RunPageOpAsync(Action<PdfDocument> op, Func<int, int?>? remapBookmark)
    {
        if (IsReadOnlyView || _pageOpRunning || _document is not { } document)
        {
            return false;
        }
        _pageOpRunning = true;
        try
        {
            await Viewer.FinishEraserAsync();
            _thumbnailGeneration++;
            SearchInvalidated?.Invoke();
            Viewer.PreparePageMutation();
            await Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, () =>
            {
                op(document);
                return true;
            });
            AfterPageMutation(remapBookmark);
            return true;
        }
        catch (Exception ex) when (ex is PDFDesk.PdfiumInterop.PdfiumException or InvalidOperationException)
        {
            AfterPageMutation(null); // resync the UI; metrics may have moved
            PageOpFailed?.Invoke(this, ex.Message);
            return false;
        }
        finally
        {
            _pageOpRunning = false;
            SearchRefreshRequested?.Invoke();
        }
    }

    /// <summary>
    /// Bakes every annotation and form field into page content.
    ///
    /// Routed through the page-mutation path because flatten rewrites page
    /// content and annotation lists wholesale — every tile, thumbnail and text
    /// map derived from the old structure is stale afterwards. Undo history is
    /// dropped for the same reason: the annotation objects an undo entry refers
    /// to no longer exist.
    /// </summary>
    public async Task<(int Changed, List<int> FailedPages)?> FlattenDocumentAsync()
    {
        if (_document is not { } document)
        {
            return null;
        }

        int changed = 0;
        List<int> failedPages = [];
        if (!await RunPageOpAsync(doc => changed = doc.FlattenAllPages(onPageFailed: failedPages.Add), remapBookmark: null))
            return null;
        if (changed > 0)
        {
            ClearUndoHistory();
            PagesEdited?.Invoke(this, EventArgs.Empty);
        }
        return (changed, failedPages);
    }

    private void AfterPageMutation(Func<int, int?>? remapBookmark)
    {
        if (_document is not { } document)
        {
            return;
        }

        if (remapBookmark is not null && !_bookmarkMappingPending) _bookmarkOriginalPageCount = _thumbnails.Count;
        RefreshEditedPages(document, remapBookmark);
        _ = PopulateOutlineAsync(document);

        if (remapBookmark is not null)
        {
            _bookmarkBaseline ??= GetBookmarks();
            _bookmarkMaps.Add(remapBookmark);
        }
        if (remapBookmark is not null && _bookmarks.Count > 0)
        {
            var remapped = _bookmarks
                .Select(b => (Item: b, NewPage: remapBookmark(b.PageIndex)))
                .ToList();
            _bookmarks.Clear();
            foreach (var (item, newPage) in remapped.Where(t => t.NewPage is not null).OrderBy(t => t.NewPage))
            {
                item.PageIndex = newPage!.Value;
                _bookmarks.Add(item);
            }
            NotifyBookmarksChanged();
        }

        RefreshPaneVisibility();
        SyncThumbnailSelection(Viewer.CurrentPage);
        PagesEdited?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshEditedPages(PdfDocument document, Func<int, int?>? remap)
    {
        _thumbnailGeneration++;
        Viewer.HandleDocumentMutated(remap);
        if (remap is null)
        {
            PopulateThumbnails(document);
            return;
        }

        // Capture the images before changing any slot. Keeping each existing
        // item bound avoids GridView recycling or animating a row of cards.
        var images = new BitmapSource?[document.PageCount];
        foreach (var item in _thumbnails)
            if (remap(item.PageIndex) is int index) images[index] = item.Image;

        int rotation = Viewer.ViewRotation;
        _syncingSelection = true;
        try
        {
            while (_thumbnails.Count > document.PageCount)
                _thumbnails.RemoveAt(_thumbnails.Count - 1);
            for (int i = 0; i < document.PageCount; i++)
            {
                var size = document.GetPageSize(i);
                if (i == _thumbnails.Count)
                {
                    _thumbnails.Add(new ThumbnailItem(i, size.Width, size.Height, rotation) { Image = images[i] });
                    continue;
                }
                var slot = _thumbnails[i];
                slot.UpdateSlot(i, size.Width, size.Height, images[i]);
                if (!slot.IsRendered && ThumbList.ContainerFromIndex(i) is not null)
                    _ = RenderThumbnailAsync(slot);
            }
        }
        finally { _syncingSelection = false; }
    }

    public async Task DeleteSelectedPagesAsync()
    {
        var pages = SelectedPageIndices();
        if (pages.Count == 0 || _document is not { } document)
        {
            return;
        }
        if (pages.Count >= document.PageCount)
        {
            PageOpFailed?.Invoke(this, UiText.Translate("Cannot delete every page of a document."));
            return;
        }
        await DeletePagesWithUndoAsync(pages, cut: false);
    }

    /// <summary>Shared delete/cut: snapshots each victim page so undo re-inserts it at its original index.</summary>
    internal async Task DeletePagesWithUndoAsync(List<int> pages, bool cut)
    {
        var deleted = pages.ToHashSet();
        var before = GetBookmarks();
        byte[]? snapshot = null;
        FormFieldInfo[] remainingFields = [];
        byte[]? clipboardBytes = null;

        bool ok = await RunPageOpAsync(d =>
        {
            snapshot = d.CapturePageTree();
            if (cut)
            {
                clipboardBytes = d.ExportPages(pages); // whole selection in one PDF, in order
            }
            d.DeletePages(pages);
            remainingFields = ReadFields(d);
        }, p => BookmarkRemap.AfterDelete(p, deleted));

        if (!ok || snapshot is null)
        {
            return;
        }
        if (cut && clipboardBytes is not null)
        {
            PageClipboard.Set(clipboardBytes, pages.Count);
        }

        int? Forward(int page) => BookmarkRemap.AfterDelete(page, deleted);
        var origIndices = pages.ToList();
        static FormFieldInfo[] ReadFields(PdfDocument doc) => doc.HasFillableForm
            ? Enumerable.Range(0, doc.PageCount).SelectMany(doc.GetFormFields).ToArray() : [];
        PushEdit(new DocumentEdit
        {
            Label = pages.Count == 1 ? (cut ? "cut page" : "delete page") : $"{(cut ? "cut" : "delete")} {pages.Count} pages",
            IsPageMutation = true,
            ForwardMap = Forward,
            ReverseMap = InvertPageMap(_thumbnails.Count + pages.Count, _thumbnails.Count, Forward),
            SnapshotBytes = snapshot.LongLength,
            BookmarksBefore = before,
            BookmarksAfter = GetBookmarks(),
            UndoAction = d =>
            {
                if (!remainingFields.SequenceEqual(ReadFields(d)))
                    throw new InvalidOperationException(UiText.Current.LanguageTag == "zh-CN"
                        ? "删页后表单值已更改。请先恢复表单值，再撤销删页。"
                        : "Form values changed after deletion. Restore them before undoing the deletion.");
                d.RestorePageTree(snapshot);
            },
            RedoAction = d => d.DeletePages(origIndices),
        });
    }

    /// <summary>Copy (or cut) the selected pages into the app-wide page clipboard.</summary>
    public async Task CopySelectedPagesAsync(bool cut)
    {
        var pages = SelectedPageIndices();
        if (pages.Count == 0 || _document is not { } document || _pageOpRunning)
        {
            return;
        }
        if (cut && pages.Count >= document.PageCount)
        {
            PageOpFailed?.Invoke(this, UiText.Translate("Cannot cut every page of a document."));
            return;
        }

        if (!cut)
        {
            var bytes = await Viewer.RunOnRenderThreadAsync(
                PdfWorkPriority.Interactive, () => document.ExportPages(pages));
            PageClipboard.Set(bytes, pages.Count);
            return;
        }

        await DeletePagesWithUndoAsync(pages, cut: true);
    }

    /// <summary>Insert the page clipboard at <paramref name="atIndex"/> (default: after the selection / current page).</summary>
    public async Task PastePagesAsync(int? atIndex = null)
    {
        if (!PageClipboard.HasPages || _document is null)
        {
            return;
        }
        var bytes = PageClipboard.Pdf!;
        int at = atIndex ?? (SelectedPageIndices() is { Count: > 0 } sel ? sel[^1] + 1 : Viewer.CurrentPage + 1);
        await InsertBytesWithUndoAsync(bytes, at, "paste");
    }

    /// <summary>Insert all pages of another PDF file. Returns the number of pages inserted.</summary>
    public async Task<int> InsertPdfFileAsync(string path, int atIndex)
    {
        byte[] bytes;
        try
        {
            bytes = await Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, () =>
            {
                using var src = PdfDocument.Open(path);
                return src.ExportPages([.. Enumerable.Range(0, src.PageCount)]);
            });
        }
        catch (Exception ex) when (ex is PDFDesk.PdfiumInterop.PdfiumException or IOException)
        {
            PageOpFailed?.Invoke(this, ex.Message);
            return 0;
        }
        return await InsertBytesWithUndoAsync(bytes, atIndex, "insert PDF");
    }

    /// <summary>Insert a serialized PDF at an index with a matching undo (delete the inserted block).</summary>
    private Task<int> InsertBytesWithUndoAsync(byte[] bytes, int at, string verb) =>
        InsertWithUndoAsync(_ => bytes, at, verb);

    internal Task<int> DuplicatePagesAsync(List<int> pages) =>
        InsertWithUndoAsync(d => d.ExportPages(pages), pages[^1] + 1, "duplicate");

    private PageOrganizer? _organizer;
    private Task? _organizerSession;
    internal bool HasOpenOrganizer => _organizer is not null;

    internal async Task<bool> CloseOrganizerAsync()
    {
        if (_organizer is not { } dialog || _organizerSession is not { } session) return true;
        if (!await dialog.RequestCloseAsync()) return false;
        await session;
        return true;
    }

    internal void OpenOrganizer(List<int>? selected = null)
    {
        if (IsReadOnlyView || _document is null || _pageOpRunning || DialogHost.IsOpen(this)) return;
        var dialog = new PageOrganizer(this, _thumbnails, selected ?? [Viewer.CurrentPage]);
        _organizer = dialog;
        _organizerSession = ShowOrganizerAsync(dialog);
    }

    private async Task ShowOrganizerAsync(PageOrganizer dialog)
    {
        try { await DialogHost.ShowAsync(this, dialog); }
        finally { _organizer = null; }
    }

    internal async Task SavePageDraftAsync(PageDraft draft, string? copyPath = null)
    {
        if (IsReadOnlyView || _pageOpRunning || _document is not { } document)
            throw new InvalidOperationException("The document is unavailable.");
        _pageOpRunning = true;
        try
        {
            if (_saveTask is { IsCompleted: false }) await _saveTask;
            await CommitPendingEditsAsync();
            if (copyPath != null)
            {
                string digest = "";
                await MainWindow.WriteReaderCopyAsync(FilePath, copyPath, async staging =>
                {
                    digest = await RunPdfAsync(PdfWorkPriority.Interactive, () => draft.SaveAs(document, staging));
                    return true;
                }, () => DraftCopySaved?.Invoke(copyPath, digest, draft.MapBookmarks(BookmarksForExport())));
                return;
            }
            var positions = draft.OriginalPositions();
            var replacement = await RunPdfAsync(PdfWorkPriority.Interactive, () => draft.Save(document));
            if (_closed)
            {
                await RunPdfAsync(PdfWorkPriority.Interactive, replacement.Dispose);
                return;
            }
            double zoom = Viewer.Zoom, scroll = Viewer.ScrollFraction;
            int rotation = Viewer.ViewRotation;
            var fitMode = Viewer.FitMode;
            int current = Math.Clamp(Viewer.CurrentPage, 0, positions.Length - 1);
            int target = positions[current] ?? Math.Min(current, replacement.PageCount - 1);
            var location = positions[current].HasValue ? Viewer.CaptureReadingLocation(current) : null;
            SearchInvalidated?.Invoke();
            _document = replacement;
            Viewer.SetDocument(replacement);
            Viewer.RestoreView(zoom, rotation, scroll, fitMode, target, location);
            PopulateThumbnails(replacement);
            SyncThumbnailSelection(Viewer.CurrentPage);
            var bookmarks = draft.MapBookmarks(FinishBookmarkMapping());
            SetBookmarks(bookmarks, saved: true);
            ClearUndoHistory();
            _ = PopulateOutlineAsync(replacement);
            _ = ReadSignatureCountAsync(replacement);
            PagesEdited?.Invoke(this, EventArgs.Empty);
            SearchRefreshRequested?.Invoke();
            await RunPdfAsync(PdfWorkPriority.Interactive, document.Dispose);
        }
        finally { _pageOpRunning = false; }
    }

    private async Task<int> InsertWithUndoAsync(Func<PdfDocument, byte[]> source, int at, string verb)
    {
        var before = GetBookmarks();
        int inserted = 0;
        bool ok = await RunPageOpAsync(d =>
        {
            at = Math.Clamp(at, 0, d.PageCount);
            inserted = d.InsertPages(source(d), at);
        },
            p => BookmarkRemap.AfterInsert(p, at, inserted));
        if (!ok || inserted == 0)
        {
            return inserted;
        }

        int insertedCount = inserted;
        var insertion = new InsertedPages(at, insertedCount);
        PushEdit(new DocumentEdit
        {
            Label = insertedCount == 1 ? $"{verb} page" : $"{verb} {insertedCount} pages",
            IsPageMutation = true,
            ForwardMap = p => BookmarkRemap.AfterInsert(p, at, insertedCount),
            ReverseMap = p => p < at ? p : p < at + insertedCount ? null : p - insertedCount,
            Insertion = insertion,
            BookmarksBefore = before,
            BookmarksAfter = GetBookmarks(),
            UndoAction = insertion.Undo,
            RedoAction = insertion.Redo,
        });
        return inserted;
    }

    // ---------------------------------------------------------------- undo / redo

    /// <summary>One undoable edit: an annotation change, or a page mutation with bookmark snapshots.</summary>
    private sealed class DocumentEdit : IUndoableEdit
    {
        public required string Label { get; init; }
        public InsertedPages? Insertion { get; init; }
        public long SnapshotBytes { get => Insertion?.SnapshotBytes ?? field; init; }
        public required Action<PdfDocument> UndoAction { get; init; }
        public required Action<PdfDocument> RedoAction { get; init; }
        public Func<int, int?>? ForwardMap { get; init; }
        public Func<int, int?>? ReverseMap { get; init; }
        public bool IsPageMutation { get; init; }
        public int PageIndex { get; init; } // annotation edits: the page to refresh
        public IReadOnlyList<int>? InkPages { get; init; }
        public IReadOnlyList<PDFDesk.Services.BookmarkEntry>? BookmarksBefore { get; set; }
        public IReadOnlyList<PDFDesk.Services.BookmarkEntry>? BookmarksAfter { get; set; }
    }

    private static Func<int, int?> InvertPageMap(int oldCount, int newCount, Func<int, int?> forward)
    {
        var inverse = new int?[newCount];
        for (int i = 0; i < oldCount; i++)
            if (forward(i) is int index) inverse[index] = i;
        return i => inverse[i];
    }

    private readonly UndoStack<DocumentEdit> _undoStack = new();

    /// <summary>Raised whenever undo/redo availability or labels change.</summary>
    public event EventHandler? UndoStateChanged;

    public bool CanUndo => !IsReadOnlyView && _undoStack.CanUndo;
    public bool CanRedo => !IsReadOnlyView && _undoStack.CanRedo;
    public string? UndoLabel => _undoStack.UndoLabel;
    public string? RedoLabel => _undoStack.RedoLabel;

    private void PushEdit(DocumentEdit edit)
    {
        _undoStack.Push(edit);
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task UndoAsync()
    {
        await Viewer.FinishEraserAsync();
        if (IsReadOnlyView || _pageOpRunning)
        {
            return;
        }
        var edit = _undoStack.NextUndo;
        if (edit is not null)
        {
            await ApplyEditAsync(edit, edit.UndoAction, edit.BookmarksBefore, edit.ReverseMap, undo: true);
        }
    }

    public async Task RedoAsync()
    {
        await Viewer.FinishEraserAsync();
        if (IsReadOnlyView || _pageOpRunning)
        {
            return;
        }
        var edit = _undoStack.NextRedo;
        if (edit is not null)
        {
            await ApplyEditAsync(edit, edit.RedoAction, edit.BookmarksAfter, edit.ForwardMap, undo: false);
        }
    }

    private async Task ApplyEditAsync(DocumentEdit edit, Action<PdfDocument> action,
        IReadOnlyList<PDFDesk.Services.BookmarkEntry>? bookmarks, Func<int, int?>? remap, bool undo)
    {
        if (_document is not { } document)
        {
            return;
        }
        _pageOpRunning = true;
        try
        {
            if (edit.IsPageMutation)
            {
                _thumbnailGeneration++;
                SearchInvalidated?.Invoke();
                Viewer.PreparePageMutation();
            }
            await Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, () =>
            {
                action(document);
                return true;
            });
            _undoStack.Complete(edit, undo);

            if (edit.IsPageMutation)
            {
                if (undo && _bookmarkMaps.Count != 0) _bookmarkMaps.RemoveAt(_bookmarkMaps.Count - 1);
                else if (!undo && remap is not null) _bookmarkMaps.Add(remap);
                RefreshEditedPages(document, remap);
                _ = PopulateOutlineAsync(document);
                var inverse = undo ? edit.ForwardMap : edit.ReverseMap;
                if (bookmarks is not null && remap is not null && inverse is not null)
                {
                    var current = GetBookmarks();
                    if (undo) edit.BookmarksAfter = current;
                    else edit.BookmarksBefore = current;
                    SetBookmarks(BookmarkRemap.ReplayPageEdit(current, bookmarks, remap, inverse));
                }
                RefreshPaneVisibility();
                SyncThumbnailSelection(Viewer.CurrentPage);
            }
            else
            {
                // Undo can move or remove the very annotation that is selected,
                // and the selection frame is drawn from a cached rect — leaving
                // it would strand an empty box where the object used to be.
                Viewer.ClearObjectSelection();
                if (edit.InkPages is { } pages)
                {
                    foreach (int page in pages) Viewer.RefreshPageTiles(page);
                }
                else
                {
                    Viewer.InvalidatePage(edit.PageIndex);
                }
            }
            PagesEdited?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is PDFDesk.PdfiumInterop.PdfiumException or InvalidOperationException
            or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            PageOpFailed?.Invoke(this, UiText.Translate(ex.Message));
        }
        finally
        {
            _pageOpRunning = false;
            if (edit.IsPageMutation) SearchRefreshRequested?.Invoke();
            UndoStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetBookmarks(IReadOnlyList<PDFDesk.Services.BookmarkEntry> entries, bool saved = false)
    {
        _bookmarks.Clear();
        foreach (var entry in entries.OrderBy(b => b.PageIndex))
        {
            _bookmarks.Add(new BookmarkItem(entry.PageIndex, entry.Name));
        }
        NotifyBookmarksChanged(saved);
    }

    /// <summary>Drops the undo history — page identity is invalidated after a save-in-place reopen or close.</summary>
    public void ClearUndoHistory()
    {
        _undoStack.Clear();
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // Ctrl+C/X/V routing helpers for the shell: page ops only apply when the
    // thumbnail list has keyboard focus (otherwise Ctrl+C means "copy text").
    public bool TryCopyPages(bool cut)
    {
        if ((cut && IsReadOnlyView) || !IsThumbListFocused() || ThumbList.SelectedItems.Count == 0)
        {
            return false;
        }
        _ = CopySelectedPagesAsync(cut);
        return true;
    }

    public bool TryPastePages()
    {
        if (IsReadOnlyView || _document is null || !PageClipboard.HasPages)
        {
            return false;
        }
        _ = PastePagesAsync();
        return true;
    }

    private bool IsThumbListFocused()
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, ThumbList))
            {
                return true;
            }
            focused = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    // ---- sidebar input: drag-reorder, context menu, keys, external drops ----

    private async void ThumbList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move ||
            _document is not { } document || args.Items.Count == 0)
        {
            return;
        }

        var moved = args.Items.OfType<ThumbnailItem>().ToList();
        if (moved.Count == 0)
        {
            return;
        }
        // Original page indices of the dragged items, and the block's landing
        // position in the reordered collection (= final ordering).
        var movedIndices = moved.Select(m => m.PageIndex).OrderBy(i => i).ToList();
        int destIndex = moved.Min(m => _thumbnails.IndexOf(m));

        await MovePagesWithUndoAsync(movedIndices, destIndex);
    }

    internal async Task<bool> MovePagesWithUndoAsync(List<int> movedIndices, int destIndex)
    {
        if (_document is not { } document || movedIndices.Count == 0) return false;
        var map = BookmarkRemap.MovePermutation(document.PageCount, movedIndices, destIndex);
        bool isNoOp = true;
        for (int i = 0; i < map.Length && isNoOp; i++)
        {
            isNoOp = map[i] == i;
        }
        if (isNoOp)
        {
            return false;
        }

        var before = GetBookmarks();
        bool ok = await RunPageOpAsync(d => d.MovePages(movedIndices, destIndex), p => p >= 0 && p < map.Length ? map[p] : null);
        if (ok)
        {
            PushEdit(new DocumentEdit
            {
                Label = movedIndices.Count == 1 ? "move page" : $"move {movedIndices.Count} pages",
                IsPageMutation = true,
                ForwardMap = p => p >= 0 && p < map.Length ? map[p] : null,
                ReverseMap = InvertPageMap(map.Length, map.Length, p => p >= 0 && p < map.Length ? map[p] : null),
                SnapshotBytes = 0, // moves store no snapshot — the inverse is a permutation
                BookmarksBefore = before,
                BookmarksAfter = GetBookmarks(),
                UndoAction = d => d.RestoreMovedPages(movedIndices, destIndex),
                RedoAction = d => d.MovePages(movedIndices, destIndex),
            });
        }
        return ok;
    }

    private void ThumbList_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.Handled = true; // ours — don't let the window-level "open as tab" handler take it
        }
    }

    private async void ThumbList_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }
        e.Handled = true;

        int at = DropIndexFromPosition(e.GetPosition(ThumbList));
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFile file &&
                file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                at += await InsertPdfFileAsync(file.Path, at); // keep multi-file order
            }
        }
    }

    /// <summary>Insertion index for a drop at a point in the (vertical) thumbnail list.</summary>
    private int DropIndexFromPosition(Windows.Foundation.Point position)
    {
        for (int i = 0; i < _thumbnails.Count; i++)
        {
            if (ThumbList.ContainerFromIndex(i) is ListViewItem container)
            {
                var top = container.TransformToVisual(ThumbList).TransformPoint(new Windows.Foundation.Point(0, 0));
                if (position.Y < top.Y + container.ActualHeight / 2)
                {
                    return i;
                }
            }
        }
        return _thumbnails.Count;
    }

    private void ThumbList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && ThumbList.SelectedItems.Count > 0)
        {
            _ = DeleteSelectedPagesAsync();
            e.Handled = true;
        }
    }

    private void ThumbList_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ThumbnailItem item &&
            !ThumbList.SelectedItems.Contains(item))
        {
            ThumbList.SelectedIndex = item.PageIndex; // right-click targets what's under the cursor
        }

        int count = ThumbList.SelectedItems.Count;
        if (count == 0)
        {
            return;
        }
        string pages = count == 1 ? UiText.Translate("page") : UiText.Format("{0} pages", count);
        int insertAt = SelectedPageIndices() is { Count: > 0 } sel ? sel[^1] + 1 : _thumbnails.Count;

        var menu = new MenuFlyout();
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ThumbnailItem clicked)
            AddBookmarkMenuAction(menu, clicked.PageIndex);
        if (!IsReadOnlyView) AddMenuAction(menu, UiText.Current.OrganizePages, Symbol.AllApps, () => OpenOrganizer(SelectedPageIndices()));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenuAction(menu, UiText.Format("Copy {0}", pages), Symbol.Copy, () => _ = CopySelectedPagesAsync(cut: false));
        if (!IsReadOnlyView) AddMenuAction(menu, UiText.Format("Cut {0}", pages), Symbol.Cut, () => _ = CopySelectedPagesAsync(cut: true));
        var paste = new MenuFlyoutItem
        {
            Text = PageClipboard.PageCount is > 1 and var n ? UiText.Format("Paste {0} pages after", n) : UiText.Translate("Paste after"),
            Icon = new SymbolIcon(Symbol.Paste),
            IsEnabled = PageClipboard.HasPages,
        };
        paste.Click += (_, _) => _ = PastePagesAsync(insertAt);
        if (!IsReadOnlyView) menu.Items.Add(paste);
        if (!IsReadOnlyView) AddMenuAction(menu, UiText.Translate("Insert PDF here…"), Symbol.Add, () => _ = PickAndInsertPdfAsync(insertAt));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenuAction(menu, UiText.Format("Extract {0} to a new file…", pages), Symbol.Save,
            () => ExtractRequested?.Invoke(this, EventArgs.Empty));
        if (!IsReadOnlyView) AddMenuAction(menu, UiText.Format("Delete {0}", pages), Symbol.Delete, () => _ = DeleteSelectedPagesAsync());

        menu.ShowAt(ThumbList, e.GetPosition(ThumbList));
        e.Handled = true;
    }

    private void AddBookmarkMenuAction(MenuFlyout menu, int page)
    {
        var document = _document;
        bool bookmarked = _bookmarks.Any(b => b.PageIndex == page);
        AddMenuAction(menu, bookmarked ? UiText.Current.RemovePageBookmark : UiText.Current.AddPageBookmark,
            bookmarked ? Symbol.UnPin : Symbol.Pin, () =>
            {
                if (_document == document && _bookmarks.Any(b => b.PageIndex == page) == bookmarked)
                    ToggleBookmark(page);
            });
    }

    private static void AddMenuAction(MenuFlyout menu, string text, Symbol icon, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon) };
        if (icon is Symbol.Delete or Symbol.UnPin) PDFDesk.Styles.DestructiveAction.SetIsEnabled(item, true);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private async Task PickAndInsertPdfAsync(int atIndex)
    {
        var picked = await PDFDesk.Services.FilePickerHost.PickOpenAsync(
            App.OwnerHandle(this), ".pdf");
        if (picked.Failed)
        {
            PageOpFailed?.Invoke(this, PDFDesk.Services.FilePickerHost.FailureMessage);
        }
        else if (picked.File is { } file)
        {
            await InsertPdfFileAsync(file.Path, atIndex);
        }
    }

    // ---------------------------------------------------------------- bookmarks

    private readonly ObservableCollection<BookmarkItem> _bookmarks = [];
    private bool _bookmarksLoaded;
    private List<PDFDesk.Services.BookmarkEntry>? _bookmarkBaseline;
    private List<PDFDesk.Services.BookmarkEntry>? _incomingBookmarks;
    private readonly List<Func<int, int?>> _bookmarkMaps = [];
    private int _bookmarkOriginalPageCount;
    private bool _bookmarkMappingPending => _bookmarkBaseline is not null;

    private List<PDFDesk.Services.BookmarkEntry> FinishBookmarkMapping()
    {
        var result = BookmarksForExport();
        _bookmarkBaseline = null;
        _incomingBookmarks = null;
        _bookmarkMaps.Clear();
        return result;
    }

    internal List<PDFDesk.Services.BookmarkEntry> BookmarksForExport()
    {
        var result = GetBookmarks();
        if (_bookmarkBaseline is not null && _incomingBookmarks is not null)
            result = BookmarkRemap.Merge(_bookmarkBaseline, _incomingBookmarks, result, _bookmarkMaps);
        return result;
    }

    private void NotifyBookmarksChanged(bool saved = false)
    {
        if (!_bookmarkMappingPending) BookmarksChanged?.Invoke(this, saved);
    }

    internal List<PDFDesk.Services.BookmarkEntry> BookmarksForPersistence() =>
        _bookmarkBaseline is null ? GetBookmarks() : [.. _incomingBookmarks ?? _bookmarkBaseline];

    private void BookmarkEdited(int page, string? name)
    {
        if (_bookmarkBaseline is not null)
        {
            var savedPage = BookmarkRemap.FindSavedPage(page, _bookmarkOriginalPageCount, _bookmarkMaps);
            if (savedPage is not int original) return; // A newly inserted page first needs a saved identity.
            var entries = BookmarksForPersistence();
            entries.RemoveAll(b => b.PageIndex == original);
            if (name is not null) entries.Add(new() { PageIndex = original, Name = name });
            _incomingBookmarks = entries.OrderBy(b => b.PageIndex).ToList();
        }
        BookmarksChanged?.Invoke(this, false);
    }

    internal void RefreshBookmarkLabels()
    {
        foreach (var bookmark in _bookmarks) bookmark.RefreshLabels();
    }

    /// <summary>Raised after any bookmark add/remove/rename; the shell persists.</summary>
    public event EventHandler<bool>? BookmarksChanged;
    internal event Action<string, string, List<BookmarkEntry>>? DraftCopySaved;

    /// <summary>Fills the pane from persisted state. First call wins (idempotent across tab switches).</summary>
    public void LoadBookmarks(IEnumerable<PDFDesk.Services.BookmarkEntry> entries, bool refresh = false)
    {
        if (_bookmarkMappingPending && refresh)
            _incomingBookmarks = entries.Select(b => new PDFDesk.Services.BookmarkEntry { PageIndex = b.PageIndex, Name = b.Name }).ToList();
        if (_bookmarkMappingPending || (_bookmarksLoaded && !refresh))
        {
            return;
        }
        _bookmarksLoaded = true;
        _bookmarks.Clear();
        foreach (var entry in entries.OrderBy(b => b.PageIndex))
        {
            _bookmarks.Add(new BookmarkItem(entry.PageIndex, entry.Name));
        }
        RefreshPaneVisibility();
    }

    public List<PDFDesk.Services.BookmarkEntry> GetBookmarks() =>
        [.. _bookmarks.Select(b => new PDFDesk.Services.BookmarkEntry { PageIndex = b.PageIndex, Name = b.Name })];

    /// <summary>Adds a bookmark on the page (or removes the existing one). Returns true when added.</summary>
    public bool ToggleBookmark(int pageIndex)
    {
        var existing = _bookmarks.FirstOrDefault(b => b.PageIndex == pageIndex);
        if (existing is not null)
        {
            _bookmarks.Remove(existing);
        }
        else
        {
            var item = new BookmarkItem(pageIndex, UiText.Format("Page {0}", pageIndex + 1));
            int insertAt = 0;
            while (insertAt < _bookmarks.Count && _bookmarks[insertAt].PageIndex < pageIndex)
            {
                insertAt++;
            }
            _bookmarks.Insert(insertAt, item);
        }
        RefreshPaneVisibility();
        BookmarkEdited(pageIndex, existing is null ? _bookmarks.First(b => b.PageIndex == pageIndex).Name : null);
        return existing is null;
    }

    /// <summary>Switches the sidebar to the Bookmarks pane (used after Ctrl+B when the pane is open).</summary>
    public void ShowBookmarksPane() => ShowSidebar(SidebarPane.Bookmarks);

    private void BookmarkList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookmarkItem item)
        {
            Viewer.GoToPage(item.PageIndex, recordHistory: true);
        }
    }

    private void BookmarkList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && BookmarkList.SelectedItem is BookmarkItem item)
        {
            RemoveBookmark(item);
            e.Handled = true;
        }
    }

    private void BookmarkList_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not BookmarkItem item)
        {
            return;
        }

        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = UiText.Current.Rename, Icon = new SymbolIcon(Symbol.Rename) };
        rename.Click += async (_, _) => await RenameBookmarkAsync(item);
        var delete = new MenuFlyoutItem { Text = UiText.Current.Delete, Icon = new SymbolIcon(Symbol.Delete) };
        PDFDesk.Styles.DestructiveAction.SetIsEnabled(delete, true);
        delete.Click += (_, _) => RemoveBookmark(item);
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.ShowAt(BookmarkList, e.GetPosition(BookmarkList));
        e.Handled = true;
    }

    private void RemoveBookmark(BookmarkItem item)
    {
        _bookmarks.Remove(item);
        RefreshPaneVisibility();
        BookmarkEdited(item.PageIndex, null);
    }

    private async Task RenameBookmarkAsync(BookmarkItem item)
    {
        var document = _document;
        var digest = document?.SavedDigest;
        var maps = _bookmarkMaps.ToArray();
        int page = item.PageIndex;
        string originalName = item.Name;
        var box = new TextBox { Text = item.Name, SelectionStart = item.Name.Length, MinWidth = 280 };
        var dialog = new ContentDialog
        {
            Title = UiText.Translate("Rename bookmark"),
            Content = box,
            PrimaryButtonText = UiText.Current.Rename,
            CloseButtonText = UiText.Current.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await PDFDesk.Services.DialogHost.ShowAsync(this, dialog) == ContentDialogResult.Primary &&
            !string.IsNullOrWhiteSpace(box.Text) && document == _document && digest == document?.SavedDigest &&
            maps.SequenceEqual(_bookmarkMaps) && _bookmarks.FirstOrDefault(b => b.PageIndex == page) is { } current &&
            current.Name == originalName)
        {
            current.Name = box.Text.Trim();
            BookmarkEdited(page, current.Name);
        }
    }

    private void RefreshPaneVisibility() => ShowSidebar(_activePane);

    // ---------------------------------------------------------------- sidebar panes

    private enum SidebarPane
    {
        Thumbnails,
        Chapters,
        Bookmarks,
    }

    private SidebarPane _activePane = SidebarPane.Thumbnails;

    private void SidebarTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var pane = ReferenceEquals(sender, OutlineTab) ? SidebarPane.Chapters
            : ReferenceEquals(sender, BookmarksTab) ? SidebarPane.Bookmarks : SidebarPane.Thumbnails;
        if (_activePane != pane) ShowSidebar(pane);
    }

    private void ShowSidebar(SidebarPane pane)
    {
        _activePane = pane;
        ThumbsTab.IsChecked = pane == SidebarPane.Thumbnails;
        OutlineTab.IsChecked = pane == SidebarPane.Chapters;
        BookmarksTab.IsChecked = pane == SidebarPane.Bookmarks;

        ThumbList.Visibility = pane == SidebarPane.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
        OutlineTree.Visibility = pane == SidebarPane.Chapters && _hasOutline ? Visibility.Visible : Visibility.Collapsed;
        NoOutlineLabel.Visibility = pane == SidebarPane.Chapters && !_hasOutline ? Visibility.Visible : Visibility.Collapsed;

        bool hasBookmarks = _bookmarks.Count > 0;
        BookmarkList.Visibility = pane == SidebarPane.Bookmarks && hasBookmarks ? Visibility.Visible : Visibility.Collapsed;
        NoBookmarksLabel.Visibility = pane == SidebarPane.Bookmarks && !hasBookmarks ? Visibility.Visible : Visibility.Collapsed;
    }
}
