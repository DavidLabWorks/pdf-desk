namespace PDFDesk.Engine;

/// <summary>Stores the current contents of an inserted page range while its insertion is undone.</summary>
public sealed class InsertedPages(int index, int count)
{
    private byte[]? _snapshot;

    public long SnapshotBytes => _snapshot?.LongLength ?? 0;

    public void Undo(PdfDocument document)
    {
        lock (PdfiumInterop.PdfiumLibrary.Lock)
        {
            int[] pages = [.. Enumerable.Range(index, count)];
            document.FormKillFocus();
            var current = document.ExportPages(pages);
            document.DeletePages(pages);
            _snapshot = current;
        }
    }

    public void Redo(PdfDocument document)
    {
        document.InsertPages(_snapshot ?? throw new InvalidOperationException("No insertion to redo."), index);
        _snapshot = null;
    }
}
