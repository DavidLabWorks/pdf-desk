using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PDFDesk.Controls;
using PDFDesk.Engine;
using PDFDesk.Services;
using Windows.UI;

namespace PDFDesk;

/// <summary>
/// The annotation tool cluster's options panels — the Snipping-Tool-style
/// colour grid, size and opacity that drop down beneath whichever tool button
/// was clicked.
///
/// One <see cref="Flyout"/> per tool, built lazily and then only *mutated*:
/// replacing the visual tree of an open flyout closes it, which is why nothing
/// here rebuilds. And a plain Flyout, not a MenuFlyout — a MenuFlyout dismisses
/// the moment any item is clicked, and this panel has to stay put while the
/// user tries colours.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Swatches, laid out 6 to a row. Enough range to be useful without turning
    /// into a colour picker; the top row covers the greys that annotations
    /// actually get used in.
    /// </summary>
    private static readonly string[] ToolColors =
    [
        "#000000", "#5A5A5A", "#9A9A9A", "#FFFFFF", "#8E1B4B", "#E22222",
        "#F5761A", "#F5A623", "#FFD200", "#EEF23C", "#7ED321", "#1E9E4A",
        "#12A594", "#1BA1E2", "#2266DD", "#5B2FD1", "#9B51E0", "#C86DD7",
    ];

    private sealed class ToolPanel
    {
        public required Flyout Flyout { get; init; }
        public required FrameworkElement Anchor { get; init; }
        public required List<(string Hex, Border Ring)> Swatches { get; init; }
        public Border? Preview { get; set; }
        public TextBlock? SizeValue { get; set; }
        public TextBlock? OpacityValue { get; set; }
    }

    private readonly Dictionary<AnnotationTool, ToolPanel> _toolPanels = [];
    private AnnotationTool _hoveredButton;
    private bool _pointerInToolPanel;
    private AnnotationTool _hoverFlyout;
    private DispatcherQueueTimer? _toolHoverTimer;
    private DispatcherQueueTimer? _toolDismissTimer;

    /// <summary>True while any tool's options panel is open (used by the Esc ladder).</summary>
    private bool IsToolOptionsOpen => _toolPanels.Values.Any(p => p.Flyout.IsOpen);

    private void HideToolOptions()
    {
        _toolHoverTimer?.Stop();
        foreach (var panel in _toolPanels.Values)
        {
            panel.Flyout.Hide();
        }
    }

    /// <summary>Opens a tool's options directly beneath its own button.</summary>
    private void ShowToolOptions(AnnotationTool tool, bool hover = false)
    {
        if (tool is AnnotationTool.Note or AnnotationTool.Image)
        {
            return;
        }

        if (!_toolPanels.TryGetValue(tool, out var panel))
        {
            panel = BuildToolPanel(tool);
            _toolPanels[tool] = panel;
        }
        if (panel.Flyout.IsOpen)
        {
            return;
        }
        if (HasHoverOptions(tool))
        {
            if (_activeViewer?.ActiveTool != tool) return;
            if (!hover)
            {
                panel.Anchor.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                EditToolbarScroll.UpdateLayout();
            }
            _hoverFlyout = hover ? tool : AnnotationTool.None;
            _pointerInToolPanel = false;
            panel.Flyout.ShowAt(EditToolbarSurface, new FlyoutShowOptions
            {
                Placement = tool != AnnotationTool.Eraser
                    ? FlyoutPlacementMode.BottomEdgeAlignedLeft
                    : FlyoutPlacementMode.BottomEdgeAlignedRight,
                ShowMode = hover ? FlyoutShowMode.Transient : FlyoutShowMode.Standard,
            });
            return;
        }
        panel.Anchor.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        EditToolbarScroll.UpdateLayout();
        panel.Flyout.ShowAt(panel.Anchor);
    }

    private static bool HasHoverOptions(AnnotationTool tool) =>
        tool is AnnotationTool.Pen or AnnotationTool.Highlighter or AnnotationTool.Eraser;

    private void DrawingTool_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ToggleButton button || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var tool = button == PenToolButton ? AnnotationTool.Pen
            : button == HighlighterToolButton ? AnnotationTool.Highlighter : AnnotationTool.Eraser;
        _hoveredButton = tool;
        if (_activeViewer?.ActiveTool != tool || !button.IsEnabled || DialogHost.IsOpen
            || e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
        _toolDismissTimer?.Stop();
        if (_toolPanels.TryGetValue(tool, out var panel) && panel.Flyout.IsOpen) return;
        if (_toolHoverTimer is null)
        {
            _toolHoverTimer = DispatcherQueue.CreateTimer();
            _toolHoverTimer.Interval = TimeSpan.FromMilliseconds(120);
            _toolHoverTimer.IsRepeating = false;
            _toolHoverTimer.Tick += (_, _) =>
            {
                if (_hoveredButton != AnnotationTool.None && _activeViewer?.ActiveTool == _hoveredButton && !DialogHost.IsOpen)
                {
                    ShowToolOptions(_hoveredButton, hover: true);
                }
            };
        }
        if (!_toolHoverTimer.IsRunning)
        {
            _toolHoverTimer.Start();
        }
    }

    private void DrawingTool_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            _hoveredButton = AnnotationTool.None;
            _toolHoverTimer?.Stop();
            ScheduleToolDismiss();
        }
    }

    private void ScheduleToolDismiss()
    {
        if (_hoverFlyout == AnnotationTool.None || _hoveredButton == _hoverFlyout || _pointerInToolPanel)
        {
            return;
        }
        if (_toolDismissTimer is null)
        {
            _toolDismissTimer = DispatcherQueue.CreateTimer();
            _toolDismissTimer.Interval = TimeSpan.FromMilliseconds(220);
            _toolDismissTimer.IsRepeating = false;
            _toolDismissTimer.Tick += (_, _) =>
            {
                if (_hoverFlyout != AnnotationTool.None && _hoveredButton != _hoverFlyout && !_pointerInToolPanel
                    && _toolPanels.TryGetValue(_hoverFlyout, out var panel))
                {
                    panel.Flyout.Hide();
                }
            };
        }
        _toolDismissTimer.Stop();
        _toolDismissTimer.Start();
    }

    private void AttachHoverSurface(ToolPanel panel, AnnotationTool tool, StackPanel root, Thickness padding)
    {
        var surface = new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = padding,
            Child = root,
        };
        surface.PointerEntered += (_, e) =>
        {
            if (_hoverFlyout == tool && e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
            {
                _pointerInToolPanel = true;
                _toolDismissTimer?.Stop();
            }
        };
        surface.PointerExited += (_, e) =>
        {
            if (_hoverFlyout == tool && e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
            {
                _pointerInToolPanel = false;
                ScheduleToolDismiss();
            }
        };
        panel.Flyout.Closed += (_, _) =>
        {
            if (_hoverFlyout != tool) return;
            _hoverFlyout = AnnotationTool.None;
            _pointerInToolPanel = false;
            _toolDismissTimer?.Stop();
        };
        panel.Flyout.Content = surface;
    }

    private ToolStyle StyleFor(AnnotationTool tool)
    {
        _state.Settings.MigrateToolStyles();
        return tool == AnnotationTool.Highlighter
            ? _state.Settings.Highlighter!
            : _state.Settings.Pen!;
    }

    private ToolPanel BuildToolPanel(AnnotationTool tool)
    {
        var style = StyleFor(tool);
        bool isMarkup = tool == AnnotationTool.Highlighter;

        FrameworkElement anchor = tool switch
        {
            AnnotationTool.Pen => PenToolButton,
            AnnotationTool.Highlighter => HighlighterToolButton,
            AnnotationTool.Signature => SignToolButton,
            AnnotationTool.Eraser => EraserToolButton,
            _ => PenToolButton,
        };

        bool isPen = tool == AnnotationTool.Pen;
        var root = new StackPanel { Spacing = isPen || isMarkup ? 8 : 10, MinWidth = isPen || isMarkup ? 224 : 240 };
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var secondary = (Style)Application.Current.Resources["SecondaryTextStyle"];

        var panel = new ToolPanel { Flyout = new Flyout { OverlayInputPassThroughElement = Toolbar }, Anchor = anchor, Swatches = [] };

        if (tool == AnnotationTool.Eraser)
        {
            double width = Math.Clamp(_state.Settings.EraserWidthPt, PdfViewer.MinEraserWidthPt, PdfViewer.MaxEraserWidthPt);
            var slider = new Slider
            {
                Minimum = PdfViewer.MinEraserWidthPt,
                Maximum = PdfViewer.MaxEraserWidthPt,
                StepFrequency = 1,
                Value = width,
            };
            var value = new TextBlock { Text = $"{width:0} pt" };
            slider.ValueChanged += (_, e) =>
            {
                value.Text = $"{e.NewValue:0} pt";
                _state.Settings.EraserWidthPt = e.NewValue;
                foreach (var view in AllDocumentViews())
                {
                    view.Viewer.SetEraserWidth(e.NewValue);
                }
            };
            root.Children.Add(BuildPenSliderRow(UiText.Current.EraserWidth, slider, value, caption, secondary));
            panel.Flyout.FlyoutPresenterStyle = (Style)Application.Current.Resources["PenPanelPresenterStyle"];
            AttachHoverSurface(panel, tool, root, new Thickness(12, 8, 12, 8));
            panel.Flyout.OverlayInputPassThroughElement = RootGrid;
            panel.Flyout.Closed += (_, _) => _store.Save(_state);
            return panel;
        }

        // ---- Signature has its own panel entirely ----
        if (tool == AnnotationTool.Signature)
        {
            root.Width = 306;
            root.Padding = new Thickness(14);
            root.Spacing = 10;
            root.Children.Add(new TextBlock { Text = UiText.Current.SavedSignatures, FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            // Saved signatures are filled in asynchronously (decoding PNGs) but
            // the panel itself is built once, so keep a container to refill.
            var list = new StackPanel { Spacing = 6 };
            var scroll = new ScrollViewer
            {
                Content = list,
                MaxHeight = 226,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            root.Children.Add(scroll);
            _signatureList = list;

            root.Children.Add(new Border { Style = (Style)Application.Current.Resources["FlyoutSeparatorStyle"] });

            var draw = new Button
            {
                Content = UiText.Current.NewSignatureAction,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            draw.Click += async (_, _) =>
            {
                panel.Flyout.Hide();
                await ShowSignatureDialogAsync();
            };

            var import = new Button
            {
                Content = UiText.Current.ImportSignatureAction,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(import, UiText.Translate("Import a signature image"));
            ToolTipService.SetToolTip(import, UiText.Translate("Use a photo or scan of your signature; the paper is removed for you"));
            import.Click += async (_, _) =>
            {
                panel.Flyout.Hide();
                await ShowSignatureDialogAsync(startWithImport: true);
            };
            var actions = new Grid { ColumnSpacing = 8 };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(import, 1);
            actions.Children.Add(draw);
            actions.Children.Add(import);
            root.Children.Add(actions);

            panel.Flyout.Content = root;
            panel.Flyout.Placement = FlyoutPlacementMode.Bottom;
            panel.Flyout.FlyoutPresenterStyle = (Style)Application.Current.Resources["PenPanelPresenterStyle"];
            panel.Flyout.Opened += async (_, _) =>
            {
                scroll.ChangeView(null, 0, null, true);
                await RefreshSignatureListAsync();
            };
            return panel;
        }

        // ---- Colours: a 6-column grid, like the reference ----
        var colourLabel = new TextBlock { Text = UiText.Current.Colour, Style = caption, VerticalAlignment = VerticalAlignment.Center };
        var preview = new Border { Width = 64, VerticalAlignment = VerticalAlignment.Center };
        panel.Preview = preview;
        UpdateToolSample(preview, style, isMarkup);
        var sample = new Grid { HorizontalAlignment = HorizontalAlignment.Right, Height = 24 };
        sample.Children.Add(preview);
        if (isMarkup)
            sample.Children.Add(new TextBlock { Text = "Aa", HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center });
        var paletteHeader = new Grid { MinHeight = 24, Children = { colourLabel, sample } };
        colourLabel.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(paletteHeader);
        panel.Flyout.FlyoutPresenterStyle = (Style)Application.Current.Resources["PenPanelPresenterStyle"];

        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 2 };
        for (int c = 0; c < 6; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        for (int r = 0; r < (ToolColors.Length + 5) / 6; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < ToolColors.Length; i++)
        {
            string hex = ToolColors[i];

            // A ring marks the selection. It's an outline rather than a tick so
            // it reads on both a black and a white swatch.
            var ring = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    new Windows.UI.ViewManagement.UISettings().GetColorValue(
                        Windows.UI.ViewManagement.UIColorType.Accent)),
                Visibility = SameColor(style.Color, hex) ? Visibility.Visible : Visibility.Collapsed,
            };
            // The colour lives in the button's CONTENT, not its Background:
            // Button's hover/pressed visual states override Background, which
            // made the swatch turn white under the cursor.
            var dot = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(HexToColor(hex)),
                // A hairline keeps white (and near-white) swatches visible.
                BorderThickness = new Thickness(1),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Grid();
            cell.Children.Add(dot);
            cell.Children.Add(ring);

            var swatch = new Button
            {
                Style = (Style)Application.Current.Resources["InkSwatchButtonStyle"],
                Content = cell,
            };
            swatch.Click += (_, _) =>
            {
                StyleFor(tool).Color = hex;
                _store.Save(_state);
                ApplyToolStylesToAll(tool);
            };
            panel.Swatches.Add((hex, ring));

            Grid.SetColumn(swatch, i % 6);
            Grid.SetRow(swatch, i / 6);
            grid.Children.Add(swatch);
        }
        root.Children.Add(grid);

        // ---- Markup kind (the highlighter also does underline / strikeout) ----
        if (isMarkup)
        {
            root.Children.Add(new TextBlock { Text = UiText.Translate("Style"), Style = caption });
            var kinds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var (label, value) in new[] { (UiText.Translate("Highlight"), "Highlight"), (UiText.Translate("Underline"), "Underline"), (UiText.Translate("Strikeout"), "Strikeout") })
            {
                var button = new ToggleButton
                {
                    Content = label,
                    IsChecked = string.Equals(style.Markup, value, StringComparison.OrdinalIgnoreCase),
                };
                button.Click += (s, _) =>
                {
                    StyleFor(tool).Markup = value;
                    _store.Save(_state);
                    // Mutually exclusive: re-check only the chosen one.
                    foreach (var other in kinds.Children.OfType<ToggleButton>())
                    {
                        other.IsChecked = ReferenceEquals(other, s);
                    }
                    ApplyToolStylesToAll(tool);
                };
                kinds.Children.Add(button);
            }
            root.Children.Add(kinds);
        }

        // ---- Size, with a live stroke preview above the slider ----
        if (isPen)
        {
            root.Children.Add(new Border { Style = (Style)Application.Current.Resources["FlyoutSeparatorStyle"] });
            var size = new Slider { Minimum = 1, Maximum = 12, StepFrequency = 0.5, Value = style.Size };
            size.ValueChanged += (_, e) =>
            {
                StyleFor(tool).Size = e.NewValue;
                _store.Save(_state);
                ApplyToolStylesToAll(tool);
            };
            panel.SizeValue = new TextBlock { Text = $"{style.Size:0.#} pt" };
            root.Children.Add(BuildPenSliderRow(UiText.Current.Size, size, panel.SizeValue, caption, secondary));
        }

        // ---- Opacity (the reference has this on every drawing tool) ----
        var opacity = new Slider { Minimum = 10, Maximum = 100, StepFrequency = 5, Value = style.Opacity * 100 };
        opacity.ValueChanged += (_, e) =>
        {
            StyleFor(tool).Opacity = e.NewValue / 100.0;
            _store.Save(_state);
            ApplyToolStylesToAll(tool);
        };
        panel.OpacityValue = new TextBlock { Text = style.Opacity.ToString("P0") };
        root.Children.Add(BuildPenSliderRow(UiText.Translate("Opacity"), opacity, panel.OpacityValue, caption, secondary));
        AttachHoverSurface(panel, tool, root, new Thickness(12));
        return panel;
    }

    private static Grid BuildPenSliderRow(string label, Slider slider, TextBlock value, Style caption, Style secondary)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        row.Children.Add(new TextBlock { Text = label, Style = caption, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(slider, 1);
        slider.MinWidth = 0;
        slider.Margin = new Thickness(0);
        slider.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(slider, label);
        row.Children.Add(slider);
        Grid.SetColumn(value, 2);
        value.Style = secondary;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(value);
        return row;
    }

    private static void UpdateToolSample(Border preview, ToolStyle style, bool isMarkup)
    {
        var color = HexToColor(style.Color);
        preview.Height = isMarkup ? (style.Markup is "Underline" or "Strikeout" ? 2 : 16) : Math.Max(2, style.Size * 1.5);
        preview.Margin = isMarkup && style.Markup == "Underline" ? new Thickness(0, 15, 0, 0) : new Thickness(0);
        preview.CornerRadius = new CornerRadius(preview.Height / 2);
        preview.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Color.FromArgb((byte)Math.Clamp(Math.Round(style.Opacity * 255), 0, 255), color.R, color.G, color.B));
    }

    private static bool SameColor(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pushes the current styles to every open tab and re-syncs the panel's own
    /// affordances. Deliberately mutates the cached elements rather than
    /// rebuilding — a rebuild would close the open flyout mid-interaction.
    /// </summary>
    private void ApplyToolStylesToAll(AnnotationTool? changed = null)
    {
        _state.Settings.MigrateToolStyles();
        var pen = _state.Settings.Pen!;
        var markup = _state.Settings.Highlighter!;

        foreach (var view in AllDocumentViews())
        {
            view.Viewer.SetInkStyle(pen.Color, pen.Size, pen.Opacity);
            view.Viewer.SetMarkupStyle(ParseMarkupKind(markup.Markup), markup.Color, markup.Opacity);
        }

        if (changed is { } tool && _toolPanels.TryGetValue(tool, out var panel))
        {
            var style = StyleFor(tool);
            foreach (var (hex, ring) in panel.Swatches)
            {
                ring.Visibility = SameColor(style.Color, hex) ? Visibility.Visible : Visibility.Collapsed;
            }
            if (panel.Preview is { } preview)
            {
                UpdateToolSample(preview, style, tool == AnnotationTool.Highlighter);
                if (panel.SizeValue is { } sizeValue) sizeValue.Text = $"{style.Size:0.#} pt";
                panel.OpacityValue!.Text = style.Opacity.ToString("P0");
            }
        }
    }

    /// <summary>Gives a freshly-opened tab the current tool styles.</summary>
    private void SeedToolStyles(PdfViewer viewer)
    {
        _state.Settings.MigrateToolStyles();
        var pen = _state.Settings.Pen!;
        var markup = _state.Settings.Highlighter!;
        viewer.SetInkStyle(pen.Color, pen.Size, pen.Opacity);
        viewer.SetMarkupStyle(ParseMarkupKind(markup.Markup), markup.Color, markup.Opacity);
        viewer.SetEraserWidth(_state.Settings.EraserWidthPt);
    }

    private readonly SignatureStore _signatures = new();
    private StackPanel? _signatureList;

    /// <summary>Opens the capture pad; a saved signature is armed immediately.</summary>
    /// <param name="startWithImport">Run the file picker as soon as the dialog is up.</param>
    private async Task ShowSignatureDialogAsync(bool startWithImport = false)
    {
        // Pick before the dialog exists, not from its Opened event. Firing an
        // out-of-process picker from inside a ContentDialog's open transition is
        // where the E_FAIL in PROJECT.md §10 came from, and it also flashed an
        // empty pad behind the picker and left one stranded on cancel.
        // PickImportAsync touches no XAML precisely so it can run this early.
        var imported = default(SignaturePad.ImportOutcome);
        if (startWithImport)
        {
            imported = await SignaturePad.PickImportAsync(
                WinRT.Interop.WindowNative.GetWindowHandle(this));

            // Cancelled the picker: they asked to import, not to draw, so an
            // empty pad here is only a second dialog to dismiss. A *failure*
            // still opens it, so the message has somewhere to land and the pad's
            // own Import button is one click away.
            if (imported is { Image: null, Failure: null })
            {
                return;
            }
        }

        var pad = new SignaturePad { RemoveBackground = _state.Settings.SignatureRemoveBackground };
        // After the constructor: an image that already carries alpha unticks the
        // box itself, and that has to win over the remembered setting.
        pad.ApplyImport(imported);

        var content = new ScrollViewer
        {
            Content = pad,
            MaxHeight = Math.Max(120, RootGrid.ActualHeight - 144),
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var dialog = new ContentDialog
        {
            Title = UiText.Translate("Add a signature"),
            Content = content,
            PrimaryButtonText = UiText.Current.Save,
            IsPrimaryButtonEnabled = pad.HasContent,
            CloseButtonText = UiText.Current.Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };

        pad.ContentChanged += (_, _) => dialog.IsPrimaryButtonEnabled = pad.HasContent;

        void ResizeContent(object sender, SizeChangedEventArgs e)
            => content.MaxHeight = Math.Max(120, e.NewSize.Height - 144);

        ContentDialogResult result;
        RootGrid.SizeChanged += ResizeContent;
        try
        {
            result = await ShowDialogAsync(dialog);
        }
        finally
        {
            RootGrid.SizeChanged -= ResizeContent;
        }

        // Read after the dialog closes rather than pushed while it was open:
        // ShowNotice renders an InfoBar inside DocumentView, which sits behind
        // the modal dialog, so raising it any earlier shows the user nothing.
        if (pad.LastFailure is { } failure)
        {
            ShowError(failure);
        }
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        // Only when the user actually touched the checkbox. The pad also
        // unticks it by itself for an image that has no paper to remove, and
        // saving that as the default would turn the feature off for good.
        if (pad.RemoveBackgroundChosenByUser)
        {
            _state.Settings.SignatureRemoveBackground = pad.RemoveBackground;
            _store.Save(_state);
        }

        if (pad.Rasterize() is not { } image)
        {
            ShowNotice(UiText.Translate("Nothing was drawn, so there was no signature to save."), InfoBarSeverity.Informational);
            return;
        }

        try
        {
            await _signatures.SaveAsync(image.Bgra, image.Width, image.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(UiText.Format("Could not save the signature: {0}", ex.Message));
            return;
        }

        ArmSignature(image.Bgra, image.Width, image.Height);
    }

    /// <summary>Loads saved signatures into the Sign flyout as clickable previews.</summary>
    private async Task RefreshSignatureListAsync()
    {
        if (_signatureList is not { } list)
        {
            return;
        }

        // Capped for the same reason the import path is: the on-page hover ghost
        // hands this buffer to CanvasBitmap inside a CanvasVirtualControl
        // session, where anything past MaxSingleTilePx silently draws nothing.
        // Signatures saved by earlier builds are still on disk at full
        // resolution, so the cap has to be here too, not only at import.
        var saved = await _signatures.ListAsync(TileMath.MaxSingleTilePx);
        list.Children.Clear();

        if (saved.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = UiText.Translate("No saved signatures yet."),
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            });
            return;
        }

        foreach (var signature in saved)
        {
            var row = (Grid)((DataTemplate)Application.Current.Resources["SavedSignatureRow"]).LoadContent();
            ((Image)row.FindName("Sample")).Source = ToBitmapSource(signature);
            var pick = (Button)row.FindName("Pick");
            AutomationProperties.SetName(pick, UiText.Translate("Use this signature"));
            ToolTipService.SetToolTip(pick, UiText.Current.SignaturePlacementHint);
            pick.Click += (_, _) =>
            {
                ArmSignature(signature.Bgra, signature.Width, signature.Height);
                HideToolOptions(); // get the panel off the page so it can be placed
            };

            var remove = (Button)row.FindName("Remove");
            AutomationProperties.SetName(remove, UiText.Translate("Delete this signature"));
            ToolTipService.SetToolTip(remove, UiText.Translate("Delete this signature"));
            remove.Click += async (_, _) =>
            {
                _signatures.Delete(signature.Path);
                await RefreshSignatureListAsync();
            };
            list.Children.Add(row);
        }
    }

    private void ArmSignature(byte[] bgra, int width, int height)
    {
        if (_activeViewer is not { } viewer) return;
        SetActiveTool(AnnotationTool.Signature);
        viewer.SetPendingStamp(bgra, width, height, "signature");
        viewer.PendingWidthPt = _state.Settings.SignatureWidthPt;
    }

    /// <summary>
    /// The Image tool: pick a picture, then place it the way a signature is
    /// placed. There is no options panel, so the button runs the picker.
    /// </summary>
    private async Task PickAndArmImageAsync()
    {
        var picked = await FilePickerHost.PickOpenAsync(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff");

        if (picked.Failed)
        {
            // The picker never opened, so this is emphatically not a bad image.
            ShowError(FilePickerHost.FailureMessage);
            return;
        }
        if (picked.File is not { } file)
        {
            return; // cancelled: arm nothing, say nothing
        }

        SavedSignature? loaded;
        try
        {
            // Capped at decode time, like the signature import, because past
            // MaxSingleTilePx a bitmap silently fails to draw inside the
            // CanvasVirtualControl session the hover ghost lives in. OpenReadAsync
            // rather than the path: a picked file can be a OneDrive placeholder.
            using var stream = await file.OpenReadAsync();
            loaded = await SignatureStore.TryLoadAsync(stream, file.Path, TileMath.MaxSingleTilePx);
        }
        catch (Exception ex)
        {
            ErrorLog.Default.Write(nameof(MainWindow), ex);
            loaded = null;
        }

        if (loaded is not { } image)
        {
            ShowError(UiText.Translate("That image could not be read."));
            return;
        }

        ArmImage(image.Bgra, image.Width, image.Height);
    }

    private void ArmImage(byte[] bgra, int width, int height)
    {
        if (_activeViewer is not { } viewer) return;
        SetActiveTool(AnnotationTool.Image);
        viewer.SetPendingStamp(bgra, width, height, "image");
        viewer.PendingWidthPt = _state.Settings.ImageWidthPt > 0
            ? _state.Settings.ImageWidthPt
            : viewer.NaturalWidthPt(width);
    }

    private static Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap ToBitmapSource(SavedSignature signature)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(signature.Width, signature.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(signature.Bgra, 0, signature.Bgra.Length);
        }
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>Ctrl+E: arm the pen, or put it away if it already is.</summary>
    private void TogglePenTool()
    {
        bool on = _activeViewer?.ActiveTool == AnnotationTool.Pen;
        SetActiveTool(on ? AnnotationTool.None : AnnotationTool.Pen);
    }

    private static MarkupKind ParseMarkupKind(string value) => value switch
    {
        "Underline" => MarkupKind.Underline,
        "Strikeout" => MarkupKind.Strikeout,
        _ => MarkupKind.Highlight,
    };
}
