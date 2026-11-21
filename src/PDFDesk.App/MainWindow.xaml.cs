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
    private readonly DockedToolbarMotion _quickToolsMotion;
    private bool _suppressPageBox;
    private bool _restoringSession;

    private PrintService? _printService;
    private bool _copyingPdfFile;

    internal MainWindow(SessionState session, bool startupWindow)
    {
        _initialSession = session;
        _startupWindow = startupWindow;
        InitializeComponent();
        _quickToolsMotion = new(ReaderQuickTools, ExpandedQuickTools, DockedReaderToggle);
        AboutVersion.Text = AppBuildInfo.CurrentVersion;
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
        FindBar.Close();
        FindBar.Attach(null);
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

        FindBar.Attach(_activeViewer);
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
        _quickToolsMotion.SetExpanded(_quickToolsExpanded, ExpandedReaderPanelWidth, CompactReaderPanelWidth,
            (CornerRadius)Application.Current.Resources["ReaderToolbarRadius"], DockRadius);
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
    private void FindButton_Click(object sender, RoutedEventArgs e) => FindBar.Open();
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
    private void OrganizePages_Click(object sender, RoutedEventArgs e)
    {
        ViewActionsFlyout.Hide();
        CurrentView?.OpenOrganizer();
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
            var title = DocumentNameFormatter.Fit(view.DisplayName, name => name.Length <= 28);
            await _printService.ShowAsync(document, title, view.Viewer.WorkQueue);
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

    private Task ShowPropertiesAsync() =>
        CurrentView is { IsDocumentLoaded: true, LoadError: null } view
            ? DocumentPropertiesDialog.ShowAsync(RootGrid, view, ShowError)
            : Task.CompletedTask;

    private void ReportProblemMenuItem_Click(object sender, RoutedEventArgs e) => _ = ProblemReportDialog.ShowAsync(RootGrid);

    // ---------------------------------------------------------------- shortcuts overlay

    private void ShortcutsMenuItem_Click(object sender, RoutedEventArgs e) => _ = KeyboardShortcutsDialog.ShowAsync(RootGrid);

    // ---------------------------------------------------------------- command palette

    private void ShowPalette()
    {
        var commands = new List<PaletteCommand>
        {
            new(UiText.Current.OpenOrCreatePdf, "Ctrl+O", () => OpenButton_Click(this, null!)),
            new(UiText.Current.KeyboardShortcuts, "F1", () => _ = KeyboardShortcutsDialog.ShowAsync(RootGrid)),
            new(UiText.Current.Settings, "", () => SettingsButton_Click(this, null!)),
        };

        if (_activeViewer is { } viewer && CurrentView is { IsDocumentLoaded: true, LoadError: null })
        {
            commands.AddRange(
            [
                new(UiText.Current.FindInDocument, "Ctrl+F", FindBar.Open),
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
        return DialogHost.ShowAsync(RootGrid, dialog);
    }
}
