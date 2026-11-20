using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PDFDesk.Controls;
using PDFDesk.Engine;
using PDFDesk.Services;
using Windows.System;
using Windows.UI;

namespace PDFDesk;

// The text tool's shell half: the toolbar button, and the format bar that
// floats over the page while a box is open.
//
// The bar is built in code rather than markup so the font and size lists come
// from TextBoxFonts alone. Two copies of those lists would eventually disagree,
// and the one in markup would be the one nobody updated.
public sealed partial class MainWindow
{
    private bool _textBarBuilt;

    /// <summary>The style new boxes take, remembered between them and across sessions.</summary>
    private TextBoxStyle CurrentTextStyle
    {
        get
        {
            var saved = _state.Settings.TextBox;
            return saved is null
                ? TextBoxStyle.Default
                : new TextBoxStyle(
                    Enum.TryParse<PdfStandardFont>(saved.Font, out var f) ? f : PdfStandardFont.Helvetica,
                    saved.Size, saved.R, saved.G, saved.B, saved.Bold, saved.Italic,
                    saved.Underline,
                    Enum.TryParse<TextAlign>(saved.Align, out var a) ? a : TextAlign.Left);
        }
    }

    private void PersistTextStyle(TextBoxStyle style)
    {
        _state.Settings.TextBox = new TextBoxSettings
        {
            Font = style.Font.ToString(),
            Size = style.FontSize,
            R = style.R,
            G = style.G,
            B = style.B,
            Bold = style.Bold,
            Italic = style.Italic,
            Underline = style.Underline,
            Align = style.Align.ToString(),
        };
        _store.Save(_state);
    }

    private void TextToolButton_Click(object sender, RoutedEventArgs e)
    {
        bool on = _activeViewer?.ActiveTool == AnnotationTool.Text;
        SetActiveTool(on ? AnnotationTool.None : AnnotationTool.Text);
    }

    /// <summary>
    /// Wires a viewer's text events. Called from the same place every other
    /// per-view subscription is made.
    /// </summary>
    private void AttachTextTool(PdfViewer viewer)
    {
        viewer.SetTextStyle(CurrentTextStyle);
        viewer.TextEditingChanged += Viewer_TextEditingChanged;
        viewer.TextCommitFailed += Viewer_TextCommitFailed;
        // Placing a box is a one-shot action, like a sticky note: the tool drops
        // after it commits rather than arming the next click as another box.
        viewer.TextCommitted += (_, _) => SetActiveTool(AnnotationTool.None);
    }

    private void DetachTextTool(PdfViewer viewer)
    {
        viewer.TextEditingChanged -= Viewer_TextEditingChanged;
        viewer.TextCommitFailed -= Viewer_TextCommitFailed;
    }

    private void Viewer_TextCommitFailed(object? sender, EventArgs e) =>
        ShowError(UiText.Current.TextCommitFailed);

