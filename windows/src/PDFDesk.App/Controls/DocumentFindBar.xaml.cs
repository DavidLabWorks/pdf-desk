using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PDFDesk.Engine;
using Windows.System;

namespace PDFDesk.Controls;

public sealed partial class DocumentFindBar : UserControl
{
    private PdfViewer? _viewer;
    private CancellationTokenSource? _searchCts;
    private List<SearchHit> _searchHits = [];
    private int _activeHitIndex = -1;

    public DocumentFindBar() => InitializeComponent();

    internal void Attach(PdfViewer? viewer)
    {
        if (_viewer != viewer && _viewer?.Document is not null) _viewer.ClearSearch();
        _viewer = viewer;
        if (Visibility == Visibility.Visible) RunSearch();
    }

    private void CancelSearch()
    {
        var previous = _searchCts;
        _searchCts = null;
        previous?.Cancel();
    }

    internal void Open()
    {
        if (_viewer?.Document is null)
        {
            return;
        }
        Visibility = Visibility.Visible;
        // Prefill from the selection BEFORE SelectAll, so typing replaces it.
        if (_viewer?.HasSelection == true)
        {
            FindBox.Text = _viewer.SelectedText.Split('\n')[0].Trim();
        }
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
        if (!string.IsNullOrEmpty(FindBox.Text))
        {
            RunSearch();
        }
    }

    internal void Close()
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }
        Visibility = Visibility.Collapsed;
        ResetResults();
    }

    internal void RefreshSearch()
    {
        if (Visibility == Visibility.Visible) RunSearch();
    }

    internal void ResetResults()
    {
        CancelSearch();
        _viewer?.ClearSearch();
        _searchHits = [];
        _activeHitIndex = -1;
        FindCount.Text = "";
    }

    private void FindClose_Click(object sender, RoutedEventArgs e) => Close();
    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => RunSearch();
    private void MatchCase_Click(object sender, RoutedEventArgs e) => RunSearch();
    private void FindNext_Click(object sender, RoutedEventArgs e) => StepHit(+1);
    private void FindPrev_Click(object sender, RoutedEventArgs e) => StepHit(-1);

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            StepHit(shift ? -1 : +1);
            e.Handled = true;
        }
    }

    private async void RunSearch()
    {
        CancelSearch();

        string query = FindBox.Text;
        var viewer = _viewer;
        var document = viewer?.Document;

        viewer?.ClearSearch();
        var collected = new List<SearchHit>();
        _searchHits = collected;
        _activeHitIndex = -1;
        FindCount.Text = "";

        if (string.IsNullOrEmpty(query) || viewer is null || document is null)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _searchCts = cts;
        try
        {
            await Task.Delay(200, token); // debounce rapid typing
            token.ThrowIfCancellationRequested();
            bool matchCase = MatchCaseButton.IsChecked == true;
            // Route each page's search through the viewer's render thread at
            // Background priority: visible tiles always outrank the sweep.
            var search = new DocumentSearch(document, query, matchCase, wholeWord: false,
                workQueue: viewer.WorkQueue);

            await search.RunAsync(
                onPageHits: hits => DispatcherQueue.TryEnqueue(() =>
                {
                    if (_searchHits != collected)
                    {
                        return; // superseded by a newer query
                    }
                    collected.AddRange(hits);
                    viewer.SetSearchResults(collected);
                    if (_activeHitIndex < 0 && collected.Count > 0)
                    {
                        _activeHitIndex = 0;
                        viewer.HighlightHit(collected[0]);
                    }
                    UpdateFindCount();
                }),
                onProgress: null,
                token);

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_searchHits == collected && collected.Count == 0)
                {
                    FindCount.Text = UiText.Current.NoResults;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded query; ignore.
        }
        finally
        {
            if (_searchCts == cts) _searchCts = null;
        }
    }

    internal void StepHit(int delta)
    {
        int n = _searchHits.Count;
        if (n == 0)
        {
            return;
        }
        _activeHitIndex = ((_activeHitIndex + delta) % n + n) % n;
        _viewer?.HighlightHit(_searchHits[_activeHitIndex]);
        UpdateFindCount();
    }

    private void UpdateFindCount()
    {
        int n = _searchHits.Count;
        FindCount.Text = n == 0 ? "" : $"{_activeHitIndex + 1} of {n}";
    }
}
