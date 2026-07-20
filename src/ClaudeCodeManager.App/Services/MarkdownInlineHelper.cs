using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace ClaudeCodeManager.App.Services;

/// <summary>
/// Attached property that treats a plain string with a tiny subset of markdown
/// (**bold**, *italic*, `code`) as TextBlock Inlines — so the markers themselves
/// never appear on screen. Falls back to a plain Run for anything else.
/// Intended for frontmatter description fields displayed as UI labels.
/// </summary>
public static class MarkdownInlineHelper
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(MarkdownInlineHelper),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);
    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);

    // **bold** | *italic* | `code`  — non-greedy, at least one non-marker char
    private static readonly Regex Tokenizer = new(
        @"(\*\*(?<b>[^\*]+?)\*\*|\*(?<i>[^\*]+?)\*|`(?<c>[^`]+?)`)",
        RegexOptions.Compiled);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var text = e.NewValue as string ?? "";
        if (text.Length == 0) return;

        int cursor = 0;
        foreach (Match m in Tokenizer.Matches(text))
        {
            if (m.Index > cursor)
                tb.Inlines.Add(new Run(text[cursor..m.Index]));

            if (m.Groups["b"].Success)
                tb.Inlines.Add(new Run(m.Groups["b"].Value) { FontWeight = FontWeights.Bold });
            else if (m.Groups["i"].Success)
                tb.Inlines.Add(new Run(m.Groups["i"].Value) { FontStyle = FontStyles.Italic });
            else if (m.Groups["c"].Success)
                tb.Inlines.Add(new Run(m.Groups["c"].Value) { FontFamily = new System.Windows.Media.FontFamily("Consolas, Cascadia Mono") });

            cursor = m.Index + m.Length;
        }
        if (cursor < text.Length)
            tb.Inlines.Add(new Run(text[cursor..]));
    }
}
