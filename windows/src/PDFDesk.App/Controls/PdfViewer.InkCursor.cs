using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace PDFDesk.Controls;

// Set the cursor on the document surface, inside the ScrollViewer.
public sealed class DocumentCanvas : Canvas
{
    internal void SetDocumentCursor(InputCursor? cursor)
    {
        if (!ReferenceEquals(ProtectedCursor, cursor)) ProtectedCursor = cursor;
    }
}

public sealed partial class PdfViewer
{
    private static readonly Lazy<InputCursor> PenCursor = new(CreatePenCursor);
    private readonly InputCursor _textCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    private readonly InputCursor _blockedCursor = InputSystemCursor.Create(InputSystemCursorShape.UniversalNo);
    private Point? _mouseViewportPoint;

    private static unsafe InputCursor CreatePenCursor()
    {
        var handle = LoadImage(0, System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "pen.cur"),
            2 /* IMAGE_CURSOR */, 0, 0, 0x10 | 0x40 /* LR_LOADFROMFILE | LR_DEFAULTSIZE */);
        if (handle == 0) return InputSystemCursor.Create(InputSystemCursorShape.Cross);

        // Windows App SDK IInputCursorStaticsInterop::CreateFromHCursor.
        // Keep the source HCURSOR alive for the shared application cursor.
        using var factory = WinRT.ActivationFactory.Get("Microsoft.UI.Input.InputCursor",
            new Guid("ac6f5065-90c4-46ce-beb7-05e138e54117"));
        nint ptr = factory.ThisPtr;
        var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)ptr)[6];
        nint cursor = 0;
        Marshal.ThrowExceptionForHR(create(ptr, handle, &cursor));
        try { return WinRT.MarshalInspectable<InputCursor>.FromAbi(cursor); }
        finally { Marshal.Release(cursor); }
    }

    private void TrackMouse(PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        _mouseViewportPoint = e.GetCurrentPoint(Scroller).Position;
        if (_activeTool == AnnotationTool.Highlighter) RefreshDocumentCursor();
    }

    private void RefreshDocumentCursor()
    {
        if (_activeTool == AnnotationTool.Pen)
            DocumentExtent.SetDocumentCursor(PenCursor.Value);
        else if (_activeTool == AnnotationTool.Highlighter && _mouseViewportPoint is { } mouse && _layout is { PageCount: > 0 })
        {
            var factor = Scroller.ZoomFactor;
            var point = new Point((Scroller.HorizontalOffset + mouse.X) / factor,
                                  (Scroller.VerticalOffset + mouse.Y) / factor);
            DocumentExtent.SetDocumentCursor(_isSelecting || SelectionStartAt(point, false).Character >= 0
                ? _textCursor : _blockedCursor);
        }
        else DocumentExtent.SetDocumentCursor(null);
    }

    [DllImport("user32.dll", EntryPoint = "LoadImageW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint instance, string path, uint type, int width, int height, uint flags);
}
