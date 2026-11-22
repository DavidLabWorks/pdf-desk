using Microsoft.UI;
using Windows.UI;

namespace PDFDesk.Styles;

/// <summary>
/// Every colour PDFDesk draws with Win2D, defined once.
///
/// XAML chrome uses <c>{ThemeResource}</c> brushes and must keep doing so —
/// this class is deliberately NOT a mirror of those values. It exists because
/// a <see cref="Microsoft.Graphics.Canvas.CanvasDrawingSession"/> takes a
/// <see cref="Color"/>, and looking one up from code hits the same trap as
/// resolving a theme brush: <c>Application.Current.Resources</c> returns the
/// dark-theme value whatever the active theme is.
///
/// Note which axis each colour keys on. Chrome around the page follows
/// <c>darkSurround</c> (app theme OR night mode); marks drawn *over* the page
/// follow <c>night</c> alone, because what sits behind them is the page, not
/// the window.
/// </summary>
internal static class PDFDeskColors
{
    /// <summary>Hairline around each page, separating white paper from a light field.</summary>
    public static Color PageBorder(bool darkSurround) =>
        darkSurround ? Color.FromArgb(255, 60, 60, 66) : Color.FromArgb(255, 200, 200, 205);

    /// <summary>Paper fill drawn before the render arrives.</summary>
    public static Color PageFill(bool night) => night ? Colors.Black : Colors.White;

    /// <summary>
    /// Text selection. Keyed on night mode: the overlays are drawn after the
    /// tiles and are NOT inverted, so the daylight blue would sit almost
    /// invisibly on an inverted black page.
    /// </summary>
    public static Color Selection(bool night) =>
        night ? Color.FromArgb(110, 90, 170, 255) : Color.FromArgb(90, 0, 120, 215);

    /// <summary>Inactive search hit.</summary>
    public static Color Search(bool night) =>
        night ? Color.FromArgb(130, 200, 160, 0) : Color.FromArgb(110, 255, 210, 0);

    /// <summary>The search hit currently stepped to.</summary>
    public static Color ActiveSearch(bool night) =>
        night ? Color.FromArgb(170, 255, 150, 40) : Color.FromArgb(150, 255, 140, 0);

    // ---- Selected signature ----

    /// <summary>Dashed frame around a placed signature that is selected for moving.</summary>
    public static Color SelectedStampBorder(bool night) =>
        night ? Color.FromArgb(230, 150, 200, 255) : Color.FromArgb(230, 0, 100, 200);

    /// <summary>
    /// Corner resize handles. Near-opaque and light, so they read as grabbable
    /// against both the dashed frame and whatever the signature sits on — a
    /// handle the same colour as the border would disappear into it.
    /// </summary>
    public static Color SelectedStampHandle(bool night) =>
        night ? Color.FromArgb(245, 235, 245, 255) : Color.FromArgb(245, 255, 255, 255);

    // ---- Form fields ----
    //
    // PDFium's FFLDraw pass fills widgets and draws no edge, so two fields that
    // touch — a name box directly above an email box — merge into one wash with
    // no seam between them. These strokes are PDFDesk's, drawn over the tiles.

    /// <summary>Edge of an editable field that does not have focus.</summary>
    public static Color FormFieldBorder(bool night) =>
        night ? Color.FromArgb(150, 120, 180, 255) : Color.FromArgb(140, 0, 90, 180);

    /// <summary>Edge of the field currently taking keystrokes.</summary>
    public static Color FormFieldFocusBorder(bool night) =>
        night ? Color.FromArgb(255, 150, 200, 255) : Color.FromArgb(255, 0, 100, 200);

    /// <summary>Edge of a read-only field — present, but not an invitation to type.</summary>
    public static Color FormFieldReadOnlyBorder(bool night) =>
        night ? Color.FromArgb(110, 160, 160, 170) : Color.FromArgb(110, 120, 120, 130);
}
