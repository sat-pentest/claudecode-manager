using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ClaudeCodeManager.Core.Models;

namespace ClaudeCodeManager.Core.Services;

/// <summary>How a <c>[[link]]</c> resolved.</summary>
public enum MemoryLinkKind
{
    /// <summary>Target file exists.</summary>
    Resolved,
    /// <summary>No file matches exactly, but exactly one does once '-'/'_' are folded, or once the
    /// type prefix is allowed to be missing — i.e. a spelling slip or a shorthand, not an intent to
    /// write something later.</summary>
    Misspelled,
    /// <summary>No file and no near match. By the memory rules this is legitimate: a link to a
    /// memory not written yet marks something worth writing, so it is a to-do, not an error.</summary>
    Unwritten,
}

public sealed class MemoryLink
{
    public string From { get; init; } = "";        // slug of the entry containing the link
    public string To { get; init; } = "";          // slug as written inside [[ ]]
    public string? ResolvedTo { get; init; }       // real slug when Misspelled or Resolved
    public MemoryLinkKind Kind { get; init; }
}

public sealed class MemoryNode
{
    public string Slug { get; init; } = "";        // file name without .md / .disabled
    public MemoryEntry? Entry { get; init; }       // null for a ghost (link target that has no file)
    public MemoryType Type => Entry?.Type ?? MemoryType.Unknown;
    public bool Disabled => Entry?.Disabled ?? false;
    public bool IsGhost => Entry is null;
    public int OutDegree { get; set; }
    public int InDegree { get; set; }
    public int Degree => OutDegree + InDegree;
    public string Title => Entry?.Frontmatter.Name ?? Slug;
}

public sealed class MemoryGraph
{
    public List<MemoryNode> Nodes { get; init; } = new();
    public List<MemoryLink> Links { get; init; } = new();

    public int ResolvedCount => Links.Count(l => l.Kind == MemoryLinkKind.Resolved);
    public int MisspelledCount => Links.Count(l => l.Kind == MemoryLinkKind.Misspelled);
    public int UnwrittenCount => Links.Count(l => l.Kind == MemoryLinkKind.Unwritten);

    /// <summary>Entries nothing links to and which link to nothing — invisible to the graph the
    /// memory system is supposed to form.</summary>
    public IEnumerable<MemoryNode> Orphans => Nodes.Where(n => !n.IsGhost && n.Degree == 0);
}

/// <summary>
/// Builds the link graph over a memory directory: nodes are entries, edges are <c>[[wiki links]]</c>.
///
/// The memory format already carries this structure — entries link to each other by slug and the
/// rules ask for liberal linking — but a flat list cannot show it. Measured on the live directory:
/// 101 entries (disabled ones included — they still link), 271 links, 8 targets with no file behind
/// them. Which of those are mistakes and which are deliberate placeholders is exactly what the list
/// view cannot say, so the classification lives here rather than in the view.
/// </summary>
public static class MemoryGraphBuilder
{
    // Wiki link, minus anything that is plainly prose rather than a slug. Documentation in these
    // files legitimately writes [[wiki-link]] or [[링크]] as an example of the syntax; counting
    // those as broken links would be a false positive in the one number people act on.
    private static readonly Regex LinkRegex = new(@"\[\[([^\]\[\r\n|]{1,120})\]\]", RegexOptions.Compiled);

    /// <summary>Placeholder spellings used in prose to describe the syntax itself.</summary>
    private static readonly HashSet<string> SyntaxExamples = new(StringComparer.OrdinalIgnoreCase)
    {
        "wiki-link", "wiki_link", "wikilink", "link", "name", "their-name", "링크", "메모리이름",
    };

    private static string Fold(string s) => s.Replace('-', '_').Trim().ToLowerInvariant();

    /// <summary>Strip a trailing .md that a link sometimes carries by mistake.</summary>
    private static string StripMd(string s)
        => s.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? s[..^3] : s;