    private void Viewer_TextEditingChanged(object? sender, bool editing)
    {
        if (!ReferenceEquals(sender, _activeViewer))
        {
            return;
        }
        if (editing)
        {
            BuildTextFormatBar();
        }
        TextFormatBar.Visibility = editing && !_focusReading ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Fills the format bar once. Rebuilding it per box would drop whatever the
    /// user was mid-interaction with, and the controls are stateless besides.
    /// </summary>
    private void BuildTextFormatBar()
    {
        if (_textBarBuilt)
        {
            SyncTextFormatBar();
            return;
        }
        _textBarBuilt = true;

        // Claim the box's commit BEFORE focus moves. A pointer press arrives
        // ahead of both the editor's LostFocus and the bar's GotFocus, which is
        // the only ordering that gets in front of the commit. handledEventsToo,
        // because the controls themselves mark the press handled.
        TextFormatBar.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) =>
            {
                if (_activeViewer is { IsEditingText: true } v)
                {
                    v.SuspendTextCommit = true;
                }
            }),
            handledEventsToo: true);

        var toggleStyle = (Style)Application.Current.Resources["TextFormatToggleStyle"];
        var family = BuildTextFormatPicker(TextBoxFonts.Families, TextBoxFonts.DisplayName,
            font => Restyle(s => s with { Font = font }), 102, UiText.Translate("Font"));
        var size = BuildTextFormatPicker(TextBoxFonts.Sizes, pt => pt.ToString("0.##"),
            pt => Restyle(s => s with { FontSize = pt }), 62, UiText.Translate("Font size"));

        var bold = new ToggleButton { Content = "B", Style = toggleStyle, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
        bold.Click += (_, _) => Restyle(s => s with { Bold = bold.IsChecked == true });
        AutomationProperties.SetName(bold, "Bold");
        ToolTipService.SetToolTip(bold, "Bold");

        var italic = new ToggleButton
        {
            Content = "I",
            Style = toggleStyle,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
        };
        italic.Click += (_, _) => Restyle(s => s with { Italic = italic.IsChecked == true });
        AutomationProperties.SetName(italic, "Italic");
        ToolTipService.SetToolTip(italic, "Italic (Ctrl+I)");

        // The decoration goes on a TextBlock inside the button, not on the
        // button: TextDecorations is a text property, and a ToggleButton is a
        // ContentControl.
        var underline = new ToggleButton
        {
            Content = new TextBlock
            {
                Text = "U",
                TextDecorations = Windows.UI.Text.TextDecorations.Underline,
            },
            Style = toggleStyle,
        };
        underline.Click += (_, _) => Restyle(s => s with { Underline = underline.IsChecked == true });
        AutomationProperties.SetName(underline, "Underline");
        ToolTipService.SetToolTip(underline, "Underline (Ctrl+U)");

        // Mutually exclusive, so they are re-synced from the style rather than
        // toggled against each other: SyncTextFormatBar is already the one place
        // that decides which affordance is lit.
        var alignment = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (align, label) in new (TextAlign, string)[]
        {
            (TextAlign.Left, "Align left (Ctrl+L)"),
            (TextAlign.Center, "Centre (Ctrl+E)"),
            (TextAlign.Right, "Align right (Ctrl+R)"),
            (TextAlign.Justify, "Justify (Ctrl+J)"),
        })
        {
            var value = align;
            var button = new ToggleButton { Content = AlignmentIcon(value), Style = toggleStyle };
            button.Click += (_, _) => Restyle(s => s with { Align = value });
            AutomationProperties.SetName(button, label);
            ToolTipService.SetToolTip(button, label);
            alignment.Children.Add(button);
            _textAlignButtons[value] = button;
        }

        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, r, g, b) in FormTextColors)
        {
            byte cr = r, cg = g, cb = b;
            var dot = new Border
            {
                Width = 15,
                Height = 15,
                CornerRadius = new CornerRadius(8),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, cr, cg, cb)),
            };
            var swatch = new ToggleButton
            {
                Style = toggleStyle,
                Content = dot,
            };
            swatch.Click += (_, _) => Restyle(s => s with { R = cr, G = cg, B = cb });
            AutomationProperties.SetName(swatch, label);
            ToolTipService.SetToolTip(swatch, label);
            swatches.Children.Add(swatch);
            _textColorButtons[(r, g, b)] = swatch;
        }

        TextFormatControls.Children.Add(family);
        TextFormatControls.Children.Add(size);
        TextFormatControls.Children.Add(new Border { Style = (Style)Application.Current.Resources["HeaderSeparatorStyle"] });
        TextFormatControls.Children.Add(bold);
        TextFormatControls.Children.Add(italic);
        TextFormatControls.Children.Add(underline);
        TextFormatControls.Children.Add(new Border { Style = (Style)Application.Current.Resources["HeaderSeparatorStyle"] });
        TextFormatControls.Children.Add(alignment);
        TextFormatControls.Children.Add(new Border { Style = (Style)Application.Current.Resources["HeaderSeparatorStyle"] });
        TextFormatControls.Children.Add(swatches);

        _textFamilyBox = family;
        _textSizeBox = size;
        _textBoldButton = bold;
        _textItalicButton = italic;
        _textUnderlineButton = underline;
        SyncTextFormatBar();
    }

    private DropDownButton BuildTextFormatPicker<T>(IEnumerable<T> values, Func<T, string> display,
        Action<T> select, double width, string label)
    {
        var menu = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            OverlayInputPassThroughElement = TextFormatBar,
        };
        Action? applySelection = null;
        foreach (var value in values)
        {
            var item = new ToggleMenuFlyoutItem { Text = display(value) };
            item.Click += (_, _) => applySelection = () => select(value);
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) =>
        {
            var apply = applySelection;
            applySelection = null;
            apply?.Invoke();
        };

        var picker = new DropDownButton
        {
            Width = width,
            Style = (Style)Application.Current.Resources["TextFormatPickerStyle"],
            Flyout = menu,
        };
        AutomationProperties.SetName(picker, label);
        ToolTipService.SetToolTip(picker, label);
        return picker;
    }

    private static void SyncTextFormatPicker(DropDownButton? picker, int index)
    {
        if (picker?.Flyout is not MenuFlyout menu || index < 0 || index >= menu.Items.Count) return;

        for (int i = 0; i < menu.Items.Count; i++)
        {
            var item = (ToggleMenuFlyoutItem)menu.Items[i];
            item.IsChecked = i == index;
            if (item.IsChecked) picker.Content = item.Text;
        }
    }

    /// <summary>
    /// The four alignment affordances, drawn rather than set from a glyph.
    ///
    /// Segoe MDL2 has AlignLeft, AlignCenter and AlignRight and no justify at
    /// all — the code point next to them in the range is an unrelated icon, and
    /// a wrong glyph in an icon font fails silently by drawing the wrong
    /// picture. Four bars say it better anyway: ragged on the side that is
    /// ragged, flush on the side that is flush, and flush both ways for
    /// justified.
    /// </summary>
    private static StackPanel AlignmentIcon(TextAlign align)
    {
        var icon = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var lineStyle = (Style)Application.Current.Resources["TextFormatAlignmentLineStyle"];

        // Alternating full and short bars, so a ragged edge is visible at all.
        foreach (bool full in new[] { true, false, true, false })
        {
            icon.Children.Add(new Border
            {
                Width = full || align == TextAlign.Justify ? 14 : 9,
                Style = lineStyle,
                HorizontalAlignment = align switch
                {
                    TextAlign.Center => HorizontalAlignment.Center,
                    TextAlign.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Left,
                },
            });
        }
        return icon;
    }

    private DropDownButton? _textFamilyBox;
    private DropDownButton? _textSizeBox;
    private ToggleButton? _textBoldButton;
    private ToggleButton? _textItalicButton;
    private ToggleButton? _textUnderlineButton;
    private readonly Dictionary<TextAlign, ToggleButton> _textAlignButtons = [];
    private readonly Dictionary<(byte R, byte G, byte B), ToggleButton> _textColorButtons = [];

    /// <summary>Points the bar's controls at the current style without raising their events.</summary>
    private void SyncTextFormatBar()
    {
        var style = _activeViewer?.TextStyle ?? CurrentTextStyle;
        _syncingTextBar = true;
        try
        {
            SyncTextFormatPicker(_textFamilyBox, Math.Max(0, Array.IndexOf(TextBoxFonts.Families, style.Font)));
            int size = Array.FindIndex(TextBoxFonts.Sizes, v => Math.Abs(v - style.FontSize) < 0.01);
            SyncTextFormatPicker(_textSizeBox, size >= 0 ? size : Array.IndexOf(TextBoxFonts.Sizes, 14));
            if (_textBoldButton is { } b) { b.IsChecked = style.Bold; }
            if (_textItalicButton is { } it) { it.IsChecked = style.Italic; }
            if (_textUnderlineButton is { } u) { u.IsChecked = style.Underline; }
            foreach (var (align, button) in _textAlignButtons)
            {
                button.IsChecked = align == style.Align;
            }
            foreach (var (color, button) in _textColorButtons)
            {
                button.IsChecked = color == (style.R, style.G, style.B);
            }
        }
        finally
        {
            _syncingTextBar = false;
        }
    }

    private bool _syncingTextBar;

    /// <summary>
    /// Applies a change to the open box and remembers it. Live, because seeing
    /// the change on the page is the point of having the bar at all.
    /// </summary>
    private void Restyle(Func<TextBoxStyle, TextBoxStyle> change)
    {
        if (_syncingTextBar || _activeViewer is not { } viewer)
        {
            return;
        }
        var style = change(viewer.TextStyle);
        viewer.SetTextStyle(style);
        PersistTextStyle(style);

        // The bar has to follow, because a change can now arrive from the
        // keyboard as well as from a click on the affordance itself — Ctrl+B
        // with the bar showing an unlit B would be lying about the box.
        SyncTextFormatBar();

        // Straight back to typing. This also lifts the commit suspension, so the
        // box behaves normally again the moment the trip to the bar is over.
        viewer.FocusTextEditor();
    }

    // ---- what the keyboard reaches ----
    //
    // Each of these is what the matching accelerator runs while a box is open,
    // and they all go through Restyle so the page, the bar and the remembered
    // style stay one thing.

    private void ToggleTextBold() => Restyle(s => s with { Bold = !s.Bold });

    private void ToggleTextItalic() => Restyle(s => s with { Italic = !s.Italic });

    private void ToggleTextUnderline() => Restyle(s => s with { Underline = !s.Underline });

    private void SetTextAlign(TextAlign align) => Restyle(s => s with { Align = align });

    /// <summary>Ctrl+Shift+&gt; and &lt;: up or down the size picker's own list.</summary>
    private void StepTextSize(int direction) =>
        Restyle(s => s with { FontSize = TextBoxFonts.StepSize(s.FontSize, direction) });

    // ---- the same chords, aimed at a form field ----

    /// <summary>
    /// Bold or italic on the focused field, which works only where the file
    /// carries the font to do it with.
    ///
    /// A form field's face comes from the font resource its <c>/DA</c> names,
    /// and that resource has to exist in the AcroForm's <c>/DR</c> — most files
    /// carry one regular face and nothing else. The engine tries the
    /// conventional sibling name and reverts if the field comes back blank, so
    /// the worst case is that nothing happens. Which is worth saying out loud
    /// rather than leaving the user pressing the key harder.
    /// </summary>
    private async Task RestyleFocusedFieldAsync(Func<FieldAppearance, FieldAppearance> change, string what)
    {
        if (_activeViewer is not { } viewer)
        {
            return;
        }
        if (!await viewer.RestyleFocusedFieldAsync(change))
        {
            ShowNotice(
                $"This form field cannot be made {what}: the PDF does not carry that font for it.",
                InfoBarSeverity.Informational);
        }
    }

    private void ToggleFieldBold() => _ = RestyleFocusedFieldAsync(
        a => a with { Bold = !(a.Bold ?? false) }, "bold");

    private void ToggleFieldItalic() => _ = RestyleFocusedFieldAsync(
        a => a with { Italic = !(a.Italic ?? false) }, "italic");

    /// <summary>
    /// Steps the focused field's text size. Auto-sized fields (a <c>/DA</c> size
    /// of zero, meaning "fit the box") start from the list's default rather than
    /// from zero, which would step to the smallest size there is.
    /// </summary>
    private void StepFieldSize(int direction) => _ = RestyleFocusedFieldAsync(
        a => a with
        {
            FontSize = TextBoxFonts.StepSize(a.IsAutoSize ? 12 : a.FontSize, direction),
        },
        direction > 0 ? "larger" : "smaller");

    /// <summary>
    /// The chords that belong to a caret before they belong to the document.
    ///
    /// Every one of these used to reach straight past an open text box: Ctrl+B
    /// bookmarked the page while you were trying to embolden a word, Ctrl+I
    /// flipped the whole document to night mode, Ctrl+E armed the pen and Ctrl+R
    /// rotated the page. Registering them here, in one place, with all three of
    /// their meanings side by side, is what makes that hard to reintroduce.
    /// </summary>
    private void AddTextFormattingAccelerators()
    {
        const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control;
        const VirtualKeyModifiers CtrlShift = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;

        AddAccelerator(VirtualKey.B, Ctrl, ToggleBookmark,
            textBoxAction: ToggleTextBold, formFieldAction: ToggleFieldBold);
        AddAccelerator(VirtualKey.I, Ctrl, ToggleNightMode,
            textBoxAction: ToggleTextItalic, formFieldAction: ToggleFieldItalic);
        AddAccelerator(VirtualKey.U, Ctrl, action: null,
            textBoxAction: ToggleTextUnderline);

        AddAccelerator(VirtualKey.E, Ctrl, TogglePenTool,
            textBoxAction: () => SetTextAlign(TextAlign.Center));
        AddAccelerator(VirtualKey.L, Ctrl, action: null,
            textBoxAction: () => SetTextAlign(TextAlign.Left));
        AddAccelerator(VirtualKey.R, Ctrl, () => _activeViewer?.RotateClockwise(),
            textBoxAction: () => SetTextAlign(TextAlign.Right));
        AddAccelerator(VirtualKey.J, Ctrl, action: null,
            textBoxAction: () => SetTextAlign(TextAlign.Justify));

        // 0xBE and 0xBC are the period and comma keys — Ctrl+Shift+> and <, the
        // way every word processor spells "one size bigger". Named by code
        // rather than by VirtualKey because the enum has no member for them, the
        // same as the Ctrl+? binding in RegisterAccelerators.
        AddAccelerator((VirtualKey)0xBE, CtrlShift, action: null,
            textBoxAction: () => StepTextSize(+1), formFieldAction: () => StepFieldSize(+1));
        AddAccelerator((VirtualKey)0xBC, CtrlShift, action: null,
            textBoxAction: () => StepTextSize(-1), formFieldAction: () => StepFieldSize(-1));
    }
}
