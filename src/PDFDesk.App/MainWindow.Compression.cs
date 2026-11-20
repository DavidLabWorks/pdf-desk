using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using PDFDesk.Controls;
using PDFDesk.Engine;
using PDFDesk.Services;
using static PDFDesk.Controls.CompressionDialog;

namespace PDFDesk;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, ExportProgressCard?> _compressionTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _copySlots = new(2);
    private bool _queueExpanded = true;
    private Storyboard? _queueTransition;
    private double CompactReaderPanelWidth => (double)RootGrid.Resources["CompactReaderPanelWidth"];
    private double ExpandedReaderPanelWidth => (double)RootGrid.Resources["ExpandedReaderPanelWidth"];
    private readonly HashSet<string> _exportDestinations = new(StringComparer.OrdinalIgnoreCase);

    private void CompressExport_Click(object sender, RoutedEventArgs args)
    {
        if (CurrentView is { } view) _ = CompressExportAsync(view.FilePath);
    }

    private TabViewItem? FindDocumentTab(string path) => Tabs.TabItems.OfType<TabViewItem>()
        .FirstOrDefault(t => t.Tag is DocumentView v && string.Equals(v.FilePath, path, StringComparison.OrdinalIgnoreCase));

    private static (long Bytes, DateTime Modified) FileVersion(string path)
    {
        var file = new FileInfo(path);
        return (file.Length, file.LastWriteTimeUtc);
    }

    private async Task CompressExportAsync(string path)
    {
        if (DialogHost.IsOpen) return;
        path = Path.GetFullPath(path);
        if (_compressionTasks.TryGetValue(path, out var active))
        {
            _queueExpanded = true;
            UpdateExportQueue();
            active?.StartBringIntoView();
            string status = active?.StatusText ?? Local("Export in progress", "正在导出");
            ShowNotice($"{Path.GetFileName(path)} · {status}", InfoBarSeverity.Informational,
                Local("An export is already in progress", "此文件已有压缩任务"));
            return;
        }
        _compressionTasks.Add(path, null);
        ExportProgressCard? card = null;
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        bool acquiredSlot = false;
        string? reserved = null;
        string? temporary = null;
        string? staging = null;
        var tab = FindDocumentTab(path);
        var view = tab?.Tag as DocumentView;
        bool edited = false;
        void OnEdit(object? sender, EventArgs args) => edited = true;
        try
        {
            temporary = Directory.CreateTempSubdirectory("PDFDesk-compress-").FullName;
            var version = FileVersion(path);
            string snapshot = Path.Combine(temporary, "source.pdf");
            async Task<int> PrepareAsync(CancellationToken token)
            {
                if (view is { IsDocumentLoaded: true })
                {
                    await view.Viewer.FinishEraserAsync();
                    await view.Viewer.CommitTextAsync();
                    if (view.Viewer.IsEditingText) throw new IOException(UiText.Current.TextCommitFailed);
                    if (view.Viewer.IsFormFieldFocused) view.Viewer.KillFormFocus();
                    view.Viewer.DocumentEdited -= OnEdit;
                    view.Viewer.DocumentEdited += OnEdit;
                }
                var document = view?.Viewer.Document;
                if (document is not null && !document.MatchesFileOnDisk())
                    throw new IOException(Local("The file has changed on disk. Reopen it first.", "文件已在外部更改，请重新打开。"));
                string protectedMessage = Local("Compression is unavailable for signed or encrypted PDFs.", "已数字签名或加密的 PDF 无法压缩导出。");
                if (document?.IsDirty == true)
                {
                    await view!.Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, () =>
                    {
                        if (document.SignatureCount > 0 || document.IsEncrypted) throw new IOException(protectedMessage);
                        document.SaveAs(snapshot);
                        return true;
                    });
                }
                else
                {
                    await using var input = File.OpenRead(path);
                    await using var copy = File.Create(snapshot);
                    await input.CopyToAsync(copy, token);
                }
                token.ThrowIfCancellationRequested();
                return await Task.Run(() =>
                {
                    using var input = PdfDocument.Open(snapshot);
                    if (input.SignatureCount > 0 || input.IsEncrypted) throw new IOException(protectedMessage);
                    return input.PageCount;
                });
            }
            var dialog = new CompressionDialog(path, snapshot, version.Bytes, WinRT.Interop.WindowNative.GetWindowHandle(this), PrepareAsync, _exportDestinations.Contains);
            var result = await ShowDialogAsync(dialog);
            await dialog.Finished;
            if (!dialog.ExportRequested && result != ContentDialogResult.Primary) return;
            string destination = dialog.ReplaceOriginal ? path : dialog.CopyPath;
            if (!dialog.ReplaceOriginal)
            {
                if (!_exportDestinations.Add(destination))
                    throw new IOException(Local("Another export is writing to this file.", "其他任务正在写入此文件。"));
                reserved = destination;
            }
            string? output = dialog.ResultPath;
            int pages = dialog.PageCount;
            if (!dialog.ReplaceOriginal)
            {
                card = new ExportProgressCard(destination, cancellation);
                _compressionTasks[path] = card;
                ExportTasks.Children.Add(card);
                UpdateExportQueue();
                await _copySlots.WaitAsync(token);
                acquiredSlot = true;
                token.ThrowIfCancellationRequested();
                card.Preparing();
                if (output is null)
                {
                    if (pages == 0) pages = await PrepareAsync(token);
                    var progress = new Progress<int>(card.Report);
                    token.ThrowIfCancellationRequested();
                    string candidate = Path.Combine(temporary, "compressed.pdf");
                    int preset = dialog.SelectedPreset;
                    await Task.Run(() => PdfCompression.GenerateAsync(snapshot, candidate, preset, token, progress));
                    output = new FileInfo(candidate).Length < new FileInfo(snapshot).Length ? candidate : snapshot;
                }
                token.ThrowIfCancellationRequested();
                card.Saving();
            }
            if (output is null) return;
            staging = Path.Combine(Path.GetDirectoryName(destination)!, $".pdfdesk-{Guid.NewGuid():N}.tmp");
            await Task.Run(async () =>
            {
                using (var input = File.OpenRead(output))
                using (var target = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(target, token);
                token.ThrowIfCancellationRequested();
                using var verified = PdfDocument.Open(staging);
                if (verified.PageCount != pages) throw new InvalidDataException(Local("Page count changed during export.", "导出前后页数不一致。"));
            });
            token.ThrowIfCancellationRequested();
            if (dialog.ReplaceOriginal && (FileVersion(path) != version || FindDocumentTab(path) != tab || edited))
                throw new IOException(Local("The document changed. Start the export again.", "文档已更改，请重新压缩导出。"));
            if (dialog.ReplaceOriginal) File.Replace(staging, destination, null);
            else File.Move(staging, destination);
            if (dialog.ReplaceOriginal)
            {
                _thumbnails.Forget(path);
                if (tab != null)
                {
                    int index = Tabs.TabItems.IndexOf(tab);
                    bool selected = ReferenceEquals(Tabs.SelectedItem, tab);
                    CloseTab(tab);
                    AddTab(path, _state.FindRecent(path), selected, index);
                    if (FindDocumentTab(path)?.Tag is DocumentView reopened) await LoadTabAsync(reopened);
                }
                PopulateRecents();
            }

            if (dialog.AutoOpen) await LoadDocumentAsync(destination);
            ShowNotice(destination, InfoBarSeverity.Success, Local("Compression complete", "压缩导出完成"), null,
                new NoticeAction(Local("Open folder", "打开所在文件夹"), () => _ = OpenFileLocationAsync(destination)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            if (view != null) view.Viewer.DocumentEdited -= OnEdit;
            _compressionTasks.Remove(path);
            if (reserved != null) _exportDestinations.Remove(reserved);
            if (card != null) ExportTasks.Children.Remove(card);
            UpdateExportQueue();
            if (acquiredSlot) _copySlots.Release();
            try
            {
                if (staging != null) File.Delete(staging);
                if (temporary != null) Directory.Delete(temporary, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Default.Write("Compression cleanup", ex);
            }
        }
    }

    private void ToggleExportQueue_Click(object sender, RoutedEventArgs args)
    {
        double fromWidth = ExportQueue.ActualWidth;
        double fromHeight = ExportTasksScroll.ActualHeight;
        _queueExpanded = !_queueExpanded;
        UpdateExportQueue();
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;

        ExportTasksScroll.Visibility = Visibility.Visible;
        ExportTasksScroll.Measure(new Windows.Foundation.Size(306, double.PositiveInfinity));
        var transition = new Storyboard();
        _queueTransition = transition;
        Animate(ExportQueue, "Width", fromWidth, ExportQueue.Width);
        Animate(ExportTasksScroll, "Height", fromHeight, _queueExpanded ? ExportTasksScroll.DesiredSize.Height : 0);
        transition.Completed += (_, _) =>
        {
            if (ReferenceEquals(_queueTransition, transition)) UpdateExportQueue();
        };
        transition.Begin();

        void Animate(FrameworkElement target, string property, double from, double to)
        {
            var animation = new DoubleAnimation
            {
                From = from, To = to, Duration = TimeSpan.FromMilliseconds(170),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            transition.Children.Add(animation);
        }
    }

    private void UpdateExportQueue()
    {
        _queueTransition?.Stop();
        _queueTransition = null;
        int count = ExportTasks.Children.Count;
        if (count == 0) _queueExpanded = true;
        ExportQueue.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ExportTasksScroll.Visibility = _queueExpanded ? Visibility.Visible : Visibility.Collapsed;
        ExportQueue.Width = _queueExpanded ? ExpandedReaderPanelWidth : CompactReaderPanelWidth;
        QueueLabel.Text = Local("Export queue", "导出队列") + $" · {count}";
        QueueChevron.Glyph = _queueExpanded ? "\uE70D" : "\uE70E";
    }
}
