using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PDFDesk.Engine;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace PDFDesk.Controls;

public sealed partial class PdfViewer
{
    private bool _freehandCapture;
    private uint? _capturePointer;
    private Point _captureStart, _captureMouse;
    private DispatcherQueueTimer? _captureTimer;
    private CancellationTokenSource? _captureExport;
    private readonly TranslateTransform _captureOffset = new();
    private static readonly Lazy<InputCursor> CaptureCursor = new(() => InputSystemCursor.Create(InputSystemCursorShape.Cross));
    internal bool? ScreenshotMode => ScreenshotOverlay.Visibility == Visibility.Visible || _captureExport is { IsCancellationRequested: false }
        ? _freehandCapture : null;
    internal event Action? ScreenshotModeChanged;
    internal event Action<string?>? ScreenshotFinished;

    internal void StartScreenshot(bool freehand)
    {
        CancelScreenshot();
        if (_document == null || _layout == null) return;
        _freehandCapture = freehand;
        CaptureHint.Text = freehand ? UiText.Current.FreehandCaptureHint : UiText.Current.RectangleCaptureHint;
        CaptureInput.SetDocumentCursor(CaptureCursor.Value);
        CaptureOutline.Points = new PointCollection();
        CaptureOutline.RenderTransform = _captureOffset;
        ScreenshotOverlay.Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
        ScreenshotOverlay.Visibility = Visibility.Visible;
        ScreenshotModeChanged?.Invoke();
    }

    internal bool CancelScreenshot()
    {
        bool active = ScreenshotMode.HasValue;
        _captureExport?.Cancel();
        if (ScreenshotOverlay.Visibility != Visibility.Visible)
        {
            if (active) ScreenshotModeChanged?.Invoke();
            return active;
        }
        HideCaptureOverlay();
        ScreenshotModeChanged?.Invoke();
        return true;
    }

    private void HideCaptureOverlay()
    {
        _capturePointer = null;
        _captureTimer?.Stop();
        CaptureInput.ReleasePointerCaptures();
        ScreenshotOverlay.Visibility = Visibility.Collapsed;
        CaptureOutline.Points.Clear();
    }

    private void CaptureCancel_Click(object sender, RoutedEventArgs e) => CancelScreenshot();

    private Point CaptureDocumentPoint(Point position)
    {
        var viewport = new Point(Math.Clamp(position.X, 0, CaptureInput.ActualWidth),
                                 Math.Clamp(position.Y, 0, CaptureInput.ActualHeight));
        var point = CaptureInput.TransformToVisual(DocumentExtent).TransformPoint(viewport);
        return new Point(Math.Clamp(point.X, 0, _layout!.TotalWidth), Math.Clamp(point.Y, 0, _layout.TotalHeight));
    }

