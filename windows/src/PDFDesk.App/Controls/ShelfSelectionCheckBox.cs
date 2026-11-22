using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace PDFDesk.Controls;

public sealed class ShelfSelectionCheckBox : CheckBox
{
    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(
        nameof(IsRevealed), typeof(bool), typeof(ShelfSelectionCheckBox),
        new PropertyMetadata(false, (sender, _) => ((ShelfSelectionCheckBox)sender).UpdateReveal()));

    public bool IsRevealed
    {
        get => (bool)GetValue(IsRevealedProperty);
        set => SetValue(IsRevealedProperty, value);
    }

    private Storyboard? _motion;

    public ShelfSelectionCheckBox()
    {
        Visibility = Visibility.Collapsed;
        Unloaded += (_, _) => Settle();
    }

    private void Settle()
    {
        _motion?.Stop();
        _motion = null;
        Visibility = IsRevealed ? Visibility.Visible : Visibility.Collapsed;
        Width = 32;
        Opacity = 1;
    }

    private void UpdateReveal()
    {
        IsHitTestVisible = IsTabStop = IsRevealed;
        if (!IsLoaded || !new UISettings().AnimationsEnabled)
        {
            Settle();
            return;
        }

        bool visible = Visibility == Visibility.Visible;
        double width = visible ? ActualWidth : 0;
        double opacity = visible ? Opacity : 0;
        _motion?.Stop();
        Visibility = Visibility.Visible;
        var motion = new Storyboard();
        Add("Width", width, IsRevealed ? 32 : 0);
        Add("Opacity", opacity, IsRevealed ? 1 : 0);
        _motion = motion;
        motion.Completed += (_, _) =>
        {
            if (_motion == motion) Settle();
        };
        motion.Begin();

        void Add(string property, double from, double to)
        {
            var animation = new DoubleAnimation
            {
                From = from, To = to, Duration = TimeSpan.FromMilliseconds(IsRevealed ? 180 : 135),
                EnableDependentAnimation = property == "Width",
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, this);
            Storyboard.SetTargetProperty(animation, property);
            motion.Children.Add(animation);
        }
    }
}
