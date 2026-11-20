using System.Reflection;
using PDFDesk.Controls;
using PDFDesk.Engine;
using PDFDesk.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI;

namespace PDFDesk;

public sealed partial class MainWindow : Window
{
    private const string AppTitle = "PDF Desk";
    private const int MinimumWindowWidth = 640;
    private const int MinimumWindowHeight = 420;

    // tools/capture-demo.ps1 points this at a throwaway directory through
    // PDFDESK_STATE_DIR, so a recording cannot pick up the user's own recents,
    // theme or session and paint their filenames into every frame. Unset in
    // normal use, the store uses the PDFDesk folder in Documents.
    private static readonly string? StateDirOverride =
        Environment.GetEnvironmentVariable("PDFDESK_STATE_DIR") is { Length: > 0 } dir ? dir : null;

    private static readonly AppStateStore _store = new(StateDirOverride);
    internal static readonly AppState SharedState = _store.Load();
    private readonly AppState _state = SharedState;
    private Guid? _folderFilter;
    private readonly SessionState _initialSession;
    private readonly bool _startupWindow;
    private PdfLibrary _library;
    private string LibraryFolder => _state.Settings.LibraryFolder ?? _store.LibraryDirectory;
    private readonly Dictionary<DocumentView, RecentFile?> _pendingRestore = [];
    private readonly Dictionary<DocumentView, TabPosition> _detachedPositions = [];
    private readonly HashSet<DocumentView> _savingViews = [];

    private PdfViewer? _activeViewer;
    private bool _quickToolsExpanded = true;
    private bool _suppressPageBox;
    private bool _restoringSession;

    // Find-in-document state.
    private CancellationTokenSource? _searchCts;
    private List<SearchHit> _searchHits = [];
    private int _activeHitIndex = -1;

    private PrintService? _printService;
    private bool _copyingPdfFile;

