using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace PDFDesk.Controls;

public sealed partial class FavoritePickerDialog : ContentDialog
{
    private TextBox _folderName = null!;
    internal ContentDialogResult Choice { get; private set; }
    internal FavoriteLocation? Location => LocationsList.SelectedItem as FavoriteLocation;
    internal bool Creating => NameEditor.Visibility == Visibility.Visible;

    internal FavoritePickerDialog(RecentCard card, IReadOnlyList<FavoriteLocation> locations, Guid current,
        DataTemplate template)
    {
        InitializeComponent();
        Heading.Text = card.IsFavorite ? UiText.Current.MoveFavorite : UiText.Current.AddToFavorites;
        AutomationProperties.SetName(this, Heading.Text);
        DocumentName.Text = card.DisplayName;
        ToolTipService.SetToolTip(DocumentName, card.Path);
        Unfavorite.Visibility = card.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
        LocationsList.ItemTemplate = template;
        LocationsList.ItemsSource = locations;
        LocationsList.SelectedItem = locations.FirstOrDefault(row => row.Id == current) ?? locations[0];
    }

    private void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        ResizeBody();
        XamlRoot.Changed += Root_Changed;
        Body.UpdateLayout();
        LocationsList.ScrollIntoView(LocationsList.SelectedItem, ScrollIntoViewAlignment.Leading);
    }

    private void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args) =>
        XamlRoot.Changed -= Root_Changed;

    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeBody();

    private void ResizeBody()
    {
        double rowHeight = (double)Application.Current.Resources["HeaderHeight"];
        double maxHeight = (double)Application.Current.Resources["FavoritePickerMaxHeight"];
        double windowHeight = XamlRoot.Size.Height;
        // On short windows keep enough space for the inline name editor and footer.
        double available = Math.Min(Math.Max(356, windowHeight * 0.70), Math.Max(0, windowHeight - 64));
        double desiredHeight = 208 + Math.Max(5, LocationsList.Items.Count) * rowHeight;
        Body.Height = Math.Min(desiredHeight, Math.Min(maxHeight, available));
    }

    internal void SetNameEditor(StackPanel editor, TextBox box)
    {
        NameEditor.Content = editor;
        _folderName = box;
    }

    private void Location_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (Location == null) return;
        NameEditor.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = true;
    }

    private void NewFolder_Click(object sender, RoutedEventArgs args)
    {
        if (!Creating)
        {
            LocationsList.SelectedItem = null;
            NameEditor.Visibility = Visibility.Visible;
            _folderName.Text = "";
            IsPrimaryButtonEnabled = false;
        }
        // Finish the button click and arrange the newly visible editor before moving focus.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || !Creating) return;
            NameEditor.UpdateLayout();
            _folderName.Focus(FocusState.Keyboard);
        });
    }

    private void Dialog_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && ReferenceEquals(args.OriginalSource, _folderName) && IsPrimaryButtonEnabled)
        {
            args.Handled = true;
            Finish(ContentDialogResult.Primary);
        }
    }

    private void Finish(ContentDialogResult result)
    {
        Choice = result;
        Hide();
    }

    private void Save_Click(object sender, RoutedEventArgs args) => Finish(ContentDialogResult.Primary);
    private void Unfavorite_Click(object sender, RoutedEventArgs args) => Finish(ContentDialogResult.Secondary);
    private void Cancel_Click(object sender, RoutedEventArgs args) => Hide();
}
