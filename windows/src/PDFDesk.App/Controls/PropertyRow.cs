using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PDFDesk.Controls;

internal static class PropertyRow
{
    internal static Grid Create(string name, string value, bool isFont = false)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(isFont ? 210 : 100) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = name,
            FontSize = 13,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (name.Length > 0) ToolTipService.SetToolTip(label, name);
        row.Children.Add(label);

        // Selectable throughout: people open this dialog to copy a title, a
        // producer string or a path out of it.
        var text = new TextBlock
        {
            Text = value,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        if (name.Length == 0) Grid.SetColumnSpan(text, 2);
        else Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }
}
