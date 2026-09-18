using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClaudeCodeManager.App.Services;

/// <summary>
/// Colourises a JSON string into inline runs. Attach to a TextBlock (read-only display) or a
/// RichTextBox (display + selection); both are populated from the same tokeniser so the inline
/// result panel and the pop-out viewer can never drift apart.
///
/// Deliberately a hand-rolled scanner rather than a parser: agent results are sometimes truncated
/// mid-stream, and a parser would refuse the whole document where a scanner still colours what it
/// has. Unrecognised bytes fall through as plain text instead of throwing.
/// </summary>
public static class JsonHighlighter
{
    public static readonly DependencyProperty JsonProperty = DependencyProperty.RegisterAttached(
        "Json", typeof(string), typeof(JsonHighlighter),
        new PropertyMetadata(null, OnJsonChanged));

    public static void SetJson(DependencyObject d, string? v) => d.SetValue(JsonProperty, v);
    public static string? GetJson(DependencyObject d) => (string?)d.GetValue(JsonProperty);

    private static void OnJsonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var text = e.NewValue as string ?? "";

        switch (d)
        {
            case TextBlock tb:
                tb.Inlines.Clear();
                foreach (var run in Build(text)) tb.Inlines.Add(run);
                break;

            case RichTextBox rtb:
            {
                var para = new Paragraph { Margin = new Thickness(0) };
                foreach (var run in Build(text)) para.Inlines.Add(run);
                var doc = new FlowDocument(para)
                {
                    FontFamily = rtb.FontFamily,
                    FontSize = rtb.FontSize,
                    // Set by the host when wrapping is toggled; a large page width means "don't wrap".
                    PageWidth = double.NaN
                };
                rtb.Document = doc;
                break;
            }
        }
    }

    // ── tokeniser ─────────────────────────────────────────────────────────

    private static IEnumerable<Run> Build(string s)
    {
        var runs = new List<Run>();
        if (string.IsNullOrEmpty(s)) return runs;

        var i = 0;
        var plain = new System.Text.StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            runs.Add(new Run(plain.ToString()) { Foreground = Punct });
            plain.Clear();
        }

        while (i < s.Length)
        {
            var c = s[i];

            if (c == '"')
            {
                var start = i;
                i++;
                while (i < s.Length)
                {
                    if (s[i] == '\\') { i += 2; continue; }   // skip escape pair
                    if (s[i] == '"') { i++; break; }
                    i++;
                }
                var token = s.Substring(start, Math.Min(i, s.Length) - start);

                // A string is a key when the next non-space character is a colon.
                var j = i;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                var isKey = j < s.Length && s[j] == ':';

                FlushPlain();
                runs.Add(new Run(token) { Foreground = isKey ? Key : Str });
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                var start = i;
                if (s[i] == '-') i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '.' or 'e' or 'E' or '+' or '-')) i++;
                FlushPlain();
                runs.Add(new Run(s.Substring(start, i - start)) { Foreground = Num });
                continue;
            }

            if (Match(s, i, "true") || Match(s, i, "false") || Match(s, i, "null"))
            {
                var len = s[i] == 't' ? 4 : s[i] == 'f' ? 5 : 4;
                FlushPlain();
                runs.Add(new Run(s.Substring(i, len)) { Foreground = Lit });
                i += len;
                continue;
            }

            plain.Append(c);
            i++;
        }
        FlushPlain();
        return runs;
    }

    private static bool Match(string s, int i, string word)
        => i + word.Length <= s.Length && string.CompareOrdinal(s, i, word, 0, word.Length) == 0;

    // ── palette ───────────────────────────────────────────────────────────
    // Pulled from the live theme so the panel can never drift from RustTheme.xaml.

    private static Brush Res(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        var sc = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        sc.Freeze();
        return sc;
    }

    private static Brush? _key, _str, _num, _lit, _punct;
    private static Brush Key   => _key   ??= Res("B.Ember",     "#FFF26A2E");   // "name":
    private static Brush Str   => _str   ??= Res("B.Text",      "#FFE8DDD0");   // "value"
    private static Brush Num   => _num   ??= Res("B.Warn",      "#FFD9A02C");   // 42
    private static Brush Lit   => _lit   ??= Res("B.Success",   "#FF6FA844");   // true / false / null
    private static Brush Punct => _punct ??= Res("B.TextMuted", "#FF9A8A78");   // { } [ ] , :
}
