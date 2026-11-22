using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PDFDesk.Engine;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace PDFDesk.Controls;

// Typing on the page, anywhere, whether or not there is anything there already.
//
// The box is a real XAML TextBox laid over the Win2D canvas rather than a caret
// drawn onto it. That buys IME, selection, clipboard, spellcheck and screen
// reader support for nothing, none of which is worth reimplementing.
//
// Nothing reaches the document until the box is committed, so an abandoned box
// leaves no annotation, no dirty marker and nothing on the undo stack.
public sealed partial class PdfViewer
{
    /// <summary>Where the box's top-left sits, in unrotated page-local points.</summary>
    private (int Index, AnnotationInfo Bounds, TextBoxContent Content)? _editingStamp;
    private (int Page, double X, double Y)? _textAnchor;

    /// <summary>Guards the commit path against re-entering itself from LostFocus.</summary>
    private bool _committingText;
    private Task? _textCommitTask;

    /// <summary>True while there is a box open, so Esc and focus rules can account for it.</summary>
    public bool IsEditingText => _textAnchor is not null;

    /// <summary>The style new boxes are created with. The shell owns it and persists it.</summary>
    public TextBoxStyle TextStyle { get; private set; } = TextBoxStyle.Default;

    /// <summary>Raised after a box is committed, so the shell can drop the tool.</summary>
    public event EventHandler? TextCommitted;
    public event EventHandler? TextCommitFailed;

    /// <summary>Raised when a box opens or closes, so the format bar can show and hide.</summary>
    public event EventHandler<bool>? TextEditingChanged;

    /// <summary>
    /// Restyles the open box, or sets the style for the next one. Applying live
    /// is the whole point of the format bar: the preview is the feature.
    /// </summary>
    public void SetTextStyle(TextBoxStyle style)
    {
        TextStyle = style;
        if (IsEditingText)
        {
            ApplyStyleToEditor();
            PositionTextEditor();
        }
    }

    /// <summary>
    /// Opens a box at a document point. An open box commits first, so clicking
    /// elsewhere with the tool armed finishes one and starts the next.
    /// </summary>
    private async void BeginTextAt(Point docPoint)
    {
        if (_layout is null || _document is null)
        {
            return;
        }
        if (IsEditingText)
        {
            await CommitTextAsync();
            if (IsEditingText)
            {
                return;
            }
        }

        int page = _layout.PageAt(docPoint.X, docPoint.Y);
        var (localX, localY) = ToPageLocal(page, docPoint);
        _textAnchor = (page, localX, localY);

        TextEditor.Text = string.Empty;
        ApplyStyleToEditor();
        PositionTextEditor();
        TextEditorFrame.Visibility = Visibility.Visible;
        TextEditor.Focus(FocusState.Programmatic);
        TextEditingChanged?.Invoke(this, true);
    }

