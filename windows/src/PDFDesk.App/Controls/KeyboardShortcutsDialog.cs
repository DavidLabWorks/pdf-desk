using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PDFDesk.Services;

namespace PDFDesk.Controls;

internal static class KeyboardShortcutsDialog
{
    internal static async Task ShowAsync(FrameworkElement owner)
    {
        var categories = new ComboBox { MinWidth = 164 };
        AutomationProperties.SetName(categories, UiText.Translate("Shortcut category"));
        foreach (var group in ShortcutCatalog.Groups)
            categories.Items.Add(UiText.Translate(group.Title));

        var rows = new StackPanel { Spacing = 4, Padding = new Thickness(0, 0, 14, 0) };
        var scroll = new ScrollViewer
        {
            Height = Math.Clamp(owner.ActualHeight - 250, 140, 310),
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = rows,
        };
        var keyStyle = (Style)Application.Current.Resources["ShortcutKeyChipStyle"];
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        categories.SelectionChanged += (_, _) =>
        {
            if (categories.SelectedIndex < 0) return;
            rows.Children.Clear();
            foreach (var shortcut in ShortcutCatalog.Groups[categories.SelectedIndex].Shortcuts)
            {
                var row = new Grid { ColumnSpacing = 12, MinHeight = 31 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock
                {
                    Text = UiText.Translate(shortcut.Name), TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var keys = new Border
                {
                    Style = keyStyle, VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = UiText.Translate(shortcut.Keys), Style = caption },
                };
                Grid.SetColumn(keys, 1);
                row.Children.Add(keys);
                rows.Children.Add(row);
            }
            scroll.ChangeView(null, 0, null, true);
        };
        categories.SelectedIndex = 0;
        var content = new StackPanel { Width = 446, Spacing = 12 };
        content.Children.Add(categories);
        content.Children.Add(new Border { Style = (Style)Application.Current.Resources["FlyoutSeparatorStyle"] });
        content.Children.Add(scroll);
        await DialogHost.ShowAsync(owner, new ContentDialog
        {
            Title = UiText.Current.KeyboardShortcuts,
            Content = content,
            CloseButtonText = UiText.Current.Close,
            CloseButtonStyle = (Style)Application.Current.Resources["DocumentPropertiesCloseStyle"],
        });
    }
}
