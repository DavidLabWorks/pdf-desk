using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PDFDesk.Engine;
using PDFDesk.Services;

namespace PDFDesk.Controls;

internal static class DocumentPropertiesDialog
{
    /// <summary>How many pages the font scan walks before it calls it a day.</summary>
    private const int FontScanPageCap = 50;

    /// <summary>
    /// Ctrl+D. Everything the file says about itself, in sections.
    ///
    /// The fonts arrive after the dialog does. Everything else is a dictionary
    /// lookup, but a font list means walking page objects, and making Ctrl+D sit
    /// there for a second on a long document to answer a question most people
    /// are not asking would be the wrong trade.
    /// </summary>
    internal static async Task ShowAsync(FrameworkElement owner, DocumentView view, Action<string> showError)
    {
        if (view.Viewer.Document is not { } document)
        {
            return;
        }

        int page = view.Viewer.CurrentPage;
        var sections = await view.Viewer.RunOnRenderThreadAsync(
            PdfWorkPriority.Interactive, () => document.GetDocumentProperties(page));

        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(0, 0, 16, 2) };
        foreach (var section in sections)
        {
            panel.Children.Add(BuildPropertySection(section.Title, section.Rows));
        }

        var fonts = new StackPanel { Spacing = 5 };
        panel.Children.Add(BuildPropertySection("Fonts", [("", UiText.Translate("Looking…"))], fonts));

        var heading = (Grid)((DataTemplate)Application.Current.Resources["DocumentPropertiesHeading"]).LoadContent();
        ((TextBlock)heading.FindName("FileName")).Text = view.DisplayName;
        var dialog = new ContentDialog
        {
            Title = heading,
            Content = new ScrollViewer
            {
                Width = 428,
                MaxHeight = Math.Clamp(owner.ActualHeight - 218, 136, 396),
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel,
            },
            CloseButtonText = UiText.Current.Close,
            CloseButtonStyle = (Style)Application.Current.Resources["DocumentPropertiesCloseStyle"],
            SecondaryButtonText = view.HasSignatures ? UiText.Current.SignatureDetails : "",
        };
        AutomationProperties.SetName(dialog, UiText.Current.DocumentProperties);

        _ = FillFontsAsync(view, document, fonts);
        if (await DialogHost.ShowAsync(owner, dialog) == ContentDialogResult.Secondary)
            await ShowSignatureDetailsAsync(owner, view, showError);
    }

    /// <summary>One headed group of rows, in the same shape the report dialog uses.</summary>
    private static StackPanel BuildPropertySection(
        string title, IReadOnlyList<(string Name, string Value)> rows, StackPanel? body = null)
    {
        var section = new StackPanel { Spacing = 7 };
        var heading = (Grid)((DataTemplate)Application.Current.Resources["DocumentPropertiesSectionHeading"]).LoadContent();
        ((TextBlock)heading.FindName("Label")).Text = UiText.Translate(title);
        section.Children.Add(heading);

        body ??= new StackPanel { Spacing = 5 };
        foreach (var (name, value) in rows)
        {
            body.Children.Add(PropertyRow.Create(UiText.PropertyLabel(name), UiText.PropertyValue(title, name, value)));
        }
        section.Children.Add(body);
        return section;
    }

    /// <summary>
    /// Walks the pages for fonts at Background priority, so visible tiles keep
    /// outranking it, and replaces the placeholder when it lands.
    /// </summary>
    private static async Task FillFontsAsync(DocumentView view, PdfDocument document, StackPanel body)
    {
        IReadOnlyList<FontUsage> fonts;
        try
        {
            fonts = await view.Viewer.RunOnRenderThreadAsync(
                PdfWorkPriority.Background, () => document.GetFontsUsed(FontScanPageCap));
        }
        catch
        {
            // Closed, swapped, or the scan threw. The dialog is still useful.
            body.Children.Clear();
            body.Children.Add(PropertyRow.Create("", UiText.Translate("Could not be read.")));
            return;
        }

        body.Children.Clear();
        if (fonts.Count == 0)
        {
            body.Children.Add(PropertyRow.Create("", UiText.Translate("This document draws no text.")));
            return;
        }

        foreach (var font in fonts)
        {
            string embedded = UiText.Translate(font.Embedded ? "Embedded" : "Not embedded");
            string kind = font.Kind == "—" ? "" : $", {UiText.TranslateList(font.Kind)}";
            body.Children.Add(PropertyRow.Create(font.Name, embedded + kind, isFont: true));
        }

        if (document.PageCount > FontScanPageCap)
        {
            body.Children.Add(PropertyRow.Create("", UiText.Format("Found in the first {0} pages.", FontScanPageCap)));
        }
    }

    private static async Task ShowSignatureDetailsAsync(FrameworkElement owner, DocumentView view, Action<string> showError)
    {
        if (view.Viewer.Document is not { } document)
        {
            return;
        }

        IReadOnlyList<PdfSignatureInfo> signatures;
        try
        {
            signatures = await view.Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, document.GetSignatures);
        }
        catch (Exception ex)
        {
            showError(UiText.Format("Could not read signatures: {0}", ex.Message));
            return;
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };

        // This disclaimer is load-bearing, not boilerplate. PDFDesk ships no
        // cryptography: it has not checked the certificate, the digest, trust
        // or revocation. Presenting any of the fields below as proof of
        // authenticity would mislead someone about a document they may be
        // relying on, so the limitation goes first and is never softened.
        panel.Children.Add(new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            Message = UiText.Current.SignatureVerificationExplanation,
            IsClosable = false,
            IsOpen = true,
        });

        foreach (var signature in signatures)
        {
            panel.Children.Add(new Border { Style = (Style)Application.Current.Resources["FlyoutSeparatorStyle"] });

            var rows = new StackPanel { Spacing = 4 };
            rows.Children.Add(new TextBlock
            {
                Text = UiText.Format("Signature {0}", signature.Index + 1),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });

            AddSignatureRow(rows, "Reason", string.IsNullOrWhiteSpace(signature.Reason) ? "—" : signature.Reason);
            AddSignatureRow(rows, "Signed", signature.SignedAt?.ToString("f", System.Globalization.CultureInfo.CurrentUICulture)
                ?? (string.IsNullOrWhiteSpace(signature.SignedAtRaw) ? "—" : signature.SignedAtRaw));
            AddSignatureRow(rows, "Format", string.IsNullOrWhiteSpace(signature.SubFilter) ? "—" : signature.SubFilter);
            AddSignatureRow(rows, "Coverage", UiText.Translate(signature.Coverage switch
            {
                SignatureCoverage.CoversWholeFile => "Signed byte range covers the whole file",
                SignatureCoverage.LeavesContentUnsigned => "Part of this file is outside the signed range — it was changed or added after signing",
                _ => "Could not read the signed byte range",
            }));
            if (signature.IsCertifying)
            {
                AddSignatureRow(rows, "Certified", UiText.Format("Author signature, DocMDP level {0}", signature.DocMdpPermission));
            }

            panel.Children.Add(rows);
        }

        await DialogHost.ShowAsync(owner, new ContentDialog
        {
            Title = signatures.Count == 1 ? UiText.Translate("Signature details") : UiText.Format("Signature details ({0})", signatures.Count),
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            CloseButtonText = UiText.Current.Close,
            CloseButtonStyle = (Style)Application.Current.Resources["DocumentPropertiesCloseStyle"],
        });
    }

    private static void AddSignatureRow(Panel parent, string label, string value)
    {
        parent.Children.Add(PropertyRow.Create(UiText.Translate(label), value));
    }
}
