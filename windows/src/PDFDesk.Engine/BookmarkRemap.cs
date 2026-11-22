namespace PDFDesk.Engine;

/// <summary>
/// Pure index arithmetic for keeping user bookmarks (or anything page-indexed)
/// correct across page mutations. Mirrors the semantics of the page-editing
/// operations: delete, insert-at, and FPDF_MovePages (moved block lands at
/// destIndex in the FINAL ordering).
/// </summary>
public static class BookmarkRemap
{
    public static List<PDFDesk.Services.BookmarkEntry> ReplayPageEdit(
        IReadOnlyList<PDFDesk.Services.BookmarkEntry> current,
        IReadOnlyList<PDFDesk.Services.BookmarkEntry> destination,
        Func<int, int?> map, Func<int, int?> inverse)
    {
        // Only pages reintroduced by this operation need a historical bookmark.
        var result = destination.Where(b => inverse(b.PageIndex) is null).ToList();
        foreach (var bookmark in current)
            if (map(bookmark.PageIndex) is int page)
                result.Add(new() { PageIndex = page, Name = bookmark.Name });
        return result.OrderBy(b => b.PageIndex).ToList();
    }

    public static int? FindSavedPage(int current, int originalCount, IReadOnlyList<Func<int, int?>> maps)
    {
        for (int original = 0; original < originalCount; original++)
        {
            int? page = original;
            foreach (var map in maps)
            {
                if (!page.HasValue) break;
                page = map(page.Value);
            }
            if (page == current) return original;
        }
        return null;
    }

    // Remote edits arrive in the saved document's coordinates. Apply their delta
    // to the local working copy, keeping a local edit on a same-bookmark conflict.
    public static List<PDFDesk.Services.BookmarkEntry> Merge(
        IReadOnlyList<PDFDesk.Services.BookmarkEntry> baseline,
        IReadOnlyList<PDFDesk.Services.BookmarkEntry> incoming,
        IReadOnlyList<PDFDesk.Services.BookmarkEntry> local,
        IReadOnlyList<Func<int, int?>> maps)
    {
        var original = baseline.ToDictionary(b => b.PageIndex, b => b.Name);
        var remote = incoming.ToDictionary(b => b.PageIndex, b => b.Name);
        var merged = local.ToDictionary(b => b.PageIndex, b => b.Name);
        foreach (int page in original.Keys.Union(remote.Keys))
        {
            string? before = original.GetValueOrDefault(page), after = remote.GetValueOrDefault(page);
            if (before == after) continue;
            int? current = page;
            foreach (var map in maps)
                if (current is int i) current = map(i);
            if (current is not int target || merged.GetValueOrDefault(target) != before) continue;
            if (after is null) merged.Remove(target);
            else merged[target] = after;
        }
        return merged.OrderBy(p => p.Key).Select(p => new PDFDesk.Services.BookmarkEntry { PageIndex = p.Key, Name = p.Value }).ToList();
    }

    /// <summary>New index of a page after deleting <paramref name="deletedPages"/>, or null if it was deleted.</summary>
    public static int? AfterDelete(int page, IReadOnlyCollection<int> deletedPages)
    {
        if (deletedPages.Contains(page))
        {
            return null;
        }
        return page - deletedPages.Count(d => d < page);
    }

    /// <summary>New index of a page after inserting <paramref name="count"/> pages at <paramref name="insertAt"/>.</summary>
    public static int AfterInsert(int page, int insertAt, int count)
        => page >= insertAt ? page + count : page;

    /// <summary>New index of a page after a move (see <see cref="MovePermutation"/>).</summary>
    public static int AfterMove(int page, int pageCount, IReadOnlyList<int> movedPages, int destIndex)
        => MovePermutation(pageCount, movedPages, destIndex)[page];

    /// <summary>
    /// old-index → new-index map for moving <paramref name="movedPages"/>
    /// (order preserved) so the block starts at <paramref name="destIndex"/>
    /// in the final ordering — the same contract as FPDF_MovePages.
    /// </summary>
    public static int[] MovePermutation(int pageCount, IReadOnlyList<int> movedPages, int destIndex)
    {
        var moved = movedPages.Distinct().Where(i => i >= 0 && i < pageCount).OrderBy(i => i).ToList();
        var movedSet = new HashSet<int>(moved);
        var rest = new List<int>(pageCount - moved.Count);
        for (int i = 0; i < pageCount; i++)
        {
            if (!movedSet.Contains(i))
            {
                rest.Add(i);
            }
        }

        destIndex = Math.Clamp(destIndex, 0, rest.Count);
        var final = new List<int>(pageCount);
        final.AddRange(rest.Take(destIndex));
        final.AddRange(moved);
        final.AddRange(rest.Skip(destIndex));

        var map = new int[pageCount];
        for (int newIndex = 0; newIndex < final.Count; newIndex++)
        {
            map[final[newIndex]] = newIndex;
        }
        return map;
    }
}