    internal MainWindow(SessionState session, bool startupWindow)
    {
        _initialSession = session;
        _startupWindow = startupWindow;
        InitializeComponent();
        AboutVersion.Text = CurrentVersion;
        AttachShelfDragHandlers();
        ViewActionsFlyout.OverlayInputPassThroughElement = Toolbar;
        DocumentActionsFlyout.OverlayInputPassThroughElement = Toolbar;
        Title = AppTitle;

        ((OverlappedPresenter)AppWindow.Presenter).SetBorderAndTitleBar(true, false);
        RootGrid.Loaded += RootGrid_Loaded;
        AppWindow.Changed += (_, _) => UpdateCaptionState();

        var newWindow = new MenuFlyoutItem();
        newWindow.Click += (_, _) => App.CreateReaderWindow();
        var homeMenu = new MenuFlyout { Items = { newWindow } };
        homeMenu.Opening += (_, _) => newWindow.Text = UiText.Current.NewWindow;
        HomeButton.ContextFlyout = homeMenu;
        _folderFilter = session.FavoritesSelected.HasValue ? session.FolderFilter : _state.Settings.FavoriteFolderFilter;
        if (session.FavoritesSelected ?? _state.Settings.FavoritesTabSelected) FavoritesTab.IsChecked = true;
        _library = new PdfLibrary(LibraryFolder);
        ApplyLanguage();
        ApplyTheme(_state.Settings.Theme);
        NightItem.IsChecked = _state.Settings.NightMode;

        RegisterAccelerators();
        ((UIElement)Content).KeyDown += Content_KeyDown;
        // Tunneling handler: navigation keys must reach the document even when
        // focus sits on the tab strip or a toolbar button (those controls eat
        // arrow keys in the bubbling phase for their own focus movement).
        ((UIElement)Content).PreviewKeyDown += Content_PreviewKeyDown;
        ((UIElement)Content).GotFocus += (_, args) => ClearPageObjectSelectionForInput(args.OriginalSource);
        ((UIElement)Content).AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, args) => ClearPageObjectSelectionForInput(args.OriginalSource)), true);
        // The pen panel is built lazily on first use (BuildInkFlyout).

        PopulateRecents();

        RootGrid.Loaded += RestoreOnFirstLoad;
        Closed += MainWindow_Closed;
        Activated += (_, args) =>
        {
            if (!App.Exiting && args.WindowActivationState == WindowActivationState.Deactivated && CurrentView is { } leaving) CaptureState(leaving);
            if (args.WindowActivationState == WindowActivationState.Deactivated && _dragCard != null)
                EndShelfDrag();
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                if (SettingsPage.Visibility == Visibility.Visible) RefreshDefaultPdfStatus();
                else if (CurrentView == null && App.Readers.Count > 1) PopulateRecents();
            }
        };
        AppWindow.Closing += AppWindow_Closing;
    }

    private bool _closeApproved;
    private bool _exitInProgress;
    private TrayIcon? _trayIcon;

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved) return;
        args.Cancel = true;
        DispatcherQueue.TryEnqueue(RequestWindowClose);
    }

    private void RequestWindowClose()
    {
        if (_exitInProgress) return;
        if (App.Readers.Count > 1)
        {
            CloseReader();
            return;
        }
        if (!_state.Settings.CloseToTray)
        {
            ExitApplication();
            return;
        }

        if (ShowSharedTray())
        {
            AppWindow.Hide();
        }
        else if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Minimize();
        }
    }

    internal void RestoreWindow()
    {
        CloseTrayMenu();
        if (!AppWindow.IsVisible) AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        Activate();
        TrayIcon.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));

    }

    internal bool ShowSharedTray()
    {
        _trayIcon ??= new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this),
            () => DispatcherQueue.TryEnqueue(() => ((MainWindow?)App.MainWindow ?? this).RestoreWindow()),
            (x, y) => DispatcherQueue.TryEnqueue(() => ShowTrayMenu(x, y)));
        return _trayIcon.Show();
    }

    private async void CloseReader()
    {
        if (_exitInProgress) return;
        _exitInProgress = true;
        try
        {
            if (await EnsureSafeToCloseAsync())
            {
                _closeApproved = true;
                Close();
            }
        }
        finally { _exitInProgress = false; }
    }

    private async void ExitApplication()
    {
        if (_exitInProgress) return;
        _exitInProgress = true;
        try
        {
            CloseTrayMenu();
            var windows = App.Readers.ToArray();
            foreach (var window in windows)
                if (!await window.EnsureSafeToCloseAsync()) return;
            if (App.MainWindow is MainWindow active && active.CurrentView is { } view) active.CaptureState(view);
            App.Exiting = true;
            SaveWindowSessions();
            foreach (var window in windows)
            {
                window._closeApproved = true;
                window.Close();
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Default.Write(nameof(ExitApplication), ex);
            ShowError(UiText.Format("Couldn't close cleanly: {0}", ex.Message));
        }
        finally
        {
            _exitInProgress = false;
        }
    }

    /// <summary>
    /// Prompts for unsaved documents and saves them if asked. Returns false when
    /// the user cancelled or a save failed — i.e. it is NOT safe to close.
    /// Used for exits from the window and the tray icon.
    /// </summary>
    private async Task<bool> EnsureSafeToCloseAsync()
    {
        if (_compressionTasks.Count != 0)
        {
            RestoreWindow();
            ShowNotice(UiText.Current.FinishExportBeforeClosing, InfoBarSeverity.Informational);
            return false;
        }
        var dirty = AllDocumentViews().Where(v => v.IsDirty).ToList();
        if (dirty.Count == 0)
        {
            return true;
        }

        if (!AppWindow.IsVisible) RestoreWindow();
        var choice = await PromptSaveChangesAsync(dirty.Select(view => view.DisplayName).ToArray());
        if (choice is null)
        {
            return false; // cancelled
        }
        if (choice == true)
        {
            foreach (var view in dirty)
            {
                try
                {
                    await view.SaveInPlaceAsync();
                }
                catch (Exception ex)
                {
                    ShowError(UiText.Format("Save failed: {0}", ex.Message));
                    return false;
                }
            }
        }
        return true;
    }

    private void ApplyTheme(string theme)
    {
        var root = (FrameworkElement)Content;
        root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

    }

    private void MinimizeCaption_Click(object sender, RoutedEventArgs e) =>
        ((OverlappedPresenter)AppWindow.Presenter).Minimize();

    private void MaximizeCaption_Click(object sender, RoutedEventArgs e)
    {
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
        else presenter.Maximize();
    }

    private void CloseCaption_Click(object sender, RoutedEventArgs e) => RequestWindowClose();

    private void UpdateCaptionState()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        MaximizeMark.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreMark.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MaximizeCaption, maximized ? UiText.Current.Restore : UiText.Current.Maximize);
    }

    private CaptionHitTest? _captionHitTest;
    private Microsoft.UI.Input.InputNonClientPointerSource? _titleBarInput;
    private Windows.Graphics.RectInt32 _dragRect;

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        _captionHitTest = new CaptionHitTest(WinRT.Interop.WindowNative.GetWindowHandle(this),
            RootGrid, MaximizeCaption);
        _titleBarInput = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        RootGrid.LayoutUpdated += UpdateDragRegion;
        UpdateDragRegion(null, null);
        UpdateCaptionState();
        UpdateMinimumWindowSize();
        RootGrid.XamlRoot.Changed += (_, _) => UpdateMinimumWindowSize();
    }

    private void UpdateDragRegion(object? sender, object? args)
    {
        if (_titleBarInput is null || RootGrid.XamlRoot is not { } root) return;
        var scale = root.RasterizationScale;
        double start = NewDocumentButton.TransformToVisual(RootGrid).TransformPoint(default).X + NewDocumentButton.ActualWidth;
        int left = (int)Math.Ceiling(start * scale);
        int right = (int)Math.Floor(CaptionButtons.TransformToVisual(RootGrid).TransformPoint(default).X * scale);
        int top = (int)Math.Ceiling(3 * scale);
        var rect = new Windows.Graphics.RectInt32(left, top, Math.Max(0, right - left),
            Math.Max(0, (int)Math.Floor(CaptionButtons.ActualHeight * scale) - top));
        if (rect.Equals(_dragRect)) return;
        _dragRect = rect;
        _titleBarInput.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption,
            rect.Width > 0 && rect.Height > 0 ? [rect] : []);
    }

    private void UpdateMinimumWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter ||
            RootGrid.XamlRoot is not { } root)
        {
            return;
        }

        // The presenter takes physical pixels. Recompute the limits when DPI changes.
        int width = (int)Math.Ceiling(MinimumWindowWidth * root.RasterizationScale);
        int height = (int)Math.Ceiling(MinimumWindowHeight * root.RasterizationScale);
        if (presenter.PreferredMinimumWidth != width)
        {
            presenter.PreferredMinimumWidth = width;
        }
        if (presenter.PreferredMinimumHeight != height)
        {
            presenter.PreferredMinimumHeight = height;
        }
    }

    // ---------------------------------------------------------------- lifecycle

    private async void RestoreOnFirstLoad(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RestoreOnFirstLoad;
        try
        {
            await RestoreSessionAsync();
            if (_startupWindow) await RemindAboutDefaultPdfAppAsync();
        }
        catch (Exception ex)
        {
            // async void: this is the app's most dangerous one — an escape here
            // kills the process during startup.
            ErrorLog.Default.Write(nameof(RestoreOnFirstLoad), ex);
            ShowError(UiText.Format("Startup problem: {0}", ex.Message));
        }
    }

    private async Task RestoreSessionAsync()
    {
        if (!_state.Settings.RestoreSession)
        {
            UpdateStartPageVisibility();
            return;
        }

        _restoringSession = true;
        var session = _initialSession;
        var requestedTab = Tabs.SelectedItem;
        var savedPath = session.OpenPaths.ElementAtOrDefault(session.ActiveIndex);
        var existing = AllDocumentViews().Select(v => v.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in session.OpenPaths.Where(File.Exists))
        {
            if (existing.Add(path)) AddTab(path, session.RestorePosition(path, _state.FindRecent(path)), select: false);
        }

        Tabs.SelectedItem = requestedTab ?? Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(
            tab => tab.Tag is DocumentView view && string.Equals(view.FilePath, savedPath, StringComparison.OrdinalIgnoreCase));
        _restoringSession = false;

        UpdateStartPageVisibility();
        if (Tabs.SelectedItem is TabViewItem item && item.Tag is DocumentView view)
        {
            await LoadTabAsync(view);
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        ExitPresentation();
        CloseTrayMenu();
        _trayIcon?.Dispose();
        RootGrid.LayoutUpdated -= UpdateDragRegion;
        _captionHitTest?.Dispose();
        foreach (var obj in Tabs.TabItems)
        {
            if (obj is TabViewItem { Tag: DocumentView view })
            {
                view.Close();
            }
        }
        App.RemoveReader(this);
        if (!App.Exiting && App.Readers.Count > 0) SaveWindowSessions();
        else _store.Save(_state);
        _thumbnails.Dispose(); // stops the homepage cache's PDFium thread
    }

    internal static void SaveWindowSessions()
    {
        if (App.Readers.Count == 0) return;
        var windows = App.Readers.OrderByDescending(w => ReferenceEquals(w, App.MainWindow)).ToList();
        SessionState Snapshot(MainWindow w) => new()
        {
            Positions = w.AllDocumentViews().Select(v => (v.FilePath, Value: w.PositionOf(v)))
                .Where(p => p.Value != null).ToDictionary(p => p.FilePath, p => p.Value!, StringComparer.OrdinalIgnoreCase),
            FavoritesSelected = w.FavoritesTab.IsChecked == true,
            FolderFilter = w._folderFilter,
            OpenPaths = w.AllDocumentViews().Select(v => v.FilePath).ToList(),
            ActiveIndex = w._settingsReturnTab is { } tab ? w.Tabs.TabItems.IndexOf(tab) : w.Tabs.SelectedIndex
        };
        SharedState.Session = Snapshot(windows[0]);
        SharedState.AdditionalWindows = windows.Skip(1).Select(Snapshot).ToList();
        _store.Save(SharedState);
    }

    private TabPosition? PositionOf(DocumentView view)
    {
        if (!ReferenceEquals(view, DocHost.Content) && _detachedPositions.TryGetValue(view, out var detached)) return detached;
        if (view.IsDocumentLoaded && view.LoadError == null)
        {
            var viewer = view.Viewer;
            return new(viewer.CurrentPage, viewer.Zoom, viewer.ViewRotation, viewer.ScrollFraction, view.IsPaneOpen, viewer.PageColumns);
        }
        var restore = _pendingRestore.GetValueOrDefault(view) ??
            _initialSession.RestorePosition(view.FilePath, _state.FindRecent(view.FilePath));
        return restore == null ? null : TabPosition.From(restore);
    }

    private void CaptureState(DocumentView view)
    {
        if (!view.IsDocumentLoaded || view.LoadError is not null)
        {
            return;
        }
        var position = PositionOf(view)!;
        _state.Remember(view.FilePath, view.DisplayName,
            position.PageIndex, position.Zoom, position.Rotation, position.ScrollFraction,
            sidebarOpen: position.SidebarOpen, pageColumns: position.PageColumns);
        if (_state.FindRecent(view.FilePath) is { } entry)
        {
            entry.IsLibraryCopy |= _library.Contains(view.FilePath);
        }
    }

    // ---------------------------------------------------------------- tabs

    private void AddTab(string path, RecentFile? restore, bool select, int? index = null)
    {
        var view = new DocumentView(path);
        _pendingRestore[view] = restore;
        view.Viewer.LinkActivated += Viewer_LinkActivated;
        view.Viewer.NightMode = _state.Settings.NightMode;
        SeedToolStyles(view.Viewer);
        view.Viewer.CompressionRequested += () => _ = CompressExportAsync(view.FilePath);
        view.Viewer.DocumentEdited += (_, _) => UpdateDirtyIndicator(view);
        view.Viewer.ActiveToolChanged += (_, tool) =>
        {
            if (view.Viewer == _activeViewer) SyncToolButtons(tool);
        };
        view.Viewer.StampPlaced += (_, placement) =>
        {
            if (placement.Tool == AnnotationTool.Image)
            {
                _state.Settings.ImageWidthPt = placement.WidthPt;
            }
            else
            {
                _state.Settings.SignatureWidthPt = placement.WidthPt;
            }
            _store.Save(_state);
        };
        view.SignaturesRead += (_, _) => UpdateToolbarForActive();
        view.Viewer.NoteRequested += Viewer_NoteRequested;
        view.Viewer.FormAppearanceRequested += Viewer_FormAppearanceRequested;
        AttachTextTool(view.Viewer);
        view.BookmarksChanged += (_, _) => PersistBookmarks(view);
        view.PagesEdited += (_, _) =>
        {
            UpdateDirtyIndicator(view);
            if (view == CurrentView)
            {
                UpdateToolbarForActive(); // page count / current page changed
            }
        };
        view.PageOpFailed += (_, message) => ShowError(message);
        view.ExtractRequested += (_, _) => _ = ExtractPagesAsync(view);
        view.UndoStateChanged += (_, _) =>
        {
            if (view == CurrentView)
            {
                UpdateUndoButtons();
            }
        };
        view.Loaded2 += (_, _) => { if (view == CurrentView) { UpdateToolbarForActive(); } };

        // Tabs are strip-only (they live in the title bar); the view itself
        // is swapped into DocHost on selection. Tag carries the association.
        var tab = new TabViewItem
        {
            Header = view.DisplayName,
            Tag = view,
            Template = (ControlTemplate)RootGrid.Resources["DocumentTabTemplate"],
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(8, 0, 4, 0),
        };
        var move = new MenuFlyoutItem();
        var open = new MenuFlyoutItem();
        var menu = new MenuFlyout { Items = { open, move } };
        menu.Opening += (_, _) => { open.Text = UiText.Current.OpenInNewWindow; move.Text = UiText.Current.MoveTabToWindow; };
        open.Click += async (_, _) => await OpenTabInWindowAsync(tab, view, move: false);
        move.Click += async (_, _) => await OpenTabInWindowAsync(tab, view, move: true);
        tab.ContextFlyout = menu;
        if (index is int slot) Tabs.TabItems.Insert(slot, tab);
        else Tabs.TabItems.Add(tab);
        if (select)
        {
            Tabs.SelectedItem = tab;
        }
    }

    /// <summary>Opens a path in a new tab, or activates the existing tab if already open.</summary>
    private void OpenOrActivate(string path)
    {
        foreach (var obj in Tabs.TabItems)
        {
            if (obj is TabViewItem { Tag: DocumentView v } tabItem &&
                string.Equals(v.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                Tabs.SelectedItem = tabItem;
                return;
            }
        }

        AddTab(path, _state.FindRecent(path), select: true);
    }

    private async Task OpenTabInWindowAsync(TabViewItem tab, DocumentView view, bool move)
    {
        if (!_savingViews.Add(view)) return;
        MainWindow? destination = null;
        TabViewItem? createdTab = null;
        bool editedDuringTransfer = false;
        void OnEdit(object? sender, EventArgs args) => editedDuringTransfer = true;
        try
        {
            if (move && view.IsDocumentLoaded)
            {
                await view.Viewer.FinishEraserAsync();
                await view.Viewer.CommitTextAsync();
                if (view.Viewer.IsEditingText) throw new IOException(UiText.Current.TextCommitFailed);
                if (view.Viewer.IsFormFieldFocused) view.Viewer.KillFormFocus();
            }
            if (move && view.IsDirty)
            {
                var choice = await PromptSaveChangesAsync([view.DisplayName]);
                if (choice is null) return;
                if (choice.Value) await view.SaveInPlaceAsync();
            }
            CaptureState(view);
            view.Viewer.DocumentEdited += OnEdit;
            destination = App.CreateReaderWindow();
            destination.AddTab(view.FilePath, PositionOf(view)?.ForPath(view.FilePath), select: true);
            createdTab = (TabViewItem)destination.Tabs.SelectedItem;
            var copy = (DocumentView)createdTab.Tag;
            await copy.EnsureLoadedAsync(null);
            if (!App.Readers.Contains(destination) || !destination.Tabs.TabItems.Contains(createdTab)) return;
            if (!copy.IsDocumentLoaded || copy.LoadError is not null)
                throw new IOException(copy.LoadError ?? view.FilePath);
            if (move && App.Readers.Contains(this) && Tabs.TabItems.Contains(tab))
            {
                if (editedDuringTransfer || view.Viewer.IsEditingText || view.Viewer.IsFormFieldFocused)
                    throw new IOException(UiText.Current.SourceChangedDuringMove);
                CloseTab(tab);
            }
            SaveWindowSessions();
        }
        catch (Exception ex)
        {
            if (destination != null && App.Readers.Contains(destination))
            {
                if (createdTab != null && destination.Tabs.TabItems.Contains(createdTab)) destination.CloseTab(createdTab);
                if (destination.Tabs.TabItems.Count == 0) { destination._closeApproved = true; destination.Close(); }
            }
            if (!App.Readers.Contains(this)) return;
            RestoreWindow();
            ShowError(ex.Message);
        }
        finally { view.Viewer.DocumentEdited -= OnEdit; _savingViews.Remove(view); }
    }

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = CurrentView;
        if (_presentationWindow != null) ExitPresentation();
        if (!_restoringSession && !App.Exiting && DocHost.Content is DocumentView previous && previous != selected &&
            previous.IsDocumentLoaded && PositionOf(previous) is { } position)
        {
            _detachedPositions[previous] = position;
            CaptureState(previous);
        }
        ViewActionsFlyout?.Hide();
        DocumentActionsFlyout?.Hide();
        HideToolOptions();
        if (selected != null && SettingsPage.Visibility == Visibility.Visible) LeaveSettings();
        var previousLayout = (DocHost.Content as DocumentView)?.ReaderLayout;
        foreach (var (control, row) in new (FrameworkElement Control, int Row)[]
                 { (Toolbar, 0), (ReaderOverlays, 1), (ReaderQuickTools, 1) })
        {
            // Back-to-back selections may precede WinUI attaching the previous
            // view to the visual tree. Its child collection already owns the tools.
            if (previousLayout?.Children.Contains(control) == true)
                previousLayout.Children.Remove(control);
            else if (control.Parent is Panel parent)
                parent.Children.Remove(control);
            Grid.SetRow(control, row);
            selected?.ReaderLayout.Children.Add(control);
        }
        // Both docked surfaces need the same coordinate system for pixel rounding at fractional DPI.
        if (ExportQueue != null)
        {
            if (ExportQueue.Parent is Panel oldParent) oldParent.Children.Remove(ExportQueue);
            var target = selected?.ReaderLayout ?? RootGrid;
            Grid.SetRow(ExportQueue, selected == null ? 2 : 1);
            target.Children.Add(ExportQueue);
            Canvas.SetZIndex(ExportQueue, 1);
            UpdateExportQueue();
        }
        DocHost.Content = selected;
        HookActiveViewer();
        if (_focusReading && _activeViewer?.ActiveTool is not (null or AnnotationTool.None))
            SetActiveTool(AnnotationTool.None);
        UpdateStartPageVisibility();

        if (!_restoringSession && CurrentView is { } view)
        {
            UpdateToolbarForActive();
            await LoadTabAsync(view);
        }
    }

    private async Task LoadTabAsync(DocumentView view)
    {
        await view.EnsureLoadedAsync(_pendingRestore.GetValueOrDefault(view));
        if (!App.Readers.Contains(this) || !AllDocumentViews().Contains(view)) return;
        bool firstLoad = _pendingRestore.Remove(view);

        if (view.LoadError is null)
        {
            view.LoadBookmarks(_state.FindRecent(view.FilePath)?.Bookmarks ?? []);
            if (firstLoad && view.IsDocumentLoaded)
            {
                if (CurrentView == view || _state.FindRecent(view.FilePath) == null) CaptureState(view);
                _store.Save(_state);
                if (CurrentView is null) PopulateRecents();
            }
        }
        if (view.LoadError is { } error && view == CurrentView)
        {
            ShowError(UiText.Translate(error));
        }
        if (view == CurrentView)
        {
            UpdateToolbarForActive();
        }
    }

    /// <summary>Writes a view's bookmarks into the recents entry (creating one if needed) and saves.</summary>
    private void PersistBookmarks(DocumentView view)
    {
        var entry = _state.FindRecent(view.FilePath);
        if (entry is null)
        {
            CaptureState(view); // creates the recents entry with the current position
            entry = _state.FindRecent(view.FilePath);
        }
        if (entry is not null)
        {
            entry.Bookmarks = view.GetBookmarks();
            foreach (var other in App.Readers.SelectMany(w => w.AllDocumentViews()))
                if (other != view && other.IsDocumentLoaded && string.Equals(other.FilePath, view.FilePath, StringComparison.OrdinalIgnoreCase))
                    other.LoadBookmarks(entry.Bookmarks, refresh: true);
            _store.Save(_state);
        }
    }

    private void ToggleBookmark()
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view || _activeViewer is null)
        {
            return;
        }
        view.ToggleBookmark(_activeViewer.CurrentPage);
        if (view.IsPaneOpen)
        {
            view.ShowBookmarksPane(); // show the result where it landed
        }
    }


    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        => await CloseTabWithPromptAsync(args.Tab);

    private async Task CloseTabWithPromptAsync(TabViewItem tab)
    {
        if (tab.Tag is DocumentView { IsDirty: true } dirtyView)
        {
            var choice = await PromptSaveChangesAsync([dirtyView.DisplayName]);
            if (choice is null)
            {
                return; // cancelled
            }
            if (choice == true)
            {
                try
                {
                    await dirtyView.SaveInPlaceAsync();
                }
                catch (Exception ex)
                {
                    ShowError(UiText.Format("Save failed: {0}", ex.Message));
                    return;
                }
            }
        }
        CloseTab(tab);
    }

    /// <summary>true = save, false = discard, null = cancel.</summary>
    private async Task<bool?> PromptSaveChangesAsync(string[] names)
    {
        var dialog = new ContentDialog
        {
            Title = UiText.Current.SaveBeforeClosing,
            Content = names,
            ContentTemplate = (DataTemplate)RootGrid.Resources["UnsavedDocumentsTemplate"],
            PrimaryButtonText = names.Length == 1 ? UiText.Current.Save : UiText.Current.SaveAllChanges,
            SecondaryButtonText = UiText.Current.DiscardChanges,
            CloseButtonText = UiText.Current.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        return await ShowDialogAsync(dialog) switch
        {
            ContentDialogResult.Primary => true,
            ContentDialogResult.Secondary => false,
            _ => null,
        };
    }

    private void CloseTab(TabViewItem tab)
    {
        if (tab.Tag is DocumentView view)
        {
            CaptureState(view);
            view.Viewer.LinkActivated -= Viewer_LinkActivated;
            view.Viewer.NoteRequested -= Viewer_NoteRequested;
            view.Viewer.FormAppearanceRequested -= Viewer_FormAppearanceRequested;
            DetachTextTool(view.Viewer);
            view.Close();
            _pendingRestore.Remove(view);
            _detachedPositions.Remove(view);
        }
        Tabs.TabItems.Remove(tab);
        tab.Tag = null;
        UpdateStartPageVisibility();
    }

    private DocumentView? CurrentView =>
        (Tabs.SelectedItem as TabViewItem)?.Tag as DocumentView;

    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPage.Visibility == Visibility.Visible) LeaveSettings();
        Tabs.SelectedItem = null;
        UpdateStartPageVisibility();
    }

    private void StripSpace_SizeChanged(object sender, SizeChangedEventArgs e) => SizeDocumentStrip();

    private void SizeDocumentStrip()
    {
        if (StripSpace.ActualWidth <= 0) return;
        int count = Tabs.TabItems.Count;
        double fixedWidth = StripHeader.ActualWidth + StripHeader.Margin.Left + StripHeader.Margin.Right
            + 16; // Native tab list header, footer and viewport insets.
        fixedWidth += NewDocumentButton.Width + NewDocumentButton.Margin.Right;
        double slot = Math.Clamp(Math.Floor((StripSpace.ActualWidth - fixedWidth) / Math.Max(1, count)), 116, 204);
        StripGroup.Width = Math.Min(StripSpace.ActualWidth, fixedWidth + count * slot);
        foreach (var tab in Tabs.TabItems.OfType<TabViewItem>())
        {
            double width = slot - tab.Margin.Left - tab.Margin.Right;
            tab.MinWidth = tab.MaxWidth = width;
        }
    }

    private void UpdateStartPageVisibility()
    {
        bool hasTabs = Tabs.TabItems.Count > 0;
        bool showDocument = CurrentView is not null;

        StartPage.Visibility = showDocument ? Visibility.Collapsed : Visibility.Visible;
        HomeSelection.Visibility = StartPage.Visibility;
        DocHost.Visibility = showDocument ? Visibility.Visible : Visibility.Collapsed;
        HomeButton.IsTabStop = hasTabs;
        SizeDocumentStrip();

        Title = showDocument && CurrentView is { } view ? $"{view.DisplayName} — {AppTitle}" : AppTitle;
        UpdateFocusReadingControls();
        if (!showDocument)
        {
            PopulateRecents();
        }
    }

    private bool _focusReading;
    private Storyboard? _focusTransition;

    private async void FocusReadingButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_focusReading && _activeViewer is { IsEditingText: true } viewer)
        {
            await viewer.CommitTextAsync();
            if (viewer.IsEditingText || viewer != _activeViewer) return;
        }
        SetFocusReading(!_focusReading);
    }

    private void SetFocusReading(bool enabled)
    {
        _focusReading = enabled;
        if (enabled)
        {
            SetActiveTool(AnnotationTool.None);
            HideToolOptions();
            ViewActionsFlyout.Hide();
            DocumentActionsFlyout.Hide();
        }
        UpdateFocusReadingControls();
        UpdateExportQueue();
        AnimateFocusReading(enabled);
    }

    private void AnimateFocusReading(bool entering)
    {
        _focusTransition?.Stop();
        _focusTransition = null;
        if (CurrentView is not { } view || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;

        var transition = new Storyboard();
        var offset = (TranslateTransform)view.Viewer.RenderTransform;
        double direction = entering ? 1 : -1;
        if (view.IsPaneOpen) Add(offset, "X", direction * 8, 0);
        Add(offset, "Y", direction * 6, 0);
        if (!entering)
        {
            Add(Toolbar, "Opacity", 0.35, 1);
            Add(ReaderQuickTools, "Opacity", 0.35, 1);
        }
        _focusTransition = transition;
        transition.Completed += (_, _) =>
        {
            if (ReferenceEquals(_focusTransition, transition)) _focusTransition = null;
        };
        transition.Begin();

        void Add(DependencyObject target, string property, double from, double to)
        {
            var animation = new DoubleAnimation
            {
                From = from, To = to, Duration = TimeSpan.FromMilliseconds(160),
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            transition.Children.Add(animation);
        }
    }

    private void UpdateFocusReadingControls()
    {
        CurrentView?.SetPaneHiddenForFocus(_focusReading);
        bool reading = CurrentView != null;
        FocusReadingButton.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
        FocusReadingHighlight.Visibility = _focusReading ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = ReaderQuickTools.Visibility = reading && !_focusReading ? Visibility.Visible : Visibility.Collapsed;
        TextFormatBar.Visibility = !_focusReading && _activeViewer?.IsEditingText == true ? Visibility.Visible : Visibility.Collapsed;
        string label = _focusReading ? UiText.Current.ExitFocusReading : UiText.Current.EnterFocusReading;
        AutomationProperties.SetName(FocusReadingButton, label);
        ToolTipService.SetToolTip(FocusReadingButton, label);
    }

    private void EmptyState_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Move the artwork above the copy when the side-by-side layout would wrap.
        bool compact = e.NewSize.Width < 320;
        HomeOpener.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        var resources = Application.Current.Resources;
        HomePaperArt.Height = compact ? (double)resources["HomeCompactPaperHeight"] : double.NaN;
        HomeOpener.Spacing = (double)resources[compact ? "SpaceM" : "SpaceXL"];
        HomeOpenHeading.TextAlignment = compact ? TextAlignment.Center : TextAlignment.Left;
        HomeOpenHint.TextAlignment = HomeOpenHeading.TextAlignment;
    }

    // ---------------------------------------------------------------- active viewer wiring

    private void HookActiveViewer()
    {
        if (_activeViewer is not null)
        {
            _activeViewer.CurrentPageChanged -= Viewer_CurrentPageChanged;
            _activeViewer.ZoomChanged -= Viewer_ZoomChanged;
            _activeViewer.InkStrokeStarted -= Viewer_InkStrokeStarted;
        }

        _activeViewer = CurrentView?.Viewer;

        if (_activeViewer is not null)
        {
            _activeViewer.CurrentPageChanged += Viewer_CurrentPageChanged;
            _activeViewer.ZoomChanged += Viewer_ZoomChanged;
            _activeViewer.InkStrokeStarted += Viewer_InkStrokeStarted;
        }

        // Re-run any active search against the newly-focused document.
        if (FindBar.Visibility == Visibility.Visible)
        {
            RunSearch();
        }
    }

    private void Viewer_CurrentPageChanged(object? sender, int pageIndex)
    {
        _suppressPageBox = true;
        PageBox.Value = pageIndex + 1;
        _suppressPageBox = false;
        UpdatePageStepButtons(_activeViewer);
    }

    /// <summary>Drawing started — get the tool's options panel out of the way.</summary>
    private void Viewer_InkStrokeStarted(object? sender, EventArgs e) => HideToolOptions();

    private void Viewer_ZoomChanged(object? sender, double zoom)
    {
        ZoomLabel.Text = $"{Math.Round(zoom * 100)}%";
        UpdateFitToggles();
    }

    private void UpdateToolbarForActive()
    {
        var view = CurrentView;
        bool ready = view is { IsDocumentLoaded: true, LoadError: null };
        var viewer = ready ? view!.Viewer : null;
        UpdatePageStepButtons(viewer);
        UpdateReaderSaveButton();

        foreach (var control in new Control[]
                 {
                     ViewActionsButton, SidebarButton, PageBox, SinglePageButton, TwoPageButton, ThreePageButton,
                     ZoomInButton, ZoomOutButton, ZoomLabelButton, PresentButton,
                     // The annotation cluster. A tool button left out of this
                     // list stays permanently greyed out — nothing else enables it.
                     PenToolButton, HighlighterToolButton, NoteToolButton,
                     TextToolButton, EraserToolButton, ImageToolButton, SignToolButton,
                 })
        {
            control.IsEnabled = ready;
        }
        foreach (var item in new MenuFlyoutItem[]
                 { ReaderSaveAsItem, CompressExportItem, PrintItem, FindItem, DocumentInfoItem, FlattenItem, CopyFilePathItem })
        {
            item.IsEnabled = ready;
        }
        CopyPdfFileItem.IsEnabled = ready && !_copyingPdfFile;
        UpdateUndoButtons();

        if (viewer is null)
        {
            PageCountLabel.Text = "";
            return;
        }

        // Deliberately cheap. Asking the document what it contains would mean
        // PDFium calls on the UI thread — HasFlattenableContent() alone walks
        // every page — and this runs on every tab switch. Flatten stays
        // available and reports "nothing to flatten".
        UpdateDocumentNotice(viewer);

        _suppressPageBox = true;
        PageBox.Width = Math.Max(44, viewer.PageCount.ToString().Length * 8 + 8);
        PageBox.Maximum = viewer.PageCount;
        PageBox.Value = viewer.CurrentPage + 1;
        _suppressPageBox = false;
        var pageLayoutButton = viewer.PageColumns switch
        {
            2 => TwoPageButton,
            3 => ThreePageButton,
            _ => SinglePageButton,
        };
        pageLayoutButton.IsChecked = true;
        PageCountLabel.Text = $"/ {viewer.PageCount}";
        ZoomLabel.Text = $"{Math.Round(viewer.Zoom * 100)}%";
        SidebarButton.IsChecked = view!.IsPaneOpen;
        SyncToolButtons(viewer.ActiveTool);
        UpdateFitToggles();
    }

    /// <summary>
    /// Arms a tool (or disarms with <see cref="AnnotationTool.None"/>).
    ///
    /// Tools are mutually exclusive, so this is the single place the armed tool
    /// changes — the buttons are only a reflection of the viewer's state, never
    /// the source of truth, which is what keeps them correct across tab
    /// switches.
    /// </summary>
    private void SetActiveTool(AnnotationTool tool)
    {
        if (_activeViewer is null)
        {
            return;
        }
        if (_focusReading && tool != AnnotationTool.None) SetFocusReading(false);
        if (tool != _activeViewer.ActiveTool && _activeViewer.ActiveTool is AnnotationTool.Image or AnnotationTool.Signature)
            _activeViewer.ClearPendingStamp();
        _activeViewer.ActiveTool = tool;
        SyncToolButtons(tool);
        if (tool == AnnotationTool.None)
        {
            HideToolOptions(); // covers Esc/Ctrl+E while a panel is open
        }
    }

    /// <summary>Reflects the armed tool onto the cluster. Never sets it.</summary>
    private void SyncToolButtons(AnnotationTool tool)
    {
        foreach (var previous in _toolPanels)
        {
            if (previous.Key != tool && HasHoverOptions(previous.Key) && previous.Value.Flyout.IsOpen)
                previous.Value.Flyout.Hide();
        }
        PenToolButton.IsChecked = tool == AnnotationTool.Pen;
        HighlighterToolButton.IsChecked = tool == AnnotationTool.Highlighter;
        NoteToolButton.IsChecked = tool == AnnotationTool.Note;
        TextToolButton.IsChecked = tool == AnnotationTool.Text;
        ImageToolButton.IsChecked = tool == AnnotationTool.Image;
        SignToolButton.IsChecked = tool == AnnotationTool.Signature;
        EraserToolButton.IsChecked = tool == AnnotationTool.Eraser;
    }

    /// <summary>
    /// A tool button arms its tool and shows its options, and pressing the armed
    /// tool again puts it away.
    ///
    /// It used to re-open the panel instead, on the grounds that turning drawing
    /// off by accident mid-session was the more annoying failure and Esc was
    /// there to disarm. Pressing again works without a keyboard.
    /// </summary>
    private void ToolButton_Click(AnnotationTool tool)
    {
        if (_activeViewer?.ActiveTool == tool)
        {
            SetActiveTool(AnnotationTool.None);
            return;
        }
        SetActiveTool(tool);
        ShowToolOptions(tool, hover: tool == _hoveredButton);
    }

    private void PenToolButton_Click(object sender, RoutedEventArgs e) => ToolButton_Click(AnnotationTool.Pen);
    private void HighlighterToolButton_Click(object sender, RoutedEventArgs e) => ToolButton_Click(AnnotationTool.Highlighter);
    private void NoteToolButton_Click(object sender, RoutedEventArgs e) => ToolButton_Click(AnnotationTool.Note);
    private void ImageToolButton_Click(object sender, RoutedEventArgs e) => ToggleImageTool();
    private void SignToolButton_Click(object sender, RoutedEventArgs e) => ToolButton_Click(AnnotationTool.Signature);

    /// <summary>
    /// The picture button runs the picker rather than arming an empty tool:
    /// there is nothing to place until a file has been chosen, and a tool that
    /// looks armed but does nothing on click is worse than no tool at all.
    /// Clicking it while it is armed puts it away, like the text tool.
    /// </summary>
    private void ToggleImageTool()
    {
        if (_activeViewer?.ActiveTool == AnnotationTool.Image)
        {
            SetActiveTool(AnnotationTool.None);
            return;
        }

        // The button has already checked itself. Put it back until there
        // is actually a picture armed, or a cancelled picker leaves a tool that
        // looks on and is not.
        SyncToolButtons(_activeViewer?.ActiveTool ?? AnnotationTool.None);
        HideToolOptions();
        _ = PickAndArmImageAsync();
    }
    private void EraserToolButton_Click(object sender, RoutedEventArgs e) => ToolButton_Click(AnnotationTool.Eraser);

    // The per-tool options panels live in MainWindow.Tools.cs.

    private static Color HexToColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(255,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    // ---------------------------------------------------------------- commands

    private bool _pickingPdf;
    private MenuFlyout OpenChoices => (MenuFlyout)RootGrid.Resources["OpenChoices"];

    private void NewWindow_Click(object sender, RoutedEventArgs e) => App.CreateReaderWindow();

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pickingPdf) return;
        var anchor = sender as Button ?? (StartPage.Visibility == Visibility.Visible
            ? ShelfOpenButton
            : NewDocumentButton);
        var menu = OpenChoices;
        double width = Math.Max(ShelfOpenButton.MinWidth, ShelfOpenButton.ActualWidth);
        double height = Math.Max(ShelfOpenButton.MinHeight, ShelfOpenButton.ActualHeight);
        // Item width includes its internal padding. Only the presenter border sits outside it.
        foreach (var item in menu.Items)
        {
            item.Width = width - 2;
            // WinUI insets each row's hover surface by 2 DIPs at the top and bottom.
            item.Height = height + 4;
        }
        menu.Placement = anchor == ShelfOpenButton
            ? FlyoutPlacementMode.BottomEdgeAlignedRight
            : FlyoutPlacementMode.BottomEdgeAlignedLeft;
        menu.ShowAt(anchor);
    }

    private async void OpenOriginal_Click(object sender, RoutedEventArgs e) => await PickPdfsAsync(copy: false);
    private async void CopyToLibrary_Click(object sender, RoutedEventArgs e) => await PickPdfsAsync(copy: true);

    private async void CreateBlankPdf_Click(object sender, RoutedEventArgs e)
    {
        OpenChoices.Hide();
        if (_pickingPdf) return;
        _pickingPdf = true;
        try
        {
            var library = _library;
            var ui = UiText.Current;
            var name = new TextBox { Header = ui.DocumentName };
            var destination = new TextBlock
            {
                Text = LibraryFolder,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            };
            var error = new TextBlock
            {
                Style = (Style)Application.Current.Resources["ValidationErrorTextStyle"],
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
            AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            var location = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = ui.LibraryFolderLabel }, destination } };
            var input = new StackPanel { Spacing = 4, Children = { name, error } };
            var content = new StackPanel { Spacing = 16, MinWidth = 280, MaxWidth = 420, Children = { input, location } };
            var dialog = new ContentDialog
            {
                Title = ui.CreateBlankPdf,
                Content = content,
                PrimaryButtonText = ui.Create,
                CloseButtonText = ui.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
            };
            dialog.Opened += (_, _) => name.Focus(FocusState.Programmatic);
            name.TextChanged += (_, _) =>
            {
                bool valid = PdfLibrary.IsValidDocumentName(name.Text);
                dialog.IsPrimaryButtonEnabled = valid;
                error.Text = ui.InvalidDocumentName;
                error.Visibility = !valid && name.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            };

            string? created = null;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                string input = name.Text;
                name.IsReadOnly = true;
                dialog.IsPrimaryButtonEnabled = false;
                try
                {
                    created = await Task.Run(() => library.CreateBlank(input));
                }
                catch (Exception ex)
                {
                    args.Cancel = true;
                    ErrorLog.Default.Write(nameof(CreateBlankPdf_Click), ex);
                    error.Text = ui.BlankPdfCreationFailed(ex.Message);
                    error.Visibility = Visibility.Visible;
                    name.IsReadOnly = false;
                    dialog.IsPrimaryButtonEnabled = true;
                }
                finally { deferral.Complete(); }
            };

            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && created is not null)
            {
                OpenOrActivate(created);
            }
        }
        finally { _pickingPdf = false; }
    }

    private async Task PickPdfsAsync(bool copy)
    {
        OpenChoices.Hide();
        if (_pickingPdf) return;
        _pickingPdf = true;
        try
        {
            var paths = await PdfOpenDialog.PickAsync(WinRT.Interop.WindowNative.GetWindowHandle(this));
            foreach (string path in paths)
            {
                if (copy) await OpenLibraryCopyAsync(path);
                else OpenOrActivate(path);
            }
        }
        catch (Exception ex) { ShowError(UiText.Format("Could not open the file picker: {0}", ex.Message)); }
        finally { _pickingPdf = false; }
    }

    private int _activeImports;
    private async Task OpenLibraryCopyAsync(string path)
    {
        _activeImports++;
        ImportStatus.Visibility = Visibility.Visible;
        try
        {
            string copy = await _library.ImportAsync(path);
            OpenOrActivate(copy);
        }
        catch (Exception ex) { ShowError(UiText.Format("Could not copy {0}: {1}", Path.GetFileName(path), ex.Message)); }
        finally
        {
            if (--_activeImports == 0) ImportStatus.Visibility = Visibility.Collapsed;
        }
    }

    private void EditTools_PointerWheelChanged(object sender, PointerRoutedEventArgs e) =>
        ScrollToolbarWithWheel(EditToolbarScroll, e);

    private void TextFormat_PointerWheelChanged(object sender, PointerRoutedEventArgs e) =>
        ScrollToolbarWithWheel(TextFormatScroll, e);

    private static void ScrollToolbarWithWheel(ScrollViewer scroller, PointerRoutedEventArgs e)
    {
        var wheel = e.GetCurrentPoint(scroller).Properties;
        if (wheel.IsHorizontalMouseWheel || scroller.ScrollableWidth <= 0) return;

        double offset = scroller.HorizontalOffset - wheel.MouseWheelDelta / 120.0 * 48;
        scroller.ChangeView(Math.Clamp(offset, 0, scroller.ScrollableWidth), null, null, disableAnimation: true);
        e.Handled = true;
    }

    private void ReaderSideFlyout_Opened(object sender, object e) => SetReaderSideChevron(sender, true);

    private void ReaderSideFlyout_Closed(object sender, object e) => SetReaderSideChevron(sender, false);

    private void SetReaderSideChevron(object flyout, bool expanded)
    {
        var icon = ReferenceEquals(flyout, ViewActionsFlyout) ? ViewActionsChevron : DocumentActionsChevron;
        icon.Glyph = expanded ? "\uE70E" : "\uE70D";
    }

    private static CornerRadius DockRadius => new(8, 0, 0, 8);

    private void QuickToolsToggle_Click(object sender, RoutedEventArgs e)
    {
        _quickToolsExpanded = !_quickToolsExpanded;
        ExpandedQuickTools.Visibility = _quickToolsExpanded ? Visibility.Visible : Visibility.Collapsed;
        DockedReaderToggle.Visibility = _quickToolsExpanded ? Visibility.Collapsed : Visibility.Visible;
        ReaderQuickTools.Width = _quickToolsExpanded ? ExpandedReaderPanelWidth : CompactReaderPanelWidth;
        ReaderQuickTools.Padding = _quickToolsExpanded ? new Thickness(3, 0, 3, 0) : new Thickness(0);
        ReaderQuickTools.Margin = new Thickness(8, 4, _quickToolsExpanded ? 16 : 0, 16);
        ReaderQuickTools.CornerRadius = _quickToolsExpanded ? (CornerRadius)Application.Current.Resources["ReaderToolbarRadius"] : DockRadius;
        UpdateQuickToolsToggleLabel();
    }

    private void UpdateQuickToolsToggleLabel()
    {
        QuickToolsToggleGlyph.Glyph = _quickToolsExpanded ? "\uE76C" : "\uE76B";
        string label = _quickToolsExpanded ? UiText.Current.CollapseQuickTools : UiText.Current.ExpandQuickTools;
        AutomationProperties.SetName(QuickToolsToggle, label);
        ToolTipService.SetToolTip(QuickToolsToggle, label);
    }

    private void SidebarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_focusReading && CurrentView != null)
        {
            SetFocusReading(false);
            if (CurrentView.IsPaneOpen) return;
        }
        if (CurrentView is { IsDocumentLoaded: true } view)
        {
            view.IsPaneOpen = !view.IsPaneOpen;
            CaptureState(view);
            _store.Save(_state);
            SidebarButton.IsChecked = view.IsPaneOpen;
        }
    }

    private void UpdatePageStepButtons(PdfViewer? viewer)
    {
        PreviousPageButton.IsEnabled = viewer is { CurrentPage: > 0 };
        NextPageButton.IsEnabled = viewer is not null && viewer.CurrentPage < viewer.PageCount - 1;
    }

    private void FocusPageJump()
    {
        if (_focusReading && CurrentView != null) SetFocusReading(false);
        if (!_quickToolsExpanded) QuickToolsToggle_Click(QuickToolsToggle, new RoutedEventArgs());
        if (SettingsPage.Visibility != Visibility.Visible && PageBox.IsEnabled)
            PageBox.Focus(FocusState.Keyboard);
    }

    private void PreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeViewer is { } viewer) viewer.GoToPage(viewer.CurrentPage - 1);
    }

    private void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeViewer is { } viewer) viewer.GoToPage(viewer.CurrentPage + 1);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.ZoomIn();
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.ZoomOut();
    private void RotateButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.RotateClockwise();
    private void RotateLeftButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.RotateCounterClockwise();
    private void PageLayout_Checked(object sender, RoutedEventArgs e)
    {
        int columns = ReferenceEquals(sender, ThreePageButton) ? 3 : ReferenceEquals(sender, TwoPageButton) ? 2 : 1;
        if (Tabs is null || CurrentView is not { IsDocumentLoaded: true } view || view.Viewer.PageColumns == columns) return;
        view.Viewer.SetPageColumns(columns);
        CaptureState(view);
        _store.Save(_state);
        UpdateToolbarForActive();
    }

    private void FitWidthButton_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitWidth);
    private void FitPageButton_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitPage);
    private void ReaderSaveButton_Click(object sender, RoutedEventArgs e) => _ = SaveActiveAsync();
    private void SaveAsButton_Click(object sender, RoutedEventArgs e) => _ = SaveAsActiveAsync();
    private void PropertiesButton_Click(object sender, RoutedEventArgs e) => _ = ShowPropertiesAsync();
    private void FindButton_Click(object sender, RoutedEventArgs e) => ShowFindBar();
    private void PresentButton_Click(object sender, RoutedEventArgs e) => TogglePresentation();
    private void BookmarkButton_Click(object sender, RoutedEventArgs e) => ToggleBookmark();
    private void PaletteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // Closing a submenu restores focus to its anchor. Let that finish before
        // focusing the palette, which dismisses itself when focus leaves it.
        void OpenAfterClose(object? flyout, object args)
        {
            DocumentActionsFlyout.Closed -= OpenAfterClose;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ShowPalette);
        }

        if (DocumentActionsFlyout.IsOpen)
        {
            DocumentActionsFlyout.Closed += OpenAfterClose;
            DocumentActionsFlyout.Hide();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ShowPalette);
        }
    }
    private void UndoButton_Click(object sender, RoutedEventArgs e) => _ = CurrentView?.UndoAsync();
    private void RedoButton_Click(object sender, RoutedEventArgs e) => _ = CurrentView?.RedoAsync();

    private void UpdateUndoButtons()
    {
        var view = CurrentView;
        UndoButton.IsEnabled = view?.CanUndo == true;
        RedoButton.IsEnabled = view?.CanRedo == true;
        ToolTipService.SetToolTip(UndoButton, view?.UndoLabel is { } undo
            ? $"{UiText.Current.Undo}: {UiText.Translate(undo)} (Ctrl+Z)" : $"{UiText.Current.Undo} (Ctrl+Z)");
        ToolTipService.SetToolTip(RedoButton, view?.RedoLabel is { } redo
            ? $"{UiText.Current.Redo}: {UiText.Translate(redo)} (Ctrl+Y)" : $"{UiText.Current.Redo} (Ctrl+Y)");
    }

    // ---------------------------------------------------------------- presentation

    private Window? _presentationWindow;
    private PresentationView? _presentation;

    private void TogglePresentation()
    {
        if (_presentationWindow != null)
        {
            ExitPresentation();
            return;
        }
        if (_activeViewer is not { } viewer || CurrentView is not { IsDocumentLoaded: true, LoadError: null })
            return;

        // Keep the shell HWND in its Windows snap group. Fullscreen belongs to a transient owner-linked HWND.
        var screen = new PresentationView { IsTabStop = true };
        var host = new Window { Title = Title, Content = screen };
        _presentationWindow = host;
        _presentation = screen;
        host.AppWindow.IsShownInSwitchers = false;
        SetWindowLongPtrW(WinRT.Interop.WindowNative.GetWindowHandle(host), -8, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        host.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + area.Width / 2, area.Y + area.Height / 2, 64, 64));
        host.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        screen.ExitRequested += (_, _) => host.Close();
        screen.PreviewKeyDown += (_, args) =>
        {
            switch (args.Key)
            {
                case VirtualKey.Escape:
                case VirtualKey.F5: args.Handled = true; host.Close(); break;
                case VirtualKey.Right:
                case VirtualKey.Down:
                case VirtualKey.Space:
                case VirtualKey.PageDown: args.Handled = true; screen.Next(); break;
                case VirtualKey.Left:
                case VirtualKey.Up:
                case VirtualKey.PageUp: args.Handled = true; screen.Prev(); break;
            }
        };
        host.Closed += (_, _) =>
        {
            _presentationWindow = null;
            _presentation = null;
            int page = screen.CurrentPage;
            screen.EndPresentation();
            if (!_closeApproved && !App.Exiting && ReferenceEquals(_activeViewer, viewer))
            {
                viewer.GoToPage(page);
                Activate();
            }
        };
        host.Activate();
        screen.Show(viewer, _state.Settings.NightMode);
        screen.Focus(FocusState.Programmatic);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    private void ExitPresentation() => _presentationWindow?.Close();

    private void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string factor } &&
            double.TryParse(factor, System.Globalization.CultureInfo.InvariantCulture, out double zoom))
        {
            _activeViewer?.SetZoom(zoom);
        }
    }

    private void SetFitMode(FitMode mode)
    {
        if (_activeViewer is not null)
        {
            _activeViewer.FitMode = mode;
            UpdateFitToggles();
        }
    }

    /// <summary>Keeps the header toggles and the zoom-pill menu items in step.</summary>
    private void UpdateFitToggles()
    {
        FitWidthItem.IsChecked = _activeViewer?.FitMode == FitMode.FitWidth;
        FitPageItem.IsChecked = _activeViewer?.FitMode == FitMode.FitPage;
    }

    private void PageBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_suppressPageBox && _activeViewer is not null && !double.IsNaN(args.NewValue))
        {
            _activeViewer.GoToPage((int)args.NewValue - 1, recordHistory: true);
        }
    }

    private async void Viewer_LinkActivated(object? sender, string uri)
    {
        // Opening a URL leaves the app — confirm the destination first, since
        // a PDF link's target isn't visible before clicking.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != "http" && parsed.Scheme != "https"))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = UiText.Current.OpenLink,
            Content = parsed.ToString(),
            PrimaryButtonText = UiText.Current.OpenInBrowser,
            CloseButtonText = UiText.Current.Cancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            await Launcher.LaunchUriAsync(parsed);
        }
    }

    // ---------------------------------------------------------------- annotations & save

    private void UpdateDirtyIndicator(DocumentView view)
    {
        if (view == CurrentView)
        {
            UpdateReaderSaveButton();
        }
        foreach (var obj in Tabs.TabItems)
        {
            if (obj is TabViewItem { Tag: DocumentView v } tab && v == view)
            {
                tab.Header = view.IsDirty ? $"{view.DisplayName} •" : view.DisplayName;
                return;
            }
        }
    }

    private void UpdateReaderSaveButton() =>
        ReaderSaveButton.IsEnabled = CurrentView is { IsDocumentLoaded: true, LoadError: null, IsDirty: true } view
            && !_savingViews.Contains(view);

    private async void Viewer_NoteRequested(object? sender, (int PageIndex, double X, double Y) at)
    {
        if (CurrentView is not { } view || !ReferenceEquals(sender, view.Viewer))
        {
            return;
        }

        var panel = (StackPanel)((DataTemplate)Application.Current.Resources["NoteEditorContent"]).LoadContent();
        var box = (TextBox)panel.FindName("Editor");
        var add = (Button)panel.FindName("Add");
        ((TextBlock)panel.FindName("PageLabel")).Text = UiText.Format("Page {0}", at.PageIndex + 1);
        var dialog = new ContentDialog { Content = panel };
        AutomationProperties.SetName(dialog, UiText.Current.AddNote);
        bool confirmed = false;
        box.TextChanged += (_, _) => add.IsEnabled = !string.IsNullOrWhiteSpace(box.Text);
        add.Click += (_, _) => { confirmed = true; dialog.Hide(); };
        ((Button)panel.FindName("Cancel")).Click += (_, _) => dialog.Hide();
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        await ShowDialogAsync(dialog);
        if (confirmed && CurrentView == view)
        {
            view.Viewer.AddNote(at.PageIndex, at.X, at.Y, box.Text.Trim());
        }
    }

    /// <summary>Sizes offered for form text. "Auto" is PDF's own 0, meaning fit the box.</summary>
    private static readonly (string Label, double Value)[] FormTextSizes =
    [
        ("Auto", 0), ("8", 8), ("9", 9), ("10", 10), ("11", 11),
        ("12", 12), ("14", 14), ("16", 16), ("18", 18), ("20", 20), ("24", 24),
    ];

    /// <summary>
    /// The colours a filled field can take. Deliberately few: this sets how a
    /// form is filled in, not how it is decorated, and the realistic answers are
    /// black, a pen blue, and red for something that has to stand out.
    /// </summary>
    private static readonly (string Label, byte R, byte G, byte B)[] FormTextColors =
    [
        ("Black", 0, 0, 0),
        ("Blue", 20, 60, 160),
        ("Red", 190, 30, 30),
        ("Green", 20, 110, 60),
    ];

    private async void Viewer_FormAppearanceRequested(object? sender, FormAppearanceRequest request)
    {
        if (CurrentView is not { } view || !ReferenceEquals(sender, view.Viewer))
        {
            return;
        }

        var sizes = new ComboBox { MinWidth = 120 };
        foreach (var (label, _) in FormTextSizes)
        {
            sizes.Items.Add(UiText.Translate(label));
        }
        // Land on the size the field already declares; an unusual one (13.5pt,
        // say) is not in the list, so fall back to Auto rather than silently
        // showing 12 and changing the field the moment OK is pressed.
        int index = Array.FindIndex(FormTextSizes, s => Math.Abs(s.Value - request.Current.FontSize) < 0.01);
        sizes.SelectedIndex = index >= 0 ? index : 0;

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        int chosenColor = Array.FindIndex(FormTextColors,
            c => c.R == request.Current.R && c.G == request.Current.G && c.B == request.Current.B);
        if (chosenColor < 0)
        {
            chosenColor = 0;
        }
        for (int i = 0; i < FormTextColors.Length; i++)
        {
            var (label, r, g, b) = FormTextColors[i];
            int slot = i;
            var button = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = UiText.Translate(label),
                IsChecked = i == chosenColor,
                Tag = slot,
            };
            button.Click += (s, _) =>
            {
                chosenColor = slot;
                foreach (var other in swatches.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>())
                {
                    other.IsChecked = ReferenceEquals(other, s);
                }
            };
            swatches.Children.Add(button);
        }

        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var body = new StackPanel { Spacing = 8, MinWidth = 320 };
        body.Children.Add(new TextBlock { Text = UiText.Current.Size, Style = caption });
        body.Children.Add(sizes);
        body.Children.Add(new TextBlock { Text = UiText.Current.Colour, Style = caption });
        body.Children.Add(swatches);

        var dialog = new ContentDialog
        {
            Title = UiText.Current.TextAppearance,
            Content = body,
            PrimaryButtonText = UiText.Current.Apply,
            CloseButtonText = UiText.Current.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var (_, red, green, blue) = FormTextColors[chosenColor];
        double size = FormTextSizes[Math.Max(0, sizes.SelectedIndex)].Value;

        if (!await view.Viewer.ApplyFieldAppearanceAsync(
                request.PageIndex, request.FieldName, new FieldAppearance(size, red, green, blue)))
        {
            // The field inherits its appearance from the form's default, which
            // carries no font resource name this code can safely reuse. Saying so
            // beats leaving the user to wonder why Apply did nothing.
            ShowError(UiText.Translate("This field takes its text style from the document, so it cannot be changed here."));
        }
    }

    private async Task SaveActiveAsync()
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view
            || (!view.IsDirty && !view.Viewer.IsEditingText)
            || !_savingViews.Add(view))
        {
            return;
        }
        UpdateReaderSaveButton();
        try
        {
            await view.SaveInPlaceAsync();
        }
        catch (Exception ex)
        {
            ErrorLog.Default.Write(nameof(SaveActiveAsync), ex);
            ShowError(UiText.Format("Save failed: {0}", UiText.Translate(ex.Message)));
        }
        finally
        {
            _savingViews.Remove(view);
            UpdateDirtyIndicator(view);
        }
    }

    private async Task SaveAsActiveAsync()
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view || view.Viewer.Document is not { } document)
        {
            return;
        }

        var picked = await FilePickerHost.PickSaveAsync(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            Path.GetFileNameWithoutExtension(view.FilePath),
            UiText.Translate("PDF document"),
            ".pdf");
        if (picked.Failed)
        {
            ShowError(FilePickerHost.FailureMessage);
            return;
        }
        if (picked.File is not { } file)
        {
            return;
        }

        if (string.Equals(file.Path, view.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            await SaveActiveAsync(); // picked the same file: in-place save
            return;
        }

        try
        {
            await view.Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, () =>
            {
                document.SaveAs(file.Path);
                return true;
            });
            OpenOrActivate(file.Path);
        }
        catch (Exception ex)
        {
            ShowError(UiText.Format("Save As failed: {0}", ex.Message));
        }
    }

    /// <summary>
    /// Palette route into extract. The palette entry is offered whenever a
    /// document is open, so it has to say what to do when no pages are picked
    /// rather than appearing to do nothing.
    /// </summary>
    private async Task ExtractSelectedPagesFromPaletteAsync()
    {
        if (CurrentView is not { IsDocumentLoaded: true } view)
        {
            return;
        }
        if (view.SelectedPageCount == 0)
        {
            ShowNotice(
                UiText.Translate("Select pages in the thumbnail sidebar first, then extract them."),
                InfoBarSeverity.Informational);
            return;
        }
        await ExtractPagesAsync(view);
    }

    /// <summary>
    /// Writes the thumbnail selection out as a new PDF. The picker lives here
    /// because it needs the window handle; the work itself is DocumentView's.
    /// The current document is left alone — extract is a copy, not a cut.
    /// </summary>
    private async Task ExtractPagesAsync(DocumentView view)
    {
        if (view.SelectedPageCount == 0 || !view.IsDocumentLoaded)
        {
            return;
        }

        var picked = await FilePickerHost.PickSaveAsync(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            $"{Path.GetFileNameWithoutExtension(view.FilePath)} {view.SelectedPageRangeLabel()}",
            UiText.Translate("PDF document"),
            ".pdf");
        if (picked.Failed)
        {
            ShowError(FilePickerHost.FailureMessage);
            return;
        }
        if (picked.File is not { } file)
        {
            return;
        }

        // Writing over the document being read from would pull the file out from
        // under the open handle mid-export.
        if (string.Equals(file.Path, view.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            ShowError(UiText.Translate("Pick a different file: extracting over the open document would overwrite it."));
            return;
        }

        int count = view.SelectedPageCount;
        if (await view.ExtractSelectedPagesAsync(file.Path))
        {
            // Not ShowError: this succeeded, so it gets success semantics.
            ShowNotice(
                UiText.Format(count == 1 ? "Extracted {0} page to {1}." : "Extracted {0} pages to {1}.", count, file.Name),
                InfoBarSeverity.Success);
        }
    }

    // ---------------------------------------------------------------- night / print / properties / settings

    private void NightButton_Click(object sender, RoutedEventArgs e) => ToggleNightMode();

    private void ToggleNightMode()
    {
        _state.Settings.NightMode = !_state.Settings.NightMode;
        NightItem.IsChecked = _state.Settings.NightMode;
        foreach (var view in AllDocumentViews())
        {
            view.Viewer.NightMode = _state.Settings.NightMode;
        }
        _store.Save(_state);
    }

    private IEnumerable<DocumentView> AllDocumentViews() =>
        Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Tag).OfType<DocumentView>();

    private void PrintButton_Click(object sender, RoutedEventArgs e) => _ = PrintAsync();

    private async Task PrintAsync()
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view || view.Viewer.Document is not { } document)
        {
            return;
        }
        if (!PrintService.IsSupported)
        {
            ShowError(UiText.Translate("Printing is not supported on this device."));
            return;
        }

        try
        {
            _printService ??= new PrintService(WinRT.Interop.WindowNative.GetWindowHandle(this));
            await _printService.ShowAsync(document, $"{view.DisplayName} — PDF Desk", view.Viewer.WorkQueue);
        }
        catch (Exception ex)
        {
            ShowError(UiText.Format("Printing failed: {0}", ex.Message));
        }
    }

    private void CopyPdfFileItem_Click(object sender, RoutedEventArgs e) => _ = CopyPdfFileAsync();

    private async Task OpenFileLocationAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
            var options = new FolderLauncherOptions();
            options.ItemsToSelect.Add(file);
            if (!await Launcher.LaunchFolderAsync(folder, options))
                ShowError(UiText.Translate("File Explorer could not open this folder."));
        }
        catch (Exception ex)
        {
            ShowError(UiText.Format("Could not open the file location: {0}", ex.Message));
        }
    }

    private void CopyFilePathItem_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is { } view) _ = CopyFilePathAsync(view.FilePath);
    }

    private async Task CopyFilePathAsync(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            await ClipboardText.WriteAsync(WinRT.Interop.WindowNative.GetWindowHandle(this), fullPath);
            ShowNotice(fullPath, InfoBarSeverity.Success, UiText.Current.FilePathCopied);
        }
        catch (Exception ex)
        {
            ErrorLog.Default.Write(nameof(CopyFilePathAsync), ex);
            ShowError(UiText.Format("Could not copy the file path: {0}", ex.GetBaseException().Message));
        }
    }

    private async Task CopyPdfFileAsync()
    {
        if (_copyingPdfFile || CurrentView is not { IsDocumentLoaded: true, LoadError: null } view
            || view.Viewer.Document is not { } document)
        {
            return;
        }

        _copyingPdfFile = true;
        CopyPdfFileItem.IsEnabled = false;
        try
        {
            view.Viewer.KillFormFocus();
            await PdfClipboard.CopyAsync(
                view.FilePath, view.IsDirty ? document : null, view.Viewer.WorkQueue);
            ShowNotice(UiText.Current.PdfFileCopiedMessage, InfoBarSeverity.Success, UiText.Current.PdfFileCopied);
        }
        catch (Exception ex)
        {
            ErrorLog.Default.Write(nameof(CopyPdfFileAsync), ex);
            ShowError(UiText.Format("Could not copy the PDF file: {0}", ex.Message));
        }
        finally
        {
            _copyingPdfFile = false;
            CopyPdfFileItem.IsEnabled = CurrentView is { IsDocumentLoaded: true, LoadError: null };
        }
    }

    /// <summary>How many pages the font scan walks before it calls it a day.</summary>
    private const int FontScanPageCap = 50;

    /// <summary>
    /// Ctrl+D. Everything the file says about itself, in sections.
    ///
    /// The fonts arrive after the dialog does. Everything else is a dictionary
    /// lookup, but a font list means walking page objects, and making Ctrl+D sit
    /// there for a second on a long document to answer a question most people
    /// are not asking would be the wrong trade.
    /// </summary>
    private async Task ShowPropertiesAsync()
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view || view.Viewer.Document is not { } document)
        {
            return;
        }

        int page = view.Viewer.CurrentPage;
        var sections = await view.Viewer.RunOnRenderThreadAsync(
            PdfWorkPriority.Interactive, () => document.GetDocumentProperties(page));

        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(0, 0, 16, 2) };
        foreach (var section in sections)
        {
            panel.Children.Add(BuildPropertySection(section.Title, section.Rows));
        }

        var fonts = new StackPanel { Spacing = 5 };
        panel.Children.Add(BuildPropertySection("Fonts", [("", UiText.Translate("Looking…"))], fonts));

        var heading = (Grid)((DataTemplate)Application.Current.Resources["DocumentPropertiesHeading"]).LoadContent();
        ((TextBlock)heading.FindName("FileName")).Text = view.DisplayName;
        var dialog = new ContentDialog
        {
            Title = heading,
            Content = new ScrollViewer
            {
                Width = 428,
                MaxHeight = Math.Clamp(RootGrid.ActualHeight - 218, 136, 396),
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel,
            },
            CloseButtonText = UiText.Current.Close,
            CloseButtonStyle = (Style)Application.Current.Resources["DocumentPropertiesCloseStyle"],
            SecondaryButtonText = view.HasSignatures ? UiText.Current.SignatureDetails : "",
        };
        AutomationProperties.SetName(dialog, UiText.Current.DocumentProperties);

        _ = FillFontsAsync(view, document, fonts);
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Secondary)
            await ShowSignatureDetailsAsync(view);
    }

    /// <summary>One headed group of rows, in the same shape the report dialog uses.</summary>
    private static StackPanel BuildPropertySection(
        string title, IReadOnlyList<(string Name, string Value)> rows, StackPanel? body = null)
    {
        var section = new StackPanel { Spacing = 7 };
        var heading = (Grid)((DataTemplate)Application.Current.Resources["DocumentPropertiesSectionHeading"]).LoadContent();
        ((TextBlock)heading.FindName("Label")).Text = UiText.Translate(title);
        section.Children.Add(heading);

        body ??= new StackPanel { Spacing = 5 };
        foreach (var (name, value) in rows)
        {
            body.Children.Add(PropertyRow(UiText.PropertyLabel(name), UiText.PropertyValue(title, name, value)));
        }
        section.Children.Add(body);
        return section;
    }

    private static Grid PropertyRow(string name, string value, bool isFont = false)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(isFont ? 210 : 100) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = name,
            FontSize = 13,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (name.Length > 0) ToolTipService.SetToolTip(label, name);
        row.Children.Add(label);

        // Selectable throughout: people open this dialog to copy a title, a
        // producer string or a path out of it.
        var text = new TextBlock
        {
            Text = value,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        if (name.Length == 0) Grid.SetColumnSpan(text, 2);
        else Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    /// <summary>
    /// Walks the pages for fonts at Background priority, so visible tiles keep
    /// outranking it, and replaces the placeholder when it lands.
    /// </summary>
    private async Task FillFontsAsync(DocumentView view, PdfDocument document, StackPanel body)
    {
        IReadOnlyList<FontUsage> fonts;
        try
        {
            fonts = await view.Viewer.RunOnRenderThreadAsync(
                PdfWorkPriority.Background, () => document.GetFontsUsed(FontScanPageCap));
        }
        catch
        {
            // Closed, swapped, or the scan threw. The dialog is still useful.
            body.Children.Clear();
            body.Children.Add(PropertyRow("", UiText.Translate("Could not be read.")));
            return;
        }

        body.Children.Clear();
        if (fonts.Count == 0)
        {
            body.Children.Add(PropertyRow("", UiText.Translate("This document draws no text.")));
            return;
        }

        foreach (var font in fonts)
        {
            string embedded = UiText.Translate(font.Embedded ? "Embedded" : "Not embedded");
            string kind = font.Kind == "—" ? "" : $", {UiText.TranslateList(font.Kind)}";
            body.Children.Add(PropertyRow(font.Name, embedded + kind, isFont: true));
        }

        if (document.PageCount > FontScanPageCap)
        {
            body.Children.Add(PropertyRow("", UiText.Format("Found in the first {0} pages.", FontScanPageCap)));
        }
    }

    private void ReportProblemMenuItem_Click(object sender, RoutedEventArgs e) => _ = ShowReportProblemAsync();

    /// <summary>
    /// PDFDesk has no telemetry and makes no network requests, by design. That
    /// means a crash reaches nobody unless the user carries it out by hand, and
    /// until now nothing in the app said where the log even was. This shows the
    /// details a useful report needs and opens the two places to get them.
    /// </summary>
    private async Task ShowReportProblemAsync()
    {
        string logPath = ErrorLog.Default.Path_;
        bool haveLog = File.Exists(logPath);

        var panel = (StackPanel)((DataTemplate)Application.Current.Resources["ReportProblemContent"]).LoadContent();
        var details = (StackPanel)panel.FindName("Details");
        var assembly = typeof(MainWindow).Assembly;
        bool development = !IsPackaged && assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration != "Release";
        string distribution = UiText.Translate(IsPackaged ? "MSIX package" : development ? "Development build" : "portable");
        var rows = new List<(string Label, string Value)> { ("PDF Desk", $"{CurrentVersion} · {distribution}") };
        if (development)
            rows.Add((UiText.Translate("Build ID"), assembly.ManifestModule.ModuleVersionId.ToString("N")[..12]));
        rows.Add(("Windows", Environment.OSVersion.Version.ToString()));
        rows.Add((UiText.Translate("App / system"), $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} / {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}"));
        foreach (var (label, value) in rows)
            details.Children.Add(PropertyRow(label, value));

        var copy = (Button)panel.FindName("CopyDetails");
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new DataPackage();
                data.SetText(string.Join(Environment.NewLine, rows.Select(r => $"{r.Label}: {r.Value}")));
                Clipboard.SetContent(data);
                copy.Content = UiText.Translate("Copied");
            }
            catch (Exception ex)
            {
                ErrorLog.Default.Write(nameof(ShowReportProblemAsync), ex);
                copy.Content = UiText.Translate("Retry copy");
            }
        };
        var log = (Button)panel.FindName("LogFolder");
        log.Visibility = haveLog ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)panel.FindName("LogInfo")).Text = haveLog
            ? UiText.Translate("Attach errors.log when reporting a crash.")
            : UiText.Translate("No error log yet.");
        if (haveLog) ToolTipService.SetToolTip(log, logPath);
        log.Click += async (_, _) => await Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(logPath));
        var dialog = new ContentDialog
        {
            Title = UiText.Current.ReportAProblemAction,
            Content = panel,
            PrimaryButtonText = UiText.Current.OpenIssueTracker,
            CloseButtonText = UiText.Current.Close,
        };
        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            await Launcher.LaunchUriAsync(new Uri("https://github.com/DavidLabWorks/pdf-desk-windows/issues/new"));
        }

    }

    private object? _settingsReturnTab;
    private bool _syncingPreferences;

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPage.Visibility == Visibility.Visible) return;
        _settingsReturnTab = Tabs.SelectedItem;
        Tabs.SelectedItem = null;
        Workspace.Visibility = Visibility.Collapsed;
        HomeSelection.Visibility = Visibility.Collapsed;
        HomeButton.IsTabStop = true;
        _syncingPreferences = true;
        var settings = _state.Settings;
        ThemeChoice.SelectedIndex = settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        LanguageChoice.SelectedIndex = settings.Language switch { "en" => 1, "zh-CN" => 2, _ => 0 };
        RestoreChoice.IsOn = settings.RestoreSession;
        CloseBehaviorChoice.SelectedIndex = settings.CloseToTray ? 0 : 1;
        RefreshLibraryLocation();
        _syncingPreferences = false;
        SettingsPage.Visibility = Visibility.Visible;
        RefreshDefaultPdfStatus();
        SettingsBack.Focus(FocusState.Programmatic);
    }

    private void LeaveSettings()
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        Workspace.Visibility = Visibility.Visible;
        _settingsReturnTab = null;
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs e)
    {
        var tab = _settingsReturnTab;
        LeaveSettings();
        if (tab != null && Tabs.TabItems.Contains(tab)) Tabs.SelectedItem = tab;
        else UpdateStartPageVisibility();
        GlobalSettingsButton.Focus(FocusState.Programmatic);
    }

    private void PreferenceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPreferences || SettingsPage?.Visibility != Visibility.Visible) return;
        if (ReferenceEquals(sender, ThemeChoice))
        {
            _state.Settings.Theme = ThemeChoice.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
            foreach (var window in App.Readers) window.ApplyTheme(_state.Settings.Theme);
        }
        else
        {
            _state.Settings.Language = LanguageChoice.SelectedIndex switch { 1 => "en", 2 => "zh-CN", _ => "System" };
            foreach (var window in App.Readers)
            {
                window.ApplyLanguage();
                window.UpdateCaptionState();
                window.PopulateRecents();
            }
        }
        _store.Save(_state);
    }

    private void RefreshLibraryLocation()
    {
        LibraryPath.Text = LibraryFolder;
        ResetLibraryFolder.Visibility = _state.Settings.LibraryFolder == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetLibraryFolder(string? folder)
    {
        _state.Settings.LibraryFolder = folder;
        foreach (var window in App.Readers) window._library = new PdfLibrary(LibraryFolder);
        _store.Save(_state);
        RefreshLibraryLocation();
    }

    private async void ChangeLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
            // Check write access before making this the destination for future imports.
            using (new FileStream(Path.Combine(path, $".{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            SetLibraryFolder(string.Equals(path, _store.LibraryDirectory, StringComparison.OrdinalIgnoreCase) ? null : path);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(new ContentDialog
            {
                Title = UiText.Current.LibraryFolderError,
                Content = ex.Message,
                CloseButtonText = UiText.Current.Close,
            });
        }
    }

    private void ResetLibraryFolder_Click(object sender, RoutedEventArgs e) => SetLibraryFolder(null);

    private void RestoreSession_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingPreferences || SettingsPage?.Visibility != Visibility.Visible) return;
        _state.Settings.RestoreSession = RestoreChoice.IsOn;
        _store.Save(_state);
    }

    private void CloseBehavior_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPreferences || SettingsPage?.Visibility != Visibility.Visible) return;
        _state.Settings.CloseToTray = CloseBehaviorChoice.SelectedIndex == 0;
        _store.Save(_state);
    }

    private void ApplyLanguage()
    {
        string previousLanguage = RootGrid.Language;
        UiText.Current.SetLanguage(_state.Settings.Language);
        RootGrid.Language = UiText.Current.LanguageTag;
        if (previousLanguage != UiText.Current.LanguageTag)
        {
            foreach (var view in AllDocumentViews()) view.RefreshBookmarkLabels();
            HideToolOptions();
            _toolPanels.Clear();
            _signatureList = null;
        }
        UpdateQuickToolsToggleLabel();
        UpdateFocusReadingControls();
    }

    private static string CurrentVersion
    {
        get
        {
            if (IsPackaged)
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return v.Revision == 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            var assembly = typeof(MainWindow).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                ?? assembly.GetName().Version!.ToString(3);
        }
    }

    /// <summary>
    /// True for the MSIX (Store) build, false for the portable zip. Worth
    /// reporting in a bug: the two differ in how they are installed and
    /// updated, and only one of them is signed.
    /// <c>Package.Current</c> throws when there is no package identity, which
    /// is the documented way to ask.
    /// </summary>
    private static bool IsPackaged
    {
        get
        {
            try
            {
                return Windows.ApplicationModel.Package.Current is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    // ---------------------------------------------------------------- shortcuts overlay

    private void ShortcutsMenuItem_Click(object sender, RoutedEventArgs e) => _ = ShowShortcutsAsync();

    private async Task ShowShortcutsAsync()
    {
        var categories = new ComboBox { MinWidth = 164 };
        AutomationProperties.SetName(categories, UiText.Translate("Shortcut category"));
        foreach (var group in ShortcutCatalog.Groups)
            categories.Items.Add(UiText.Translate(group.Title));

        var rows = new StackPanel { Spacing = 4, Padding = new Thickness(0, 0, 14, 0) };
        var scroll = new ScrollViewer
        {
            Height = Math.Clamp(RootGrid.ActualHeight - 250, 140, 310),
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = rows,
        };
        var keyStyle = (Style)Application.Current.Resources["ShortcutKeyChipStyle"];
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        categories.SelectionChanged += (_, _) =>
        {
            if (categories.SelectedIndex < 0) return;
            rows.Children.Clear();
            foreach (var shortcut in ShortcutCatalog.Groups[categories.SelectedIndex].Shortcuts)
            {
                var row = new Grid { ColumnSpacing = 12, MinHeight = 31 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock
                {
                    Text = UiText.Translate(shortcut.Name), TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var keys = new Border
                {
                    Style = keyStyle, VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = UiText.Translate(shortcut.Keys), Style = caption },
                };
                Grid.SetColumn(keys, 1);
                row.Children.Add(keys);
                rows.Children.Add(row);
            }
            scroll.ChangeView(null, 0, null, true);
        };
        categories.SelectedIndex = 0;
        var content = new StackPanel { Width = 446, Spacing = 12 };
        content.Children.Add(categories);
        content.Children.Add(new Border { Style = (Style)Application.Current.Resources["FlyoutSeparatorStyle"] });
        content.Children.Add(scroll);
        await ShowDialogAsync(new ContentDialog
        {
            Title = UiText.Current.KeyboardShortcuts,
            Content = content,
            CloseButtonText = UiText.Current.Close,
            CloseButtonStyle = (Style)Application.Current.Resources["DocumentPropertiesCloseStyle"],
        });
    }

    // ---------------------------------------------------------------- command palette

    private void ShowPalette()
    {
        var commands = new List<PaletteCommand>
        {
            new(UiText.Current.OpenOrCreatePdf, "Ctrl+O", () => OpenButton_Click(this, null!)),
            new(UiText.Current.KeyboardShortcuts, "F1", () => _ = ShowShortcutsAsync()),
            new(UiText.Current.Settings, "", () => SettingsButton_Click(this, null!)),
        };

        if (_activeViewer is { } viewer && CurrentView is { IsDocumentLoaded: true, LoadError: null })
        {
            commands.AddRange(
            [
                new(UiText.Current.FindInDocument, "Ctrl+F", ShowFindBar),
                new(UiText.Current.HighlightSelection, "Ctrl+H", () => viewer.MarkupSelection(MarkupKind.Highlight)),
                new(UiText.Current.DrawTogglePen, "Ctrl+E", TogglePenTool),
                new(UiText.Current.HighlighterTool, "", () => SetActiveTool(AnnotationTool.Highlighter)),
                new(UiText.Current.TypeOnThePage, "Ctrl+T", () => SetActiveTool(AnnotationTool.Text)),
                new(UiText.Current.PlaceAPicture, "", () => _ = PickAndArmImageAsync()),
                new(UiText.Current.EraserTool, "", () => SetActiveTool(AnnotationTool.Eraser)),
                new(UiText.Current.Save, "Ctrl+S", () => _ = SaveActiveAsync()),
                new(UiText.Current.SaveAsAction, "Ctrl+Shift+S", () => _ = SaveAsActiveAsync()),
                new(UiText.Current.ExtractSelectedPagesToANewFile, "", () => _ = ExtractSelectedPagesFromPaletteAsync()),
                new(UiText.Current.PrintAction, "Ctrl+P", () => _ = PrintAsync()),
                new(UiText.Current.CopyPdfFile, "", () => _ = CopyPdfFileAsync()),
                new(UiText.Current.DocumentProperties, "Ctrl+D", () => _ = ShowPropertiesAsync()),
                new(UiText.Current.ToggleNightMode, "Ctrl+I", ToggleNightMode),
                new(UiText.Current.ToggleSidebar, "F9", () => SidebarButton_Click(this, null!)),
                new(UiText.Current.PresentationMode, "F5", TogglePresentation),
                new(UiText.Current.BookmarkThisPage, "Ctrl+B", ToggleBookmark),
                new(UiText.Current.Undo, "Ctrl+Z", () => _ = CurrentView?.UndoAsync()),
                new(UiText.Current.Redo, "Ctrl+Y", () => _ = CurrentView?.RedoAsync()),
                new(UiText.Current.NextPage, "", () => viewer.GoToPage(viewer.CurrentPage + 1)),
                new(UiText.Current.PreviousPage, "", () => viewer.GoToPage(viewer.CurrentPage - 1)),
                new(UiText.Current.FirstPage, "Home", () => viewer.GoToPage(0, recordHistory: true)),
                new(UiText.Current.LastPage, "End", () => viewer.GoToPage(viewer.PageCount - 1, recordHistory: true)),
                new(UiText.Current.ZoomIn, "Ctrl++", viewer.ZoomIn),
                new(UiText.Current.ZoomOut, "Ctrl+-", viewer.ZoomOut),
                new(UiText.Current.ActualSize, "Ctrl+1", () => viewer.SetZoom(1.0)),
                new(UiText.Current.FitWidth, "Ctrl+2", () => SetFitMode(FitMode.FitWidth)),
                new(UiText.Current.FitPage, "Ctrl+0", () => SetFitMode(FitMode.FitPage)),
                new(UiText.Current.RotateClockwise, "Ctrl+R", viewer.RotateClockwise),
                new(UiText.Current.CloseTab, "Ctrl+W", CloseCurrentTab),
            ]);
        }

        foreach (var recent in _state.Recents.Take(8))
        {
            string path = recent.Path;
            commands.Add(new PaletteCommand(UiText.Format("Open recent: {0}", recent.DisplayName), "", () => OpenOrActivate(path)));
        }

        Palette.Show(commands, pageNumber =>
            _activeViewer is { PageCount: > 0 } v && pageNumber >= 1 && pageNumber <= v.PageCount
                ? new PaletteCommand(UiText.Format("Go to page {0}", pageNumber), "", () => v.GoToPage(pageNumber - 1, recordHistory: true))
                : null);
    }

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

        label.Text = FitDocumentName(name, Fits);
    }

    private static string FitDocumentName(string name, Func<string, bool> fits)
    {
        if (fits(name)) return name;

        // Slice at grapheme boundaries so an ellipsis cannot split a visible character.
        int[] starts = System.Globalization.StringInfo.ParseCombiningCharacters(name);
        int low = 1, high = starts.Length - 1;
        string best = "…";
        while (low <= high)
        {
            int keep = low + (high - low) / 2;
            int suffix = keep / 2;
            string tail = suffix == 0 ? "" : name[starts[^suffix]..];
            string candidate = name[..starts[keep - suffix]] + "…" + tail;
            if (fits(candidate))
            {
                best = candidate;
                low = keep + 1;
            }
            else
            {
                high = keep - 1;
            }
        }
        return best;
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

    // ---------------------------------------------------------------- accelerators

    private void RegisterAccelerators()
    {
        AddAccelerator(VirtualKey.Add, VirtualKeyModifiers.Control, () => _activeViewer?.ZoomIn());
        AddAccelerator((VirtualKey)0xBB, VirtualKeyModifiers.Control, () => _activeViewer?.ZoomIn());
        AddAccelerator(VirtualKey.Subtract, VirtualKeyModifiers.Control, () => _activeViewer?.ZoomOut());
        AddAccelerator((VirtualKey)0xBD, VirtualKeyModifiers.Control, () => _activeViewer?.ZoomOut());
        AddAccelerator(VirtualKey.Number0, VirtualKeyModifiers.Control, () => SetFitMode(FitMode.FitPage));
        AddAccelerator(VirtualKey.Number1, VirtualKeyModifiers.Control, () => _activeViewer?.SetZoom(1.0));
        AddAccelerator(VirtualKey.Number2, VirtualKeyModifiers.Control, () => SetFitMode(FitMode.FitWidth));
        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _activeViewer?.GoBack());
        AddAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu, () => _activeViewer?.GoForward());
        AddAccelerator(VirtualKey.W, VirtualKeyModifiers.Control, CloseCurrentTab);
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, ShowFindBar);
        AddAccelerator(VirtualKey.G, VirtualKeyModifiers.Control, FocusPageJump);
        AddAccelerator(VirtualKey.F3, VirtualKeyModifiers.None, () => StepHit(+1));
        AddAccelerator(VirtualKey.F3, VirtualKeyModifiers.Shift, () => StepHit(-1));
        AddAccelerator(VirtualKey.P, VirtualKeyModifiers.Control, () => _ = PrintAsync());
        AddAccelerator(VirtualKey.D, VirtualKeyModifiers.Control, () => _ = ShowPropertiesAsync());
        AddAccelerator(VirtualKey.H, VirtualKeyModifiers.Control, () =>
        {
            if (_activeViewer is not { } viewer) return;
            if (viewer.HasSelection) viewer.MarkupSelection(MarkupKind.Highlight);
            else ToolButton_Click(AnnotationTool.Highlighter);
        });
        AddAccelerator(VirtualKey.T, VirtualKeyModifiers.Control,
            () => TextToolButton_Click(this, null!), skipWhenTextInputFocused: true);
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, () => _ = SaveActiveAsync());
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = SaveAsActiveAsync());

        // Moved off the old CommandBar buttons when the header was slimmed down.
        AddAccelerator(VirtualKey.F9, VirtualKeyModifiers.None, () => SidebarButton_Click(this, null!));
        AddAccelerator(VirtualKey.R, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => _activeViewer?.RotateCounterClockwise());
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control, () => _ = CurrentView?.UndoAsync());
        AddAccelerator(VirtualKey.Y, VirtualKeyModifiers.Control, () => _ = CurrentView?.RedoAsync());

        AddTextFormattingAccelerators();

        // Available even with no document open.
        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, () => OpenButton_Click(this, null!), requiresDocument: false);
        AddAccelerator(VirtualKey.K, VirtualKeyModifiers.Control, ShowPalette, requiresDocument: false);
        AddAccelerator(VirtualKey.F1, VirtualKeyModifiers.None, () => _ = ShowShortcutsAsync(), requiresDocument: false);
        // Ctrl+? — GNOME's other shortcuts-window binding (Shift+/ = ? on US layouts).
        AddAccelerator((VirtualKey)0xBF, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => _ = ShowShortcutsAsync(), requiresDocument: false);
        AddAccelerator(VirtualKey.N, VirtualKeyModifiers.Control, () => App.CreateReaderWindow(), requiresDocument: false);
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, TogglePresentation);
        // Escape peels off one layer at a time, outermost first.
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, () =>
        {
            if (_presentationWindow != null)
            {
                ExitPresentation();
            }
            else if (Palette.IsOpen)
            {
                Palette.Hide();
            }
            else if (IsToolOptionsOpen)
            {
                HideToolOptions(); // close the panel but keep the tool armed
            }
            else if (FindBar.Visibility == Visibility.Visible)
            {
                HideFindBar();
            }
            else if (_focusReading && CurrentView != null)
            {
                SetFocusReading(false);
            }
            else if (_activeViewer?.CancelStampPlacement() == true)
            {
                // Abandon a half-drawn placement before disarming the tool.
            }
            else if (_activeViewer?.ClearObjectSelection() == true)
            {
                // Deselect a placed object before disarming anything.
            }
            else if (_activeViewer?.ActiveTool is not (null or AnnotationTool.None))
            {
                _activeViewer?.ClearPendingStamp();
                SetActiveTool(AnnotationTool.None); // finally, put the tool away
            }
        }, requiresDocument: false);

        // Ctrl+C/X/V must fall through to focused text boxes (find box, page box).
        AddAccelerator(VirtualKey.C, VirtualKeyModifiers.Control, CopySelection, skipWhenTextInputFocused: true);
        AddAccelerator(VirtualKey.X, VirtualKeyModifiers.Control,
            () => CurrentView?.TryCopyPages(cut: true), skipWhenTextInputFocused: true);
        AddAccelerator(VirtualKey.V, VirtualKeyModifiers.Control,
            () => CurrentView?.TryPastePages(), skipWhenTextInputFocused: true);
    }

    /// <summary>
    /// Registers one chord, with up to three meanings depending on where the
    /// caret is.
    /// </summary>
    /// <param name="action">
    /// What the chord does to the document. Null for a chord that only ever
    /// means something to a caret, like Ctrl+U.
    /// </param>
    /// <param name="textBoxAction">
    /// What it does while a text box is open. Ctrl+B has to bold the words
    /// rather than bookmark the page, and Ctrl+R align them right rather than
    /// rotate the document.
    /// </param>
    /// <param name="formFieldAction">
    /// What it does while a PDF form field has focus. Same reasoning: filling in
    /// a form and reaching for Ctrl+B must not flip night mode.
    /// </param>
    private void AddAccelerator(
        VirtualKey key, VirtualKeyModifiers modifiers, Action? action,
        bool requiresDocument = true, bool skipWhenTextInputFocused = false,
        Action? textBoxAction = null, Action? formFieldAction = null)
    {
        // A chord that means something to a caret must never fall through to the
        // document when a caret has it — including the find box's, which owns no
        // formatting but must not lose the keystroke either.
        bool caretAware = skipWhenTextInputFocused || textBoxAction is not null || formFieldAction is not null;

        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            if (textBoxAction is not null && _activeViewer?.IsEditingText == true)
            {
                textBoxAction();
                args.Handled = true;
                return;
            }
            if (formFieldAction is not null && _activeViewer?.IsFormFieldFocused == true)
            {
                formFieldAction();
                args.Handled = true;
                return;
            }
            if (caretAware && IsTextInputFocused())
            {
                return; // leave args.Handled false so the text box gets the key
            }
            if (action is not null && (!requiresDocument || _activeViewer is not null))
            {
                action();
                args.Handled = true;
            }
        };
        ((UIElement)Content).KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>
    /// True when keystrokes belong to something that is taking text, so the
    /// document's own navigation must stand down.
    ///
    /// A focused PDF form field counts: it is a text input that happens to be
    /// drawn by PDFium rather than by XAML, so without this arrows would scroll
    /// the page instead of moving the caret, and Backspace would do nothing.
    /// Every keyboard path (accelerators and the tunneling navigation
    /// handler) gates on this one predicate.
    /// </summary>
    private bool IsTextInputFocused() =>
        FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or NumberBox or AutoSuggestBox or PasswordBox
        || _activeViewer?.IsFormFieldFocused == true;

    private void ClearPageObjectSelectionForInput(object source)
    {
        if (_activeViewer?.HasSelectedObject != true) return;

        for (var target = source as DependencyObject; target is UIElement;
             target = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(target))
        {
            if (target is TextBox or NumberBox or AutoSuggestBox or PasswordBox or SelectorItem or TreeViewItem)
            {
                _activeViewer.ClearObjectSelection();
                return;
            }
        }
    }

    // ---------------------------------------------------------------- keyboard navigation

    private void Content_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || _activeViewer is null || Palette.IsOpen || IsTextInputFocused())
        {
            return;
        }

        bool shift = IsKeyDown(VirtualKey.Shift);
        if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu))
        {
            return; // modified combos belong to the KeyboardAccelerators
        }

        // Space pages here in the BUBBLING phase (not PreviewKeyDown) so a
        // focused button keeps its Space-to-activate accessibility behavior.
        if (e.Key == VirtualKey.Space)
        {
            _activeViewer.ScrollByViewport(shift ? -0.9 : +0.9);
            e.Handled = true;
            return;
        }

    }

    /// <summary>
    /// Standard navigation — always on, matching Evince/GNOME Papers: arrows
    /// scroll/page, PageUp/Down step viewports, Home/End jump. Tunneling so
    /// the tab strip and toolbar can't swallow the keys; text inputs and the
    /// sidebar's own lists are explicitly excluded.
    /// </summary>
    private void Content_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _dragCard != null)
        {
            EndShelfDrag();
            e.Handled = true;
            return;
        }
        if (e.Handled)
        {
            return;
        }

        // Presentation mode owns the keyboard while active (Esc/F5 exit via
        // their accelerators).
        if (_presentationWindow != null)
        {
            switch (e.Key)
            {
                case VirtualKey.Right:
                case VirtualKey.Down:
                case VirtualKey.Space:
                case VirtualKey.PageDown:
                    _presentation?.Next();
                    e.Handled = true;
                    break;
                case VirtualKey.Left:
                case VirtualKey.Up:
                case VirtualKey.PageUp:
                    _presentation?.Prev();
                    e.Handled = true;
                    break;
            }
            return;
        }

        if (_activeViewer is null || Palette.IsOpen)
        {
            return;
        }
        if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu))
        {
            return;
        }
        // Selecting an object gives the page the deletion keys, even if XAML retained
        // focus in the previous control. Entering an input clears the selection.
        if (e.Key is VirtualKey.Delete or VirtualKey.Back && !IsKeyDown(VirtualKey.Shift)
            && !_activeViewer.IsEditingText && !_activeViewer.IsFormFieldFocused
            && _activeViewer.TryDeleteSelectedObject())
        {
            e.Handled = true;
            return;
        }
        if (IsTextInputFocused()) return;

        // Sidebar thumbnails/outline/bookmarks keep their own arrow navigation.
        if (FocusManager.GetFocusedElement(Content.XamlRoot)
            is Microsoft.UI.Xaml.Controls.Primitives.SelectorItem or TreeViewItem)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Up:
                _activeViewer.ScrollByLines(-3);
                break;
            case VirtualKey.Down:
                _activeViewer.ScrollByLines(+3);
                break;
            case VirtualKey.Left:
                _activeViewer.GoToPage(_activeViewer.CurrentPage - 1);
                break;
            case VirtualKey.Right:
                _activeViewer.GoToPage(_activeViewer.CurrentPage + 1);
                break;
            case VirtualKey.PageUp:
                _activeViewer.ScrollByViewport(-0.9);
                break;
            case VirtualKey.PageDown:
                _activeViewer.ScrollByViewport(+0.9);
                break;
            case VirtualKey.Home:
                _activeViewer.GoToPage(0, recordHistory: true);
                break;
            case VirtualKey.End:
                _activeViewer.GoToPage(_activeViewer.PageCount - 1, recordHistory: true);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void CloseCurrentTab()
    {
        if (Tabs.SelectedItem is TabViewItem tab)
        {
            CloseTab(tab);
        }
    }

    // ---------------------------------------------------------------- find in document

    private void ShowFindBar()
    {
        if (CurrentView is not { IsDocumentLoaded: true })
        {
            return;
        }
        FindBar.Visibility = Visibility.Visible;
        // Prefill from the selection BEFORE SelectAll, so typing replaces it.
        if (_activeViewer?.HasSelection == true)
        {
            FindBox.Text = _activeViewer.SelectedText.Split('\n')[0].Trim();
        }
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
        if (!string.IsNullOrEmpty(FindBox.Text))
        {
            RunSearch();
        }
    }

    private void HideFindBar()
    {
        if (FindBar.Visibility != Visibility.Visible)
        {
            return;
        }
        FindBar.Visibility = Visibility.Collapsed;
        _searchCts?.Cancel();
        _activeViewer?.ClearSearch();
        _searchHits = [];
        _activeHitIndex = -1;
        FindCount.Text = "";
    }

    private void FindClose_Click(object sender, RoutedEventArgs e) => HideFindBar();
    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => RunSearch();
    private void MatchCase_Click(object sender, RoutedEventArgs e) => RunSearch();
    private void FindNext_Click(object sender, RoutedEventArgs e) => StepHit(+1);
    private void FindPrev_Click(object sender, RoutedEventArgs e) => StepHit(-1);

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            StepHit(shift ? -1 : +1);
            e.Handled = true;
        }
    }

    private async void RunSearch()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        string query = FindBox.Text;
        var viewer = _activeViewer;
        var document = CurrentView?.Viewer.Document;

        viewer?.ClearSearch();
        var collected = new List<SearchHit>();
        _searchHits = collected;
        _activeHitIndex = -1;
        FindCount.Text = "";

        if (string.IsNullOrEmpty(query) || viewer is null || document is null)
        {
            return;
        }

        try
        {
            await Task.Delay(200, cts.Token); // debounce rapid typing
            bool matchCase = MatchCaseButton.IsChecked == true;
            // Route each page's search through the viewer's render thread at
            // Background priority: visible tiles always outrank the sweep.
            var search = new DocumentSearch(document, query, matchCase, wholeWord: false,
                workQueue: viewer.WorkQueue);

            await search.RunAsync(
                onPageHits: hits => DispatcherQueue.TryEnqueue(() =>
                {
                    if (_searchCts != cts)
                    {
                        return; // superseded by a newer query
                    }
                    collected.AddRange(hits);
                    viewer.SetSearchResults(collected);
                    if (_activeHitIndex < 0 && collected.Count > 0)
                    {
                        _activeHitIndex = 0;
                        viewer.HighlightHit(collected[0]);
                    }
                    UpdateFindCount();
                }),
                onProgress: null,
                cts.Token);

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_searchCts == cts && collected.Count == 0)
                {
                    FindCount.Text = UiText.Current.NoResults;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded query; ignore.
        }
    }

    private void StepHit(int delta)
    {
        int n = _searchHits.Count;
        if (n == 0)
        {
            return;
        }
        _activeHitIndex = ((_activeHitIndex + delta) % n + n) % n;
        _activeViewer?.HighlightHit(_searchHits[_activeHitIndex]);
        UpdateFindCount();
    }

    private void UpdateFindCount()
    {
        int n = _searchHits.Count;
        FindCount.Text = n == 0 ? "" : $"{_activeHitIndex + 1} of {n}";
    }

    private void CopySelection()
    {
        // Sidebar-focused Ctrl+C means "copy PAGES"; otherwise copy text.
        if (CurrentView is { } view && view.TryCopyPages(cut: false))
        {
            return;
        }
        string text = _activeViewer?.SelectedText ?? "";
        if (!string.IsNullOrEmpty(text))
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
    }

    // ---------------------------------------------------------------- errors + external open

    internal async Task LoadDocumentAsync(string path, int? initialPage = null, double? initialZoom = null)
    {
        if (!File.Exists(path))
        {
            ShowError(UiText.Format("File not found: {0}", path));
            return;
        }
        OpenOrActivate(path);

        // Command-line overrides for scripted testing.
        if ((initialPage ?? initialZoom) is not null && CurrentView is { } view)
        {
            await view.EnsureLoadedAsync(null);
            if (initialZoom is double z)
            {
                view.Viewer.SetZoom(z);
            }
            if (initialPage is int p)
            {
                view.Viewer.GoToPage(p - 1);
            }
        }
    }

    // ---------------------------------------------------------------- forms / flatten / signatures

    /// <summary>
    /// Shows a standing notice for things the user needs to know about the
    /// document itself: an XFA form PDFDesk cannot fill, or the presence of
    /// digital signatures.
    /// </summary>
    private void UpdateDocumentNotice(PdfViewer viewer)
    {
        if (CurrentView is not { } view)
        {
            return;
        }

        // Both notices describe a property of the document, not an event, so
        // they carry a dismiss key: this method runs again on every tab load,
        // page edit and flatten, and without one a notice the user closed would
        // immediately reappear.
        if (viewer.IsXfaForm)
        {
            // Saying nothing here is the worst option: the fields are visible,
            // so the user types into them and nothing happens.
            view.ShowNotice(
                UiText.Translate("XFA form"),
                UiText.Translate("This document uses an XFA form, which PDF Desk cannot fill. Adobe Acrobat can open it."),
                InfoBarSeverity.Warning,
                dismissKey: "xfa");
            return;
        }

        int signatures = view.SignatureCount;
        if (signatures > 0)
        {
            view.ShowNotice(
                signatures == 1 ? UiText.Translate("Digitally signed document") : UiText.Format("{0} digital signatures", signatures),
                UiText.Translate("PDF Desk has not verified the signature. Open Document information for signature details."),
                InfoBarSeverity.Informational,
                dismissKey: "signatures");
        }
    }

    private async void FlattenButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view || view.Viewer.Document is not { } document)
        {
            return;
        }

        // Flatten is irreversible in the file and clears the undo history, so
        // it always asks — and says plainly what will be lost.
        var confirm = new ContentDialog
        {
            Title = UiText.Current.FlattenAnnotationsAction,
            Content = UiText.Current.FlattenAnnotationsExplanation,
            PrimaryButtonText = UiText.Current.Flatten,
            CloseButtonText = UiText.Current.Cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            view.Viewer.KillFormFocus();
            int changed = await view.FlattenDocumentAsync();
            UpdateToolbarForActive(); // flatten removes what made the item enabled
            // Not ShowError: this succeeded. Reporting it through the error
            // channel gave it a red bar, an error icon and error semantics for
            // screen readers.
            ShowNotice(
                changed == 0
                    ? UiText.Translate("Nothing to flatten.")
                    : UiText.Format(changed == 1 ? "Flattened {0} page. Save to write the change to disk."
                        : "Flattened {0} pages. Save to write the change to disk.", changed),
                changed == 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowError(UiText.Format("Flatten failed: {0}", ex.Message));
        }
    }

    private async Task ShowSignatureDetailsAsync(DocumentView view)
    {
        if (view.Viewer.Document is not { } document)
        {
            return;
        }

        IReadOnlyList<PdfSignatureInfo> signatures;
        try
        {
            signatures = await view.Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, document.GetSignatures);
        }
        catch (Exception ex)
        {
            ShowError(UiText.Format("Could not read signatures: {0}", ex.Message));
            return;
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };

        // This disclaimer is load-bearing, not boilerplate. PDFDesk ships no
        // cryptography: it has not checked the certificate, the digest, trust
        // or revocation. Presenting any of the fields below as proof of
        // authenticity would mislead someone about a document they may be
        // relying on, so the limitation goes first and is never softened.
        panel.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            Message = UiText.Current.SignatureVerificationExplanation,
            IsClosable = false,
            IsOpen = true,
        });

        foreach (var signature in signatures)
        {
            panel.Children.Add(new Border { Style = (Style)Application.Current.Resources["FlyoutSeparatorStyle"] });

            var rows = new StackPanel { Spacing = 4 };
            rows.Children.Add(new TextBlock
            {
                Text = UiText.Format("Signature {0}", signature.Index + 1),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });

            AddSignatureRow(rows, "Reason", string.IsNullOrWhiteSpace(signature.Reason) ? "—" : signature.Reason);
            AddSignatureRow(rows, "Signed", signature.SignedAt?.ToString("f", System.Globalization.CultureInfo.CurrentUICulture)
                ?? (string.IsNullOrWhiteSpace(signature.SignedAtRaw) ? "—" : signature.SignedAtRaw));
            AddSignatureRow(rows, "Format", string.IsNullOrWhiteSpace(signature.SubFilter) ? "—" : signature.SubFilter);
            AddSignatureRow(rows, "Coverage", UiText.Translate(signature.Coverage switch
            {
                SignatureCoverage.CoversWholeFile => "Signed byte range covers the whole file",
                SignatureCoverage.LeavesContentUnsigned => "Part of this file is outside the signed range — it was changed or added after signing",
                _ => "Could not read the signed byte range",
            }));
            if (signature.IsCertifying)
            {
                AddSignatureRow(rows, "Certified", UiText.Format("Author signature, DocMDP level {0}", signature.DocMdpPermission));
            }

            panel.Children.Add(rows);
        }

        await ShowDialogAsync(new ContentDialog
        {
            Title = signatures.Count == 1 ? UiText.Translate("Signature details") : UiText.Format("Signature details ({0})", signatures.Count),
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            CloseButtonText = UiText.Current.Close,
            CloseButtonStyle = (Style)Application.Current.Resources["DocumentPropertiesCloseStyle"],
        });
    }

    private static void AddSignatureRow(Panel parent, string label, string value)
    {
        parent.Children.Add(PropertyRow(UiText.Translate(label), value));
    }

    /// <summary>
    /// The app's single notice channel.
    ///
    /// Routes to the active document's own notice host so the card floats over
    /// the page rather than across the sidebar; falls back to the window-level
    /// host for messages that arrive with no document open (startup problems,
    /// a missing recent file, background failures).
    /// </summary>
    private void ShowNotice(string message, InfoBarSeverity severity, string? title = null, string? dismissKey = null, params NoticeAction[] actions)
    {
        if (CurrentView is { IsDocumentLoaded: true, LoadError: null } view)
        {
            WindowNotice.Clear();
            view.ShowNotice(title, message, severity, dismissKey, actions);
            return;
        }
        WindowNotice.Show(title, message, severity, dismissKey, actions);
    }

    private void ShowError(string message) => ShowNotice(message, InfoBarSeverity.Error);

    /// <summary>Surfaces a background/unhandled failure (called by App's safety net).</summary>
    internal void ReportBackgroundError(string message) =>
        ShowError(UiText.Format("Something went wrong: {0}", message));

    /// <summary>
    /// Shows a dialog through <see cref="DialogHost"/> so two can never overlap,
    /// filling in XamlRoot and theme centrally. Every ContentDialog in this
    /// window goes through here.
    /// </summary>
    private Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        dialog.XamlRoot ??= Content.XamlRoot;
        return DialogHost.ShowAsync(dialog);
    }
}
