using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeCodeManager.App.Views;

/// <summary>
/// Themed find overlay for FlowDocumentScrollViewer. Attach via <see cref="Target"/>.
/// Highlights the exact matching text (not just the containing block) and scrolls to it.
/// Scope is limited to the current document only.
/// </summary>
public partial class FindOverlay : UserControl
{
    public FlowDocumentScrollViewer? Target { get; set; }

    private sealed class MatchLoc
    {
        public System.Windows.Documents.Block Block = null!;
        public int CharIndexInBlock;
    }

    private readonly List<MatchLoc> _matches = new();
    private int _currentIdx = -1;         // index into _matches; -1 = none active
    private string _cachedQuery = "";
    private TextPointer? _lastHighlightStart;
    private TextPointer? _lastHighlightEnd;

    public FindOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised when Find state is reset (close, query change). Views should re-render the preview
    /// document to guarantee no lingering highlight fragments from partial-range formatting.
    /// </summary>
    public event Action? DocumentResetRequested;

    public void Open()
    {
        Visibility = Visibility.Visible;
        Status.Text = "";
        // Focus needs to happen after the visibility change is applied to the visual tree.
        // Defer via Dispatcher (Render priority) so the TextBox is ready to receive keyboard input on the same Ctrl+F press.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Input.Focus();
            Keyboard.Focus(Input);
            Input.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Close()
    {
        ClearHighlight();
        _matches.Clear();
        _currentIdx = -1;
        _cachedQuery = "";
        Visibility = Visibility.Collapsed;
        DocumentResetRequested?.Invoke();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) FindPrev();
            else FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnNext(object sender, RoutedEventArgs e) => FindNext();
    private void OnPrev(object sender, RoutedEventArgs e) => FindPrev();
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void FindNext() => Step(+1);
    private void FindPrev() => Step(-1);

    private void Step(int direction)
    {
        var query = Input.Text;
        if (string.IsNullOrEmpty(query) || Target?.Document is null)
        {
            ClearHighlight();
            Status.Text = "";
            return;
        }

        // Rebuild match index if the query changed. Also fire a document reset so any lingering
        // highlight from the previous query is wiped by a fresh re-render before we re-scan.
        if (!string.Equals(query, _cachedQuery, StringComparison.Ordinal))
        {
            ClearHighlight();
            DocumentResetRequested?.Invoke();
            BuildMatchIndex(query);
            _cachedQuery = query;
            _currentIdx = -1;
        }

        if (_matches.Count == 0)
        {
            ClearHighlight();
            Status.Text = "not found";
            return;
        }

        // Advance / wrap
        if (_currentIdx < 0)
            _currentIdx = direction >= 0 ? 0 : _matches.Count - 1;
        else
            _currentIdx = ((_currentIdx + direction) % _matches.Count + _matches.Count) % _matches.Count;

        JumpToCurrent(query);
    }

    private void BuildMatchIndex(string query)
    {
        _matches.Clear();
        if (Target?.Document is null) return;
        var blocks = Flatten(Target.Document.Blocks);
        foreach (var block in blocks)
        {
            var (text, _) = BuildCharPointerMap(block);
            int idx = 0;
            while (idx <= text.Length)
            {
                var found = text.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                _matches.Add(new MatchLoc { Block = block, CharIndexInBlock = found });
                idx = found + query.Length;
            }
        }
    }

    private void JumpToCurrent(string query)
    {
        ClearHighlight();
        if (_currentIdx < 0 || _currentIdx >= _matches.Count) return;
        var m = _matches[_currentIdx];
        var (text, pointers) = BuildCharPointerMap(m.Block);
        if (m.CharIndexInBlock + query.Length > pointers.Count) { Status.Text = "stale"; return; }
        var start = pointers[m.CharIndexInBlock];
        var end = pointers[m.CharIndexInBlock + query.Length];
        Highlight(start, end);
        Status.Text = $"{_currentIdx + 1} / {_matches.Count}";
    }

    /// <summary>Walks the block's content and builds a text string + parallel list of TextPointers, one per character.</summary>
    private static (string text, List<TextPointer> pointers) BuildCharPointerMap(System.Windows.Documents.Block block)
    {
        var sb = new StringBuilder();
        var pointers = new List<TextPointer>();
        var current = block.ContentStart;
        var end = block.ContentEnd;
        while (current is not null && current.CompareTo(end) < 0)
        {
            var context = current.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                var runText = current.GetTextInRun(LogicalDirection.Forward);
                for (int i = 0; i < runText.Length; i++)
                {
                    var ptr = current.GetPositionAtOffset(i, LogicalDirection.Forward);
                    if (ptr is null) break;
                    pointers.Add(ptr);
                    sb.Append(runText[i]);
                }
                current = current.GetPositionAtOffset(runText.Length, LogicalDirection.Forward);
            }
            else
            {
                current = current.GetNextContextPosition(LogicalDirection.Forward);
            }
        }
        if (current is not null) pointers.Add(current);
        return (sb.ToString(), pointers);
    }

    private void Highlight(TextPointer? start, TextPointer? end)
    {
        if (start is null || end is null) return;
        try
        {
            var range = new TextRange(start, end);
            // Only touch Background — foreground is left alone so heading/rendering colors survive
            range.ApplyPropertyValue(TextElement.BackgroundProperty, HighlightBackground);
            _lastHighlightStart = start;
            _lastHighlightEnd = end;
            var parent = start.Parent as FrameworkContentElement;
            if (parent is not null) parent.BringIntoView();
            else if (start.Paragraph is not null) start.Paragraph.BringIntoView();
        }
        catch (Exception ex)
        {
            Status.Text = "err: " + ex.Message;
        }
    }

    private void ClearHighlight()
    {
        if (_lastHighlightStart is null || _lastHighlightEnd is null) return;
        try
        {
            var range = new TextRange(_lastHighlightStart, _lastHighlightEnd);
            // Explicit Transparent (not UnsetValue) — reliably clears the visual highlight.
            range.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
        }
        catch { /* pointer may have become invalid after document re-render */ }
        _lastHighlightStart = null;
        _lastHighlightEnd = null;
    }

    private static Brush HighlightBackground =>
        Application.Current?.Resources["B.Warn"] as Brush ?? Brushes.Yellow;
    private static Brush HighlightForeground =>
        Application.Current?.Resources["B.BG.Primary"] as Brush ?? Brushes.Black;

    /// <summary>
    /// Yield only leaf blocks. For container blocks (List / Section), descend into inner blocks
    /// so a text match is not counted twice (once on the container + once on the child).
    /// </summary>
    private static IEnumerable<System.Windows.Documents.Block> Flatten(IEnumerable<System.Windows.Documents.Block> src)
    {
        foreach (var b in src)
        {
            switch (b)
            {
                case List list:
                    foreach (var li in list.ListItems)
                        foreach (var inner in Flatten(li.Blocks))
                            yield return inner;
                    break;
                case Section section:
                    foreach (var inner in Flatten(section.Blocks))
                        yield return inner;
                    break;
                default:
                    yield return b;
                    break;
            }
        }
    }
}