    /// <summary>
    /// Resolve a prefix-stripped shorthand to a real slug, but only when it is unambiguous.
    ///
    /// Ambiguity is the whole risk here: [[idor]] suffix-matches three different project files, so
    /// guessing one would invent a link the author never wrote. Requiring a unique match and a
    /// reasonably long tail keeps the rule to the cases where there is only one thing it can mean.
    /// </summary>
    private static string? ResolveShorthand(string foldedTarget, List<string> foldedSlugs,
                                            Dictionary<string, string> folded)
    {
        if (foldedTarget.Length < 8) return null;
        var needle = "_" + foldedTarget;
        string? only = null;
        foreach (var s in foldedSlugs)
        {
            if (!s.EndsWith(needle, StringComparison.Ordinal)) continue;
            if (only is not null) return null;   // more than one candidate — refuse to guess
            only = s;
        }
        return only is null ? null : folded[only];
    }

    public static MemoryGraph Build(MemoryProject project)
        => Build(project?.Entries ?? new List<MemoryEntry>());

    public static MemoryGraph Build(IReadOnlyList<MemoryEntry> entries)
    {
        var nodes = new Dictionary<string, MemoryNode>(StringComparer.OrdinalIgnoreCase);
        var links = new List<MemoryLink>();

        static string SlugOf(MemoryEntry e)
        {
            var n = e.DisplayName;
            return n.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? n[..^3] : n;
        }

        foreach (var e in entries)
        {
            var slug = SlugOf(e);
            nodes[slug] = new MemoryNode { Slug = slug, Entry = e };
        }

        // Fold -> real slug, for catching '-' vs '_' and case slips.
        var folded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slug in nodes.Keys) folded[Fold(slug)] = slug;
        var foldedSlugs = folded.Keys.ToList();

        foreach (var e in entries)
        {
            var from = SlugOf(e);
            // Frontmatter can carry links too, but Body is what the rules ask people to link in.
            foreach (Match m in LinkRegex.Matches(e.Body))
            {
                var raw = m.Groups[1].Value.Trim();
                if (raw.Length == 0) continue;
                if (SyntaxExamples.Contains(raw)) continue;
                // A link with a space and no separator is a sentence, not a slug.
                if (raw.Contains(' ') && !raw.Contains('_') && !raw.Contains('-')) continue;

                var target = StripMd(raw);

                MemoryLinkKind kind;
                string? resolved = null;
                if (nodes.ContainsKey(target))
                {
                    kind = MemoryLinkKind.Resolved;
                    resolved = target;
                }
                else if (folded.TryGetValue(Fold(target), out var near))
                {
                    kind = MemoryLinkKind.Misspelled;
                    resolved = near;
                }
                else if (ResolveShorthand(Fold(target), foldedSlugs, folded) is { } shorthand)
                {
                    // A link written without its type prefix — [[burp_mcp_routing]] for
                    // feedback_burp_mcp_routing. The file exists, so this is a link to fix, not a
                    // memory to write.
                    kind = MemoryLinkKind.Misspelled;
                    resolved = shorthand;
                }
                else
                {
                    kind = MemoryLinkKind.Unwritten;
                    // A ghost node so the unwritten target still has a place on the canvas —
                    // that is what makes "this hub points at nine things that don't exist" visible.
                    if (!nodes.ContainsKey(target))
                        nodes[target] = new MemoryNode { Slug = target, Entry = null };
                }

                links.Add(new MemoryLink { From = from, To = target, ResolvedTo = resolved, Kind = kind });
            }
        }

        // Degrees are what the renderer sizes nodes by. A misspelled link still connects two real
        // entries, so it counts toward both — the fix changes the spelling, not the relationship.
        foreach (var l in links)
        {
            if (nodes.TryGetValue(l.From, out var f)) f.OutDegree++;
            var toKey = l.ResolvedTo ?? l.To;
            if (nodes.TryGetValue(toKey, out var t)) t.InDegree++;
        }

        return new MemoryGraph
        {
            Nodes = nodes.Values.OrderByDescending(n => n.Degree).ThenBy(n => n.Slug, StringComparer.OrdinalIgnoreCase).ToList(),
            Links = links,
        };
    }
}
