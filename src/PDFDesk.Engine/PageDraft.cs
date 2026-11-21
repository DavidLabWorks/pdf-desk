namespace PDFDesk.Engine;

/// <summary>Page edits stay in this index model until the user saves the draft.</summary>
public sealed class PageDraft(int pageCount)
{
    private enum Kind { Move, Copy, Remove }
    private sealed record Step(Kind Kind, int[] Pages, int Destination);
    private readonly List<Step> _history = [];
    private readonly List<int> _order = [.. Enumerable.Range(0, pageCount)];
    private int _cursor;
    public int Count => _order.Count;
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor < _history.Count;
    public bool Changed => _order.Count != pageCount || _order.Where((id, i) => id != i).Any();

    // Originals retain their identity for bookmark remapping. Copies reference
    // the same source preview, including copies made from earlier copies.
    public int SourceAt(int slot) => _order[slot] >= 0 ? _order[slot] : ~_order[slot];
    public int?[] OriginalPositions()
    {
        var positions = new int?[pageCount];
        for (int i = 0; i < Count; i++)
            if (_order[i] >= 0) positions[_order[i]] = i;
        return positions;
    }

    public void Move(IReadOnlyList<int> pages, int destination) => Add(Kind.Move, pages, destination);
    public void Copy(IReadOnlyList<int> pages) => Add(Kind.Copy, pages, pages.Count == 0 ? 0 : pages.Max() + 1);
    public void Remove(IReadOnlyList<int> pages) => Add(Kind.Remove, pages, 0);

    private void Add(Kind kind, IReadOnlyList<int> selection, int destination)
    {
        int[] pages = [.. selection.Distinct().Order()];
        if (pages.Length == 0) return;
        if (pages[0] < 0 || pages[^1] >= Count) throw new ArgumentOutOfRangeException(nameof(selection));
        if (kind == Kind.Remove && pages.Length == Count) throw new InvalidOperationException("Cannot delete every page of a document.");
        if (kind == Kind.Move)
        {
            if (destination < 0 || destination > Count - pages.Length) throw new ArgumentOutOfRangeException(nameof(destination));
            if (pages[0] == destination && pages[^1] - pages[0] + 1 == pages.Length) return;
        }
        var step = new Step(kind, pages, destination);
        _history.RemoveRange(_cursor, _history.Count - _cursor);
        _history.Add(step);
        Redo();
    }

    private void Apply(Step step)
    {
        if (step.Kind == Kind.Copy)
        {
            var copies = step.Pages.Select(i => ~SourceAt(i)).ToArray();
            _order.InsertRange(step.Destination, copies);
            return;
        }
        var moved = step.Kind == Kind.Move ? step.Pages.Select(i => _order[i]).ToArray() : null;
        for (int i = step.Pages.Length - 1; i >= 0; i--) _order.RemoveAt(step.Pages[i]);
        if (moved is not null) _order.InsertRange(step.Destination, moved);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _cursor--;
        _order.Clear();
        _order.AddRange(Enumerable.Range(0, pageCount));
        for (int i = 0; i < _cursor; i++) Apply(_history[i]);
    }

    public void Redo()
    {
        if (CanRedo) Apply(_history[_cursor++]);
    }

    public PdfDocument Save(PdfDocument source)
    {
        if (source.PageCount != pageCount) throw new InvalidOperationException("The document changed while organizing pages.");
        return source.SaveEditedCopy(copy =>
        {
            foreach (var step in _history.Take(_cursor))
            {
                switch (step.Kind)
                {
                    case Kind.Move: copy.MovePages(step.Pages, step.Destination); break;
                    case Kind.Remove: copy.DeletePages(step.Pages); break;
                    case Kind.Copy: copy.InsertPages(copy.ExportPages(step.Pages), step.Destination); break;
                }
            }
        });
    }
}