    private async Task<bool> EditTextStampAtAsync(Point point)
    {
        if (_document is not { } document || _layout is not { PageCount: > 0 } layout || IsEditingText)
            return false;
        int page = layout.PageAt(point.X, point.Y);
        var (x, y) = ToPageLocal(page, point);
        var found = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
        {
            var hit = document.GetAnnotations(page).LastOrDefault(a => a.Subtype == StampSubtype &&
                x >= a.X && x <= a.X + a.Width && y >= a.Y && y <= a.Y + a.Height);
            return (hit, content: hit == null ? null : document.TryReadTextBox(page, hit.Index));
        });
        if (found.hit is not { } bounds || found.content is not { } text || _document != document ||
            _layout != layout || IsEditingText || _activeTool != AnnotationTool.None) return false;
        ClearObjectSelection();
        _textAnchor = (page, bounds.X, bounds.Y);
        _editingStamp = (bounds.Index, bounds, text);
        TextStyle = new(text.Font, text.FontSize, text.R, text.G, text.B, text.Bold, text.Italic, text.Underline, text.Align);
        TextEditor.Text = text.Text;
        ApplyStyleToEditor();
        PositionTextEditor();
        TextEditorFrame.Visibility = Visibility.Visible;
        TextEditor.Focus(FocusState.Programmatic);
        TextEditor.Select(text.Text.Length, 0);
        TextEditingChanged?.Invoke(this, true);
        return true;
    }

    /// <summary>The ink brush, mutated in place so the resource overrides keep pointing at it.</summary>
    private readonly SolidColorBrush _editorInk = new(Colors.Black);

    private readonly SolidColorBrush _editorPaper = new(Colors.Transparent);
    private bool _editorBrushesReady;

    /// <summary>
    /// Makes the box actually transparent, and its text actually the chosen ink.
    ///
    /// Setting Background and Foreground is not enough: a TextBox's template
    /// swaps in <c>TextControlBackground*</c> and <c>TextControlForeground*</c>
    /// per visual state, so the property loses the moment the box takes focus —
    /// which is always, since it is created focused. That is what painted an
    /// opaque grey slab with light text over the page. Overriding the keys on
    /// the element itself is what the template actually reads.
    ///
    /// The ink is one brush instance mutated in place rather than a new brush per
    /// change, because these resources are resolved once when the template is
    /// applied and would go on pointing at the old one.
    /// </summary>
    private void EnsureEditorBrushes()
    {
        if (_editorBrushesReady)
        {
            return;
        }
        _editorBrushesReady = true;


        var edge = new SolidColorBrush(Colors.Transparent);

        foreach (string key in new[]
                 {
                     "TextControlBackground", "TextControlBackgroundPointerOver",
                     "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                 })
        {
            TextEditor.Resources[key] = _editorPaper;
        }
        foreach (string key in new[]
                 {
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
                     "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
                 })
        {
            TextEditor.Resources[key] = edge;
        }
        foreach (string key in new[]
                 {
                     "TextControlForeground", "TextControlForegroundPointerOver",
                     "TextControlForegroundFocused", "TextControlForegroundDisabled",
                 })
        {
            TextEditor.Resources[key] = _editorInk;
        }
    }

    private void ApplyStyleToEditor()
    {
        EnsureEditorBrushes();

        _editorPaper.Color = _editingStamp.HasValue ? Colors.White : Colors.Transparent;
        TextEditorFrame.Background = _editorPaper;
        var s = TextStyle;
        TextEditor.FontFamily = new FontFamily(TextBoxFonts.WindowsFamily(s.Font));
        TextEditor.FontWeight = s.Bold
            ? Microsoft.UI.Text.FontWeights.Bold
            : Microsoft.UI.Text.FontWeights.Normal;
        TextEditor.FontStyle = s.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;

        // Underline is the one part of the style the editor cannot preview.
        // WinUI's TextBox has no TextDecorations — only RichEditBox does — and
        // drawing rules underneath by hand would need the per-line metrics the
        // control does not expose, so they would drift from the words they are
        // meant to sit under. The format bar's U shows the state instead, and
        // the rule appears the moment the box commits.

        // A new box has no width, so there is no slack for the alignment to
        // move the line within — until there are two lines, and then the short
        // one moves against the long one, exactly as it will on the page.
        TextEditor.TextAlignment = s.Align switch
        {
            TextAlign.Center => TextAlignment.Center,
            TextAlign.Right => TextAlignment.Right,
            TextAlign.Justify => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };

        _editorInk.Color = Color.FromArgb(255, s.R, s.G, s.B);
        TextEditor.Foreground = _editorInk;
    }

    /// <summary>
    /// Puts the box where the page point it is anchored to currently sits.
    ///
    /// Called on every scroll and zoom, so the box stays glued to the page
    /// rather than to the window. The font size scales with the zoom for the
    /// same reason: what is on screen has to be what lands.
    /// </summary>
    private void PositionTextEditor()
    {
        if (_textAnchor is not { } anchor || _layout is null)
        {
            return;
        }

        var doc = ToDocumentPoint(anchor.Page, anchor.X, anchor.Y);
        // Document space to viewer space: subtract the scroll, and the
        // ScrollViewer's own gesture zoom on top of the real one.
        double viewX = doc.X * Scroller.ZoomFactor - Scroller.HorizontalOffset;
        double viewY = doc.Y * Scroller.ZoomFactor - Scroller.VerticalOffset;

        var padding = TextEditor.Padding;
        TextEditorFrame.Margin = new Thickness(viewX - padding.Left, viewY - padding.Top, 0, 0);
        double scale = _zoom * Scroller.ZoomFactor;
        if (_editingStamp is { } original)
        {
            OriginalTextMask.Margin = new Thickness(viewX, viewY, 0, 0);
            OriginalTextMask.Width = original.Bounds.Width * scale;
            OriginalTextMask.Height = original.Bounds.Height * scale;
            OriginalTextMask.Visibility = Visibility.Visible;
            TextEditorFrame.Width = original.Content.WidthPt > 0
                ? original.Content.WidthPt * scale + padding.Left + padding.Right : double.NaN;
            TextEditor.TextWrapping = original.Content.WidthPt > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }
        TextEditor.FontSize = Math.Max(1, TextStyle.FontSize * _zoom * Scroller.ZoomFactor);
    }

    private void TextEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Esc commits rather than discards. Losing a paragraph to a stray key
        // is a far worse outcome than an unwanted box the user can undo.
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _ = CommitTextAsync();
        }
    }

    /// <summary>
    /// Holds off the commit-on-focus-loss while the format bar is being used.
    ///
    /// Without it the bar cannot work at all: clicking a swatch moves focus off
    /// the box, LostFocus commits, and the change lands on nothing. Focus events
    /// are no help on their own either, because the old element's LostFocus runs
    /// before the new one's GotFocus — so the shell sets this from the bar's
    /// *pointer press*, which happens earlier still.
    /// </summary>
    public bool SuspendTextCommit { get; set; }

    /// <summary>Returns the caret to the box after a trip to the format bar.</summary>
    public void FocusTextEditor()
    {
        SuspendTextCommit = false;
        if (IsEditingText)
        {
            TextEditor.Focus(FocusState.Programmatic);
        }
    }

    private void TextEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SuspendTextCommit)
        {
            return;
        }
        _ = CommitTextAsync();
    }

    /// <summary>
    /// Writes the box into the document and closes it. An empty box is dropped
    /// silently: it was a click that changed its mind.
    /// </summary>
    public Task CommitTextAsync() =>
        _textCommitTask is { IsCompleted: false } ? _textCommitTask : _textCommitTask = CommitTextCoreAsync();

    private async Task CommitTextCoreAsync()
    {
        if (_committingText || _textAnchor is not { } anchor || _document is not { } document)
        {
            return;
        }
        _committingText = true;
        try
        {
            string text = TextEditor.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (_editingStamp is { } empty)
                {
                    TextEditor.IsReadOnly = true;
                    try
                    {
                        if (!await RemoveAnnotationAsync(document, anchor.Page, empty.Index, "delete text", expectedText: empty.Content))
                        {
                            if (_document == document) TextCommitFailed?.Invoke(this, EventArgs.Empty);
                            return;
                        }
                    }
                    finally { TextEditor.IsReadOnly = false; }
                }
                CloseTextEditor();
                return;
            }

            var content = TextStyle.ToContent(text);
            if (_editingStamp is { } existing)
            {
                var before = existing.Content;
                content = content with { WidthPt = before.WidthPt };
                if (content != before)
                {
                    TextEditor.IsReadOnly = true;
                    try
                    {
                        if (!await _scheduler.RunAsync(PdfWorkPriority.Interactive,
                            () => document.UpdateTextBox(anchor.Page, existing.Index, before, content)))
                        {
                            TextCommitFailed?.Invoke(this, EventArgs.Empty);
                            return;
                        }
                    }
                    catch { TextCommitFailed?.Invoke(this, EventArgs.Empty); return; }
                    finally { TextEditor.IsReadOnly = false; }
                    if (_document != document) return;
                    InvalidatePage(anchor.Page);
                    DocumentEdited?.Invoke(this, EventArgs.Empty);
                    AnnotationEdited?.Invoke(this, new AnnotationEditEventArgs
                    {
                        Label = "edit text", PageIndex = anchor.Page,
                        UndoAction = d => d.ReplayTextBoxEdit(anchor.Page, existing.Index, content, before),
                        RedoAction = d => d.ReplayTextBoxEdit(anchor.Page, existing.Index, before, content),
                    });
                }
                CloseTextEditor();
                return;
            }
            AnnotationSpec? spec;
            TextEditor.IsReadOnly = true;
            try
            {
                spec = await _scheduler.RunAsync(PdfWorkPriority.Interactive,
                    () => document.AddTextBox(anchor.Page, anchor.X, anchor.Y, content));
            }
            catch
            {
                spec = null;
            }
            finally
            {
                TextEditor.IsReadOnly = false;
            }
            if (_document != document)
            {
                return;
            }
            if (spec is null)
            {
                TextCommitFailed?.Invoke(this, EventArgs.Empty);
                return;
            }

            CloseTextEditor();
            InvalidatePage(anchor.Page);
            DocumentEdited?.Invoke(this, EventArgs.Empty);
            RaiseAnnotationAdded("text", anchor.Page, spec);
            TextCommitted?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _committingText = false;
        }
    }

    private void CloseTextEditor()
    {
        _textAnchor = null;
        _editingStamp = null;
        OriginalTextMask.Visibility = Visibility.Collapsed;
        TextEditorFrame.Width = double.NaN;
        TextEditor.TextWrapping = TextWrapping.NoWrap;
        TextEditorFrame.Visibility = Visibility.Collapsed;
        TextEditor.Text = string.Empty;
        TextEditingChanged?.Invoke(this, false);
    }
}

/// <summary>
/// How a text box looks. Mirrors <see cref="TextBoxContent"/> minus the words,
/// so the shell can carry a style around and persist it between boxes.
/// </summary>
public sealed record TextBoxStyle(
    PdfStandardFont Font,
    double FontSize,
    byte R,
    byte G,
    byte B,
    bool Bold,
    bool Italic,
    bool Underline = false,
    TextAlign Align = TextAlign.Left)
{
    public static TextBoxStyle Default =>
        new(PdfStandardFont.Helvetica, 14, 0, 0, 0, false, false);

    /// <summary>
    /// A new box has no width: it grows with what is typed, and only a corner
    /// drag gives it one. So the style carries no width either — it is a
    /// property of a box that exists, not of the next one to be made.
    /// </summary>
    public TextBoxContent ToContent(string text) =>
        new(text, Font, FontSize, R, G, B, Bold, Italic, Underline, Align);
}
