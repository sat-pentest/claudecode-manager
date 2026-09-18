using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using ClaudeCodeManager.App.Services;
using ClaudeCodeManager.Core.Services;

namespace ClaudeCodeManager.App.Views;

/// <summary>
/// Standalone reader for one agent result's raw JSON. The inline FULL JSON panel is capped in
/// height and shares space with the rest of the card, so anything longer than a few dozen lines
/// is unreadable there; this gives it the whole window plus zoom, wrap and syntax colour.
/// </summary>
public partial class JsonViewerWindow : Window
{
    private const double MinFont = 9;
    private const double MaxFont = 32;
    private const double DefaultFont = 14;

    /// <summary>Wide enough that no realistic line wraps — how a RichTextBox is told "don't wrap".</summary>
    private const double NoWrapPageWidth = 6000;

    private readonly string _raw;

    public JsonViewerWindow(WorkflowResultSummary result)
    {
        InitializeComponent();

        _raw = result.FullJson ?? "";

        HeadlineText.Text = string.IsNullOrWhiteSpace(result.Headline) ? "(result)" : result.Headline;
        AgentText.Text = string.IsNullOrEmpty(result.AgentIdShort) ? "" : $"· {result.AgentIdShort}";

        JsonHighlighter.SetJson(JsonBox, _raw);
        ApplyWrap();

        var lines = _raw.Count(c => c == '\n') + 1;
        StatusText.Text = $"{lines:N0} lines · {_raw.Length:N0} chars"
                        + (string.IsNullOrEmpty(result.SeverityBadge) ? "" : $" · {result.SeverityBadge}");

        UpdateZoomLabel();
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    // ── zoom ──────────────────────────────────────────────────────────────

    private void SetFont(double size)
    {
        JsonBox.FontSize = Math.Clamp(size, MinFont, MaxFont);
        if (JsonBox.Document is { } doc) doc.FontSize = JsonBox.FontSize;
        UpdateZoomLabel();
    }

    private void UpdateZoomLabel() => ZoomText.Text = $"{JsonBox.FontSize:0} pt";

    private void OnZoomIn(object sender, RoutedEventArgs e) => SetFont(JsonBox.FontSize + 1);
    private void OnZoomOut(object sender, RoutedEventArgs e) => SetFont(JsonBox.FontSize - 1);
    private void OnZoomReset(object sender, RoutedEventArgs e) => SetFont(DefaultFont);

    /// <summary>Ctrl+wheel zooms; without Ctrl the wheel scrolls as usual.</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        SetFont(JsonBox.FontSize + (e.Delta > 0 ? 1 : -1));
        e.Handled = true;
    }

    // ── wrap ──────────────────────────────────────────────────────────────

    private void OnWrapToggled(object sender, RoutedEventArgs e) => ApplyWrap();

    private void ApplyWrap()
    {
        var wrap = WrapToggle.IsChecked == true;
        if (JsonBox.Document is not { } doc) return;
        // A FlowDocument always wraps to its page width; a very wide page is the only way to turn
        // wrapping off, and NaN restores wrapping to the viewport.
        doc.PageWidth = wrap ? double.NaN : NoWrapPageWidth;
        JsonBox.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    // ── misc ──────────────────────────────────────────────────────────────

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_raw); StatusText.Text = "클립보드에 복사됨"; }
        catch (Exception ex) { StatusText.Text = $"복사 실패: {ex.Message}"; }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:       SetFont(JsonBox.FontSize + 1); e.Handled = true; return;
                case Key.OemMinus or Key.Subtract: SetFont(JsonBox.FontSize - 1); e.Handled = true; return;
                case Key.D0 or Key.NumPad0:        SetFont(DefaultFont);          e.Handled = true; return;
                case Key.W:
                    WrapToggle.IsChecked = WrapToggle.IsChecked != true;
                    ApplyWrap();
                    e.Handled = true;
                    return;
            }
        }
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }

    private void Caption_Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Caption_MaxRestore(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Caption_Close(object sender, RoutedEventArgs e) => Close();
}
