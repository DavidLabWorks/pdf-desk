using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace PDFDesk.Controls;

public sealed partial class FolderNameDialog : ContentDialog
{
    internal bool Submitted { get; private set; }

    internal FolderNameDialog(bool renaming)
    {
        InitializeComponent();
        Heading.Text = renaming ? UiText.Current.RenameFolder : UiText.Current.NewFavoriteFolder;
        AutomationProperties.SetName(this, Heading.Text);
        Submit.Content = renaming ? UiText.Current.Save : UiText.Current.Create;
        IsPrimaryButtonEnabled = false;
    }

    internal void SetEditor(StackPanel panel, TextBox box)
    {
        EditorHost.Content = panel;
        Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        box.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter || !IsPrimaryButtonEnabled) return;
            args.Handled = true;
            SubmitName();
        };
    }

    private void SubmitName()
    {
        Submitted = true;
        Hide();
    }

    private void Submit_Click(object sender, RoutedEventArgs args) => SubmitName();
    private void Cancel_Click(object sender, RoutedEventArgs args) => Hide();
}
