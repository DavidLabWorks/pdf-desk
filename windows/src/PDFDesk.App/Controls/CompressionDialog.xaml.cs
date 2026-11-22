using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFDesk.Services;

namespace PDFDesk.Controls;

public sealed partial class CompressionDialog : ContentDialog
{
    private readonly Func<string, bool> _isReserved;
    private readonly string _source;
    private XamlRoot? _hostRoot;
    private readonly string _baseName;
    private readonly nint _owner;
    internal string CopyPath { get; private set; } = "";
    private readonly long _currentBytes;
    private CancellationTokenSource? _preview;
    private readonly Func<CancellationToken, Task<int>> _prepare;
    internal int PageCount { get; private set; }
    internal bool ExportRequested { get; private set; }
    private int _measuredPreset = -1;
    private string? _measuredPath;
    private Task _work = Task.CompletedTask;
    private readonly CancellationTokenSource _estimateCancellation = new();
    private Task _estimation = Task.CompletedTask;
    private long[]? _estimates;
    private bool _estimateFinished;
    internal Task Finished => Task.WhenAll(_work, _estimation);
    internal string? ResultPath { get; private set; }
    internal int SelectedPreset => Preset.Items.IndexOf(Preset.SelectedItem);
    internal bool ReplaceOriginal => Replace.IsChecked == true;
    internal bool AutoOpen => OpenAfterExport.IsChecked == true;

    internal static string Local(string en, string zh) => UiText.Current.LanguageTag == "zh-CN" ? zh : en;
    private static string Size(long n) => n >= 1048576 ? $"{n / 1048576d:N2} MB" :
        n >= 1024 ? $"{n / 1024d:N1} KB" : $"{n} B";

