namespace PDFDesk;

/// <summary>
/// The single source of truth for every keyboard shortcut PDFDesk ships. The
/// shortcuts dialog renders it; keep it in sync when adding accelerators
/// (PROJECT.md §5 mirrors this table).
/// </summary>
internal static class ShortcutCatalog
{
    internal sealed record Shortcut(string Name, string Keys);
    internal sealed record Group(string Title, Shortcut[] Shortcuts);

    internal static readonly Group[] Groups =
    [
        new("Navigation",
        [
            new("Scroll", "↑ / ↓"),
            new("Previous / next page", "← / →"),
            new("Screen up / down", "PgUp / PgDn"),
            new("Screen down / up", "Space / Shift+Space"),
            new("First / last page", "Home / End"),
            new("Back / forward", "Alt+← / Alt+→"),
            new("Go to page", "Ctrl+G"),
            new("Bookmark current page", "Ctrl+B"),
        ]),
        new("View",
        [
            new("Zoom in / out", "Ctrl++ / Ctrl+-"),
            new("Actual size", "Ctrl+1"),
            new("Fit width / fit page", "Ctrl+2 / Ctrl+0"),
            new("Rotate right / left", "Ctrl+R / Ctrl+Shift+R"),
            new("Night mode", "Ctrl+I"),
            new("Sidebar", "F9"),
            new("Presentation", "F5"),
        ]),
        new("Find",
        [
            new("Find in document", "Ctrl+F"),
            new("Next / previous match", "F3 / Shift+F3"),
        ]),
        new("Annotate",
        [
            new("Highlighter / highlight selection", "Ctrl+H"),
            new("Draw with the pen", "Ctrl+E"),
            new("Type on the page", "Ctrl+T"),
            new("Resize a picture before dropping it", "Ctrl+wheel"),
            new("Delete what you placed", "Delete / Backspace"),
            new("Put the tool away", "Esc"),
            new("Copy selected text", "Ctrl+C"),
        ]),
        // These take over from the document's own chords while a text box is
        // open or a form field has the caret, and hand them straight back on
        // Esc. Both halves matter, so both are listed.
        new("Text box",
        [
            new("Bold / italic / underline", "Ctrl+B / Ctrl+I / Ctrl+U"),
            new("Bigger / smaller", "Ctrl+Shift+. / Ctrl+Shift+,"),
            new("Left / centre / right / justify", "Ctrl+L / Ctrl+E / Ctrl+R / Ctrl+J"),
            new("Finish the box and free the shortcuts", "Esc"),
        ]),
        new("Form field",
        [
            new("Bold / italic", "Ctrl+B / Ctrl+I"),
            new("Bigger / smaller", "Ctrl+Shift+. / Ctrl+Shift+,"),
            new("Leave form field", "Esc"),
        ]),
        new("File & window",
        [
            new("Open", "Ctrl+O"),
            new("Save / save as", "Ctrl+S / Ctrl+Shift+S"),
            new("Print", "Ctrl+P"),
            new("Document properties", "Ctrl+D"),
            new("Close tab", "Ctrl+W"),
            new("Command palette", "Ctrl+K"),
            new("Keyboard shortcuts", "F1"),
        ]),
        new("Pages (thumbnail sidebar)",
        [
            new("Select multiple pages", "Ctrl+click / Shift+click"),
            new("Copy / cut pages", "Ctrl+C / Ctrl+X"),
            new("Paste pages (works across tabs)", "Ctrl+V"),
            new("Delete pages", "Delete"),
            new("Undo / redo", "Ctrl+Z / Ctrl+Y"),
        ]),
    ];
}
