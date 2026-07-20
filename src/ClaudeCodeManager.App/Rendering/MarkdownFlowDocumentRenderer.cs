using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ClaudeCodeManager.App.Rendering;

/// <summary>
/// Converts a markdown source string into a themed WPF FlowDocument.
/// Uses global application resources for colors so it always matches the current theme.
/// </summary>
public static class MarkdownFlowDocumentRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static FlowDocument Render(string? markdown)
    {
        var doc = NewDocument();
        if (string.IsNullOrEmpty(markdown)) return doc;

        MarkdownDocument ast;
        try { ast = Markdown.Parse(markdown, Pipeline); }
        catch { return doc; }

        foreach (var block in ast)
        {
            var wpfBlock = RenderBlock(block);
            if (wpfBlock is not null) doc.Blocks.Add(wpfBlock);
        }
        return doc;
    }

    // ═══════════════ setup ═══════════════

    private static Brush B(string key, string fallback = "#FFFFFF")
    {
        var res = Application.Current?.Resources[key];
        if (res is Brush br) return br;
        return (Brush)new BrushConverter().ConvertFromString(fallback)!;
    }

    private static FontFamily F(string key, string fallback)
    {
        var res = Application.Current?.Resources[key];
        if (res is FontFamily ff) return ff;
        return new FontFamily(fallback);
    }

    private static FlowDocument NewDocument()
    {
        return new FlowDocument
        {
            Background = B("B.BG.Sunken", "#050403"),
            Foreground = B("B.Text", "#E8DDD0"),
            FontFamily = F("Font.UI", "Segoe UI"),
            FontSize = 14,
            LineHeight = 22,
            PagePadding = new Thickness(28, 20, 28, 24),
            IsOptimalParagraphEnabled = true,
            IsHyphenationEnabled = false,
            IsColumnWidthFlexible = true,
            ColumnWidth = double.MaxValue,
            TextAlignment = TextAlignment.Left,
        };
    }

    // ═══════════════ blocks ═══════════════

    private static System.Windows.Documents.Block? RenderBlock(Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock h:            return RenderHeading(h);
            case ParagraphBlock p:          return RenderParagraph(p);
            case FencedCodeBlock fc:        return RenderFencedCode(fc);
            case CodeBlock cb:              return RenderIndentedCode(cb);
            case ListBlock lb:              return RenderList(lb);
            case QuoteBlock qb:             return RenderQuote(qb);
            case ThematicBreakBlock:        return RenderHr();
            case HtmlBlock html:            return RenderHtmlBlock(html);
            default:
                return null;
        }
    }

    private static System.Windows.Documents.Block RenderHeading(HeadingBlock h)
    {
        var (size, color) = h.Level switch
        {
            1 => (26.0, B("B.Ember", "#F26A2E")),
            2 => (21.0, B("B.Rust",  "#D9441C")),
            3 => (18.0, B("B.Warn",  "#D9A02C")),
            4 => (16.0, B("B.Warn",  "#D9A02C")),
            _ => (15.0, B("B.Warn",  "#D9A02C")),
        };
        var p = new Paragraph
        {
            FontSize = size,
            FontWeight = FontWeights.Bold,
            Foreground = color,
            Margin = new Thickness(0, h.Level == 1 ? 16 : 14, 0, 6),
            // Tag with 0-based source line so outline click can locate this heading in preview mode
            Tag = h.Line,
        };
        RenderInlines(h.Inline, p.Inlines);
        return p;
    }

    private static System.Windows.Documents.Block RenderParagraph(ParagraphBlock pb)
    {
        var p = new Paragraph { Margin = new Thickness(0, 4, 0, 6) };
        RenderInlines(pb.Inline, p.Inlines);
        return p;
    }

    private static System.Windows.Documents.Block RenderFencedCode(FencedCodeBlock cb)
    {
        var text = string.Join(Environment.NewLine, cb.Lines.Lines.Select(l => l.ToString())).TrimEnd();
        var p = new Paragraph
        {
            FontFamily = new FontFamily("Consolas, Cascadia Mono"),
            FontSize = 13,
            Foreground = B("B.Warn", "#D9A02C"),
            Background = B("B.BG.Panel", "#14110F"),
            BorderBrush = B("B.RustDim", "#7A2812"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 8, 0, 8),
        };
        if (!string.IsNullOrEmpty(cb.Info))
        {
            var lang = new Run("[" + cb.Info + "] ")
            {
                Foreground = B("B.RustDim", "#7A2812"),
                FontStyle = FontStyles.Italic,
            };
            p.Inlines.Add(lang);
            p.Inlines.Add(new LineBreak());
        }
        p.Inlines.Add(new Run(text));
        return p;
    }

    private static System.Windows.Documents.Block RenderIndentedCode(CodeBlock cb)
    {
        var text = string.Join(Environment.NewLine, cb.Lines.Lines.Select(l => l.ToString())).TrimEnd();
        var p = new Paragraph
        {
            FontFamily = new FontFamily("Consolas, Cascadia Mono"),
            FontSize = 13,
            Foreground = B("B.Warn", "#D9A02C"),
            Background = B("B.BG.Panel", "#14110F"),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 6, 0, 6),
        };
        p.Inlines.Add(new Run(text));
        return p;
    }

    private static System.Windows.Documents.Block RenderList(ListBlock lb)
    {
        var list = new List
        {
            MarkerStyle = lb.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(4, 2, 0, 6),
            Padding = new Thickness(16, 0, 0, 0),
        };
        foreach (var itemObj in lb)
        {
            if (itemObj is not ListItemBlock lib) continue;
            var li = new ListItem();
            foreach (var childBlock in lib)
            {
                var wb = RenderBlock(childBlock);
                if (wb is not null) li.Blocks.Add(wb);
            }
            list.ListItems.Add(li);
        }
        return list;
    }

    private static System.Windows.Documents.Block RenderQuote(QuoteBlock qb)
    {
        var p = new Paragraph
        {
            Foreground = B("B.TextMuted", "#9A8A78"),
            FontStyle = FontStyles.Italic,
            BorderBrush = B("B.Rust", "#D9441C"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(14, 4, 4, 4),
            Margin = new Thickness(0, 6, 0, 6),
        };
        bool first = true;
        foreach (var child in qb)
        {
            if (!first) p.Inlines.Add(new LineBreak());
            first = false;
            if (child is ParagraphBlock cpb) RenderInlines(cpb.Inline, p.Inlines);
        }
        return p;
    }

    private static System.Windows.Documents.Block RenderHr()
    {
        return new BlockUIContainer(new Rectangle
        {
            Height = 1,
            Fill = B("B.RustDim", "#7A2812"),
            Margin = new Thickness(0, 14, 0, 14),
        });
    }

    private static System.Windows.Documents.Block RenderHtmlBlock(HtmlBlock html)
    {
        // Show HTML raw text dimly (useful for showing CCM-DISABLED comments as-is)
        var text = string.Join(Environment.NewLine, html.Lines.Lines.Select(l => l.ToString())).TrimEnd();
        var p = new Paragraph
        {
            Foreground = B("B.TextDim", "#5A4F44"),
            FontStyle = FontStyles.Italic,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 2),
        };
        p.Inlines.Add(new Run(text));
        return p;
    }

    // ═══════════════ inlines ═══════════════

    private static void RenderInlines(ContainerInline? container, InlineCollection target)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    target.Add(new Run(lit.ToString()));
                    break;

                case EmphasisInline em:
                    var span = new Span();
                    if (em.DelimiterCount >= 2)
                    {
                        span.FontWeight = FontWeights.Bold;
                        span.Foreground = B("B.Ember", "#F26A2E");
                    }
                    else
                    {
                        span.FontStyle = FontStyles.Italic;
                        span.Foreground = B("B.TextMuted", "#9A8A78");
                    }
                    RenderInlines(em, span.Inlines);
                    target.Add(span);
                    break;

                case CodeInline code:
                    var codeRun = new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Consolas, Cascadia Mono"),
                        FontSize = 12.5,
                        Foreground = B("B.Warn", "#D9A02C"),
                        Background = B("B.BG.Panel", "#14110F"),
                    };
                    target.Add(codeRun);
                    break;

                case LinkInline link:
                    if (link.IsImage)
                    {
                        target.Add(new Run("[image: " + (link.Title ?? link.Url) + "]")
                        {
                            Foreground = B("B.TextMuted", "#9A8A78"),
                            FontStyle = FontStyles.Italic,
                        });
                    }
                    else
                    {
                        var hl = new Hyperlink { Foreground = B("B.Success", "#6FA844") };
                        RenderInlines(link, hl.Inlines);
                        if (!string.IsNullOrEmpty(link.Url))
                        {
                            hl.Inlines.Add(new Run(" (" + link.Url + ")")
                            {
                                Foreground = B("B.TextMuted", "#9A8A78"),
                                FontSize = 11,
                            });
                        }
                        target.Add(hl);
                    }
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case HtmlInline htmlInline:
                    target.Add(new Run(htmlInline.Tag)
                    {
                        Foreground = B("B.TextDim", "#5A4F44"),
                        FontStyle = FontStyles.Italic,
                    });
                    break;

                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;

                default:
                    target.Add(new Run(inline.ToString() ?? ""));
                    break;
            }
        }
    }
}
