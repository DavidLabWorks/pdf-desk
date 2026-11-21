using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PDFDesk.Controls;

public sealed partial class RecycleFilesDialog : ContentDialog
{
    internal bool Confirmed { get; private set; }

    internal RecycleFilesDialog(IReadOnlyList<RecentCard> cards)
    {
        InitializeComponent();
        Heading.Text = UiText.Current.ConfirmDeleteRecentFiles(cards.Count);
        AutomationProperties.SetName(this, Heading.Text);
        AutomationProperties.SetName(Confirm, UiText.Current.MoveToRecycleBin);
        FileList.ItemsSource = cards;
    }

    private void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args) =>
        Dismiss.Focus(FocusState.Programmatic);

    private void Dismiss_Click(object sender, RoutedEventArgs args) => Hide();

    private void Confirm_Click(object sender, RoutedEventArgs args)
    {
        Confirmed = true;
        Hide();
    }
}
