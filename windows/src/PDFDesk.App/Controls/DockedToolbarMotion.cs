using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace PDFDesk.Controls;

internal sealed class DockedToolbarMotion(Border surface, ScrollViewer tools, Button handle)
{
    private readonly TranslateTransform _offset = new();
    private Storyboard? _transition;

    internal void SetExpanded(bool expanded, double fullWidth, double compactWidth,
        CornerRadius fullRadius, CornerRadius dockRadius)
    {
        double width = surface.ActualWidth;
        double inset = _transition is null ? surface.Margin.Right : -_offset.X;
        double contentOpacity = tools.Visibility == Visibility.Visible ? tools.Opacity : 0;
        double handleOpacity = handle.Visibility == Visibility.Visible ? handle.Opacity : 0;
        _transition?.Stop();
        _transition = null;

        double targetWidth = expanded ? fullWidth : compactWidth;
        double targetInset = expanded ? 16 : 0;
        void Finish()
        {
            surface.Width = targetWidth;
            surface.Margin = new Thickness(8, 4, targetInset, 16);
            surface.Padding = new Thickness(expanded ? 3 : 0, 0, expanded ? 3 : 0, 0);
            surface.CornerRadius = expanded ? fullRadius : dockRadius;
            _offset.X = 0;
            tools.ClearValue(FrameworkElement.WidthProperty);
            tools.HorizontalAlignment = HorizontalAlignment.Stretch;
            tools.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            handle.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            tools.Opacity = handle.Opacity = 1;
        }

        tools.IsHitTestVisible = expanded;
        handle.IsHitTestVisible = !expanded;
        if (width <= 0 || !new UISettings().AnimationsEnabled)
        {
            Finish();
            return;
        }

        surface.RenderTransform = _offset;
        surface.Margin = new Thickness(8, 4, 0, 16);
        surface.Padding = new Thickness(0);
        surface.CornerRadius = fullRadius;
        // Keep the controls at their measured size while the rounded surface reveals them.
        tools.Width = fullWidth - 6;
        tools.HorizontalAlignment = HorizontalAlignment.Center;
        handle.HorizontalAlignment = HorizontalAlignment.Right;
        handle.Width = compactWidth;
        tools.Opacity = contentOpacity;
        handle.Opacity = handleOpacity;
        tools.Visibility = handle.Visibility = Visibility.Visible;
        var transition = new Storyboard();
        Add(surface, "Width", width, targetWidth, 195, dependent: true);
        Add(_offset, "X", -inset, -targetInset, 195);
        Add(tools, "Opacity", contentOpacity, expanded ? 1 : 0, expanded ? 135 : 75, expanded ? 60 : 0);
        Add(handle, "Opacity", handleOpacity, expanded ? 0 : 1, expanded ? 60 : 85, expanded ? 0 : 110);
        _transition = transition;
        transition.Completed += (_, _) =>
        {
            if (_transition != transition) return;
            transition.Stop();
            _transition = null;
            Finish();
        };
        transition.Begin();

        void Add(DependencyObject target, string property, double from, double to, int duration,
            int delay = 0, bool dependent = false)
        {
            var animation = new DoubleAnimation
            {
                From = from, To = to, Duration = TimeSpan.FromMilliseconds(duration),
                BeginTime = TimeSpan.FromMilliseconds(delay), EnableDependentAnimation = dependent,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            transition.Children.Add(animation);
        }
    }
}
