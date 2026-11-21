using Microsoft.UI.Xaml;

namespace PDFDesk.Styles;

public sealed class DestructiveAction : DependencyObject
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(DestructiveAction), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);

    private static readonly Uri Palette = new("ms-appx:///Styles/DestructiveAction.xaml");

    private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var dictionaries = ((FrameworkElement)sender).Resources.MergedDictionaries;
        if ((bool)args.NewValue)
            dictionaries.Add(new ResourceDictionary { Source = Palette });
        else if (dictionaries.FirstOrDefault(dictionary => dictionary.Source == Palette) is { } palette)
            dictionaries.Remove(palette);
    }
}
