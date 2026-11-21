using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PDFDesk.Controls;
using PDFDesk.Engine;
using Windows.System;

namespace PDFDesk;

public sealed partial class MainWindow
{
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
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, FindBar.Open);
        AddAccelerator(VirtualKey.G, VirtualKeyModifiers.Control, FocusPageJump);
        AddAccelerator(VirtualKey.F3, VirtualKeyModifiers.None, () => FindBar.StepHit(+1));
        AddAccelerator(VirtualKey.F3, VirtualKeyModifiers.Shift, () => FindBar.StepHit(-1));
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
        AddAccelerator(VirtualKey.F1, VirtualKeyModifiers.None, () => _ = KeyboardShortcutsDialog.ShowAsync(RootGrid), requiresDocument: false);
        // Ctrl+? — GNOME's other shortcuts-window binding (Shift+/ = ? on US layouts).
        AddAccelerator((VirtualKey)0xBF, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => _ = KeyboardShortcutsDialog.ShowAsync(RootGrid), requiresDocument: false);
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
                FindBar.Close();
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
            PDFDesk.Services.ClipboardText.SetText(text);
        }
    }
}