    internal CompressionDialog(string path, string snapshot, long diskBytes, nint owner, Func<CancellationToken, Task<int>> prepare, Func<string, bool> isReserved)
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _hostRoot = XamlRoot;
            FitDialogWidth(_hostRoot);
            _hostRoot.Changed += RootSizeChanged;
        };
        Unloaded += (_, _) =>
        {
            if (_hostRoot is { } root) root.Changed -= RootSizeChanged;
            _hostRoot = null;
        };
        _prepare = prepare;
        _isReserved = isReserved;
        _owner = owner;
        _baseName = Path.GetFileNameWithoutExtension(path);
        DirectoryText.Text = Path.GetDirectoryName(Path.GetFullPath(path))!;
        DirectoryLabel.Text = Local("Save to", "保存目录");
        BrowseFolder.Content = Local("Browse", "更改目录");
        _source = snapshot;
        _currentBytes = diskBytes;
        FileLabel.Text = Path.GetFileName(path);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Preset, Local("Compression level", "压缩档位"));
        Smallest.Text = Local("Smallest file", "极致压缩");
        Balanced.Text = Local("Balanced", "均衡");
        Quality.Text = Local("High quality", "高质量");
        InputLabel.Text = Local("Current size", "当前大小");
        OutputLabel.Text = Local("Export size", "导出大小");
        InputSize.Text = Size(diskBytes);
        OpenAfterExport.Content = Local("Open in PDF Desk after compression", "压缩后自动打开");
        Replace.Content = Local("Replace the current file and reload it", "替换当前文件并重新加载");
        Replace.Checked += (_, _) => UpdateAction();
        Replace.Unchecked += (_, _) => UpdateAction();
        UpdateAction();
        CalculateSizeButton.Content = Local("Calculate exact size", "计算准确大小");
        ShowSelection();
        CalculateSizeButton.Click += (_, _) => StartPreview(false);
        Preset.SelectionChanged += (_, _) => ShowSelection();
        PrimaryButtonClick += (_, args) =>
        {
            if (!ReplaceOriginal || ResultPath is not null) return;
            args.Cancel = true;
            StartPreview(true);
        };
        Opened += (_, _) => _estimation = EstimateAsync(path);
        Closed += (_, _) =>
        {
            _estimateCancellation.Cancel();
            _preview?.Cancel();
        };
    }

    private async Task EstimateAsync(string path)
    {
        try
        {
            _estimateCancellation.CancelAfter(TimeSpan.FromSeconds(6));
            _estimates = await Task.Run(() => PdfCompression.EstimateAsync(path, Path.GetDirectoryName(_source)!, _estimateCancellation.Token));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException) ErrorLog.Default.Write("Compression estimate", ex);
        }
        finally
        {
            _estimateFinished = true;
            if (_preview is null && ResultPath is null) ShowSelection();
        }
    }

    private void RootSizeChanged(XamlRoot sender, XamlRootChangedEventArgs args) => FitDialogWidth(sender);

    private void FitDialogWidth(XamlRoot root)
    {
        // Leave room for the dialog padding and the margin around its surface.
        DialogBody.Width = Math.Clamp(root.Size.Width - 96, 0, DialogBody.MaxWidth);
    }

    private void UpdateAction()
    {
        PrimaryButtonText = ReplaceOriginal ? Local("Replace file", "替换文件") : Local("Save copy", "保存副本");
        DestinationPanel.Visibility = ReplaceOriginal ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (await FilePickerHost.PickFolderAsync(_owner) is { } folder)
            {
                DirectoryText.Text = folder.Path;
                UpdateCopyPath();
            }
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private void UpdateCopyPath()
    {
        string suffix = Preset.SelectedItem == Smallest ? Local("smallest", "极致压缩")
            : Preset.SelectedItem == Quality ? Local("high-quality", "高质量") : Local("balanced", "均衡压缩");
        string name = PdfLibrary.FitFileName(_baseName, $"-{suffix}.pdf");
        for (int n = 2; Path.Exists(Path.Combine(DirectoryText.Text, name)) || _isReserved(Path.Combine(DirectoryText.Text, name)); n++)
            name = PdfLibrary.FitFileName(_baseName, $"-{suffix} ({n}).pdf");
        CopyPath = Path.Combine(DirectoryText.Text, name);
        CopyName.Text = name;
        ToolTipService.SetToolTip(DirectoryText, DirectoryText.Text);
    }

    private void ShowSelection()
    {
        UpdateCopyPath();
        int preset = Preset.Items.IndexOf(Preset.SelectedItem);
        ResultPath = preset == _measuredPreset ? _measuredPath : null;
        if (ResultPath is not null)
        {
            ShowSize(ResultPath);
            return;
        }
        OutputLabel.Text = Local("Estimated size", "预计大小");
        OutputSize.Visibility = Visibility.Visible;
        CalculateSizeButton.Visibility = Visibility.Visible;
        if (_estimates is not null && preset >= 0)
        {
            long estimate = _estimates[preset];
            OutputSize.Text = "≈ " + Size(estimate);
            double reduction = Math.Clamp(1 - (double)estimate / _currentBytes, 0, 1);
            Status.Text = Local("Estimated reduction", "预计减少") + $" {reduction:P0}" + Local(" · Image sample", " · 图片抽样估算");
        }
        else
        {
            OutputSize.Text = "—";
            Status.Text = _estimateFinished
                ? Local("A reliable estimate is unavailable for this PDF.", "此文件无法快速估算，可计算准确大小。")
                : Local("Sampling images…", "正在抽样估算…");
        }
        Replace.IsEnabled = true;
    }

    private void ShowSize(string path)
    {
        OutputLabel.Text = Local("Export size", "导出大小");
        long bytes = new FileInfo(path).Length;
        OutputSize.Text = Size(bytes);
        Status.Text = bytes < _currentBytes
            ? Local("Saves ", "节省 ") + $"{Size(_currentBytes - bytes)} ({1 - (double)bytes / _currentBytes:P0})"
            : Local("This preset offers no size reduction. You can still save a copy.", "此档位无法进一步减小文件，可另存副本。");
        Replace.IsEnabled = bytes < _currentBytes;
        if (!Replace.IsEnabled) Replace.IsChecked = false;
        CalculateSizeButton.Visibility = Visibility.Collapsed;
        OutputSize.Visibility = Visibility.Visible;
    }

    private void StartPreview(bool export)
    {
        if (_preview != null) return;
        _estimateCancellation.Cancel();
        OutputLabel.Text = Local("Export size", "导出大小");
        var current = new CancellationTokenSource();
        _preview = current;
        IsPrimaryButtonEnabled = false;
        Preset.IsEnabled = false;
        BrowseFolder.IsEnabled = false;
        Replace.IsEnabled = false;
        CalculateSizeButton.Visibility = Visibility.Collapsed;
        OutputSize.Visibility = Visibility.Visible;
        ResultPath = null;
        OutputSize.Text = "—";
        Progress.Visibility = Visibility.Visible;
        Status.Text = Local("Compressing PDF…", "正在压缩 PDF…");
        _work = PreviewAsync(export, current, Preset.Items.IndexOf(Preset.SelectedItem));
    }

    private async Task PreviewAsync(bool export, CancellationTokenSource current, int preset)
    {
        string candidate = Path.Combine(Path.GetDirectoryName(_source)!, "compressed.pdf");
        try
        {
            bool replacing = ReplaceOriginal;
            if (PageCount == 0) PageCount = await _prepare(current.Token);
            current.Token.ThrowIfCancellationRequested();
            await Task.Run(() => PdfCompression.GenerateAsync(_source, candidate, preset, current.Token));
            current.Token.ThrowIfCancellationRequested();
            long bytes = new FileInfo(candidate).Length;
            long baseline = new FileInfo(_source).Length;
            ResultPath = bytes < baseline ? candidate : _source;
            _measuredPreset = preset;
            _measuredPath = ResultPath;
            ShowSize(ResultPath);
            if (export && (!replacing || ReplaceOriginal))
            {
                ExportRequested = true;
                Hide();
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!current.IsCancellationRequested)
                Status.Text = Local("Compression failed. ", "压缩失败。") + ex.Message;
        }
        finally
        {
            Preset.IsEnabled = true;
            BrowseFolder.IsEnabled = true;
            IsPrimaryButtonEnabled = true;
            if (ResultPath is null && !current.IsCancellationRequested)
            {
                _measuredPreset = -1;
                Replace.IsEnabled = true;
                CalculateSizeButton.Visibility = Visibility.Visible;
                OutputSize.Visibility = Visibility.Collapsed;
            }
            if (ReferenceEquals(_preview, current))
            {
                Progress.Visibility = Visibility.Collapsed;
                _preview = null;
            }
            current.Dispose();
        }
    }
}
