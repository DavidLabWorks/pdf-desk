using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFDesk.Services;
using Windows.ApplicationModel;

namespace PDFDesk;

public sealed partial class MainWindow
{
    // "App" is the Application Id declared in Package.appxmanifest.
    private static string? DefaultPdfAppId =>
        AppBuildInfo.IsPackaged ? $"{Package.Current.Id.FamilyName}!App" : null;

    private void RefreshDefaultPdfStatus()
    {
        bool isDefault = DefaultPdfApp.IsDefault(DefaultPdfAppId);
        DefaultPdfStatus.Visibility = isDefault ? Visibility.Visible : Visibility.Collapsed;
        DefaultPdfHint.Visibility = SetDefaultPdfButton.Visibility = isDefault ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RemindAboutDefaultPdfAppAsync()
    {
        if (_state.Settings.SuppressDefaultPdfReminder || DefaultPdfApp.IsDefault(DefaultPdfAppId))
        {
            return;
        }

        var ui = UiText.Current;
        var optOut = new CheckBox { Content = ui.DontRemindAgain };
        var content = new StackPanel { Spacing = 12, MaxWidth = 330 };
        content.Children.Add(new TextBlock
        {
            Text = ui.DefaultPdfAppPromptBody,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(optOut);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri("ms-appx:///Assets/Square44x44Logo.targetsize-256.png")),
            Width = 28, Height = 28,
        });
        header.Children.Add(new TextBlock
        {
            Text = ui.DefaultPdfAppPromptTitle, FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var dialog = new ContentDialog
        {
            Title = header,
            Content = content,
            PrimaryButtonText = ui.OpenWindowsSettings,
            CloseButtonText = ui.RemindLater,
            DefaultButton = ContentDialogButton.Primary,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, ui.DefaultPdfAppPromptTitle);
        var result = await ShowDialogAsync(dialog);
        if (optOut.IsChecked == true)
        {
            _state.Settings.SuppressDefaultPdfReminder = true;
            _store.Save(_state);
        }
        if (result == ContentDialogResult.Primary)
        {
            await OpenDefaultPdfSettingsAsync();
        }
    }

    private async void SetDefaultPdfApp_Click(object sender, RoutedEventArgs e) => await OpenDefaultPdfSettingsAsync();

    private async Task OpenDefaultPdfSettingsAsync()
    {
        try
        {
            if (await DefaultPdfApp.OpenSettingsAsync(DefaultPdfAppId))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Default.Write(nameof(OpenDefaultPdfSettingsAsync), ex);
        }
        ShowError(UiText.Current.DefaultAppsSettingsFailed);
    }
}
