using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static PDFDesk.Controls.CompressionDialog;

namespace PDFDesk.Controls;

public sealed partial class ExportProgressCard : UserControl
{
    private readonly CancellationTokenSource _cancellation;
    internal string StatusText => ExportStage.Text;

    public ExportProgressCard(string destination, CancellationTokenSource cancellation)
    {
        InitializeComponent();
        _cancellation = cancellation;
        ExportFileName.Text = Path.GetFileName(destination);
        ToolTipService.SetToolTip(ExportFileName, destination);
        ExportStage.Text = Local("Queued", "等待中");
    }

    public void Preparing()
    {
        ExportStage.Text = Local("Preparing file", "正在准备文件");
        ExportProgress.IsIndeterminate = true;
    }

    public void Report(int value)
    {
        if (_cancellation.IsCancellationRequested) return;
        ExportProgress.IsIndeterminate = false;
        ExportProgress.Value = value;
        ExportStage.Text = Local("Compressing", "正在压缩") + $" · {value}%";
    }

    public void Saving()
    {
        ExportProgress.IsIndeterminate = false;
        ExportProgress.Value = 96;
        ExportStage.Text = Local("Verifying and saving", "正在校验并保存");
    }

    private void CancelTask_Click(object sender, RoutedEventArgs args)
    {
        CancelTask.IsEnabled = false;
        ExportStage.Text = Local("Cancelling…", "正在取消…");
        _cancellation.Cancel();
    }
}