    private void Capture_Pressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var point = e.GetCurrentPoint(CaptureInput);
        if (_capturePointer != null || !point.Properties.IsLeftButtonPressed || _layout == null ||
            !CaptureInput.CapturePointer(e.Pointer)) return;
        CaptureHint.Text = _freehandCapture ? UiText.Current.FreehandCaptureHint : UiText.Current.RectangleCaptureHint;
        _capturePointer = e.Pointer.PointerId;
        _captureMouse = point.Position;
        _captureStart = CaptureDocumentPoint(_captureMouse);
        CaptureOutline.Points.Clear();
        CaptureOutline.Points.Add(new Point(0, 0));
        UpdateCapturePreview();
        if (_captureTimer == null)
        {
            _captureTimer = DispatcherQueue.CreateTimer();
            _captureTimer.Interval = TimeSpan.FromMilliseconds(32);
            _captureTimer.Tick += (_, _) => ScrollCaptureEdge();
        }
        _captureTimer.Start();
    }

    private void Capture_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_capturePointer != e.Pointer.PointerId) return;
        e.Handled = true;
        if (_freehandCapture)
        {
            var samples = e.GetIntermediatePoints(CaptureInput);
            for (int i = samples.Count - 1; i >= 0; i--)
            {
                _captureMouse = samples[i].Position;
                UpdateCapturePreview();
            }
        }
        else
        {
            _captureMouse = e.GetCurrentPoint(CaptureInput).Position;
            UpdateCapturePreview();
        }
    }

    private void PositionCaptureOutline()
    {
        var origin = DocumentExtent.TransformToVisual(CaptureInput).TransformPoint(_captureStart);
        _captureOffset.X = origin.X;
        _captureOffset.Y = origin.Y;
    }

    private void UpdateCapturePreview()
    {
        PositionCaptureOutline();
        var point = CaptureDocumentPoint(_captureMouse);
        var delta = new Point(point.X - _captureStart.X, point.Y - _captureStart.Y);
        var points = CaptureOutline.Points;
        if (_freehandCapture)
        {
            var last = points[points.Count - 1];
            if (Math.Abs(delta.X - last.X) + Math.Abs(delta.Y - last.Y) >= 1) points.Add(delta);
        }
        else
        {
            points.Clear();
            points.Add(new Point(0, 0));
            points.Add(new Point(delta.X, 0));
            points.Add(delta);
            points.Add(new Point(0, delta.Y));
        }
    }

    private void ScrollCaptureEdge()
    {
        if (_capturePointer == null) return;
        static double Step(double position, double size) =>
            Math.Clamp(position - (size - 24), 0, 28) - Math.Clamp(24 - position, 0, 28);
        var dx = Step(_captureMouse.X, CaptureInput.ActualWidth);
        var dy = Step(_captureMouse.Y, CaptureInput.ActualHeight);
        if (dx != 0 || dy != 0)
            Scroller.ChangeView(Scroller.HorizontalOffset + dx, Scroller.VerticalOffset + dy, null, true);
        UpdateCapturePreview();
    }

    private void Capture_Wheel(object sender, PointerRoutedEventArgs e)
    {
        var wheel = e.GetCurrentPoint(CaptureInput).Properties;
        double delta = wheel.MouseWheelDelta * 0.75;
        if (wheel.IsHorizontalMouseWheel) Scroller.ChangeView(Scroller.HorizontalOffset + delta, null, null, true);
        else Scroller.ChangeView(null, Scroller.VerticalOffset - delta, null, true);
        e.Handled = true;
    }

    private void Capture_Lost(object sender, PointerRoutedEventArgs e)
    {
        if (_capturePointer == e.Pointer.PointerId) CancelScreenshot();
    }

    private async void Capture_Released(object sender, PointerRoutedEventArgs e)
    {
        if (_capturePointer != e.Pointer.PointerId) return;
        e.Handled = true;
        _captureMouse = e.GetCurrentPoint(CaptureInput).Position;
        UpdateCapturePreview();
        _capturePointer = null;
        _captureTimer?.Stop();
        CaptureInput.ReleasePointerCaptures();
        await CopyCaptureAsync();
    }

    private async Task CopyCaptureAsync()
    {
        if (_document is not { } document || _layout is not { } layout || ScreenshotOverlay.Visibility != Visibility.Visible) return;
        var points = CaptureOutline.Points.Select(p => new Point(_captureStart.X + p.X, _captureStart.Y + p.Y)).ToArray();
        bool freehand = _freehandCapture;
        if (points.Length < 3) { CancelScreenshot(); return; }
        using var export = new CancellationTokenSource();
        _captureExport = export;
        HideCaptureOverlay();
        double scale = DisplayScale;
        bool night = _nightMode;
        var surround = ((SolidColorBrush)Scroller.Background).Color;
        try
        {
            using var png = await RunOnRenderThreadAsync(PdfWorkPriority.Interactive,
                () => CanvasScreenshot.Render(document, layout, points, freehand, scale, surround, night, export.Token));
            if (png == null || export.IsCancellationRequested || document != _document || !IsLoaded) return;
            var package = new DataPackage();
            using var bitmap = freehand ? await CanvasScreenshot.WhiteBitmapAsync(png) : null;
            if (export.IsCancellationRequested || document != _document || !IsLoaded) return;
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(bitmap ?? png));
            package.SetData("PNG", png);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ScreenshotFinished?.Invoke(null);
        }
        catch (Exception ex)
        {
            if (!export.IsCancellationRequested && IsLoaded) ScreenshotFinished?.Invoke(ex.Message);
        }
        finally
        {
            if (_captureExport == export)
            {
                _captureExport = null;
                ScreenshotModeChanged?.Invoke();
            }
        }
    }
}
