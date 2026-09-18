using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// Ranked full-text search over the managed tree.
///
/// The literal search answers "which lines contain this string", which is the right tool when you
/// know the string. It is the wrong tool for "where did I write about scope enforcement", because
/// every hit weighs the same and a file that mentions the term once outranks nothing. This one
/// scores whole documents with BM25 and returns them best-first, with a snippet cut around the
/// densest match.
///
/// Implemented as an in-memory inverted index rather than SQLite FTS5 on purpose: the corpus is a
/// few hundred small markdown files, and the manager ships as a single self-contained executable
/// where a native SQLite payload would be a disproportionate thing to carry for a few kilobytes of
/// prose. The index builds in milliseconds and is rebuilt whenever the tree changes.
/// </summary>
public static class RankedSearchService
{
    // BM25 parameters. k1 controls how fast term frequency saturates, b how hard length
    // normalisation bites. These are the standard defaults and behave well on short documents.
    private const double K1 = 1.2;
    private const double B = 0.75;

    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int SnippetRadius = 110;

    // ── Index ────────────────────────────────────────────────────────────

    private sealed class Doc
    {
        public string Path = "";
        public string Text = "";
        public int Length;                 // token count, for length normalisation
        public SearchHitStatus Status;
        public SearchHitCategory Category;
    }

    private sealed class Index
    {
        public List<Doc> Docs = new();
        /// <summary>term -> (docId -> term frequency)</summary>
        public Dictionary<string, Dictionary<int, int>> Postings = new(StringComparer.Ordinal);
        public double AvgLength = 1;
        public string Signature = "";
    }

    private static Index? _cache;
    private static readonly object Gate = new();

    /// <summary>
    /// Cheap fingerprint of the tree. Rebuilding on every search would be correct but wasteful;
    /// stat-ing the files is roughly two orders of magnitude cheaper than reading them.
    /// </summary>
    private static string ComputeSignature(List<string> files)
    {
        long acc = files.Count;
        foreach (var f in files)
        {
            try
            {
                var fi = new FileInfo(f);
                acc = unchecked(acc * 31 + fi.LastWriteTimeUtc.Ticks + fi.Length);
            }
            catch { /* vanished mid-scan; the next search picks it up */ }
        }
        return acc.ToString(CultureInfo.InvariantCulture);
    }

    private static Index GetIndex()
    {
        var files = EnumerateIndexableFiles().ToList();
        var sig = ComputeSignature(files);

        lock (Gate)
        {
            if (_cache is not null && _cache.Signature == sig) return _cache;

            var idx = new Index { Signature = sig };
            long totalLength = 0;

            foreach (var path in files)
            {
                string text;
                try
                {
                    if (new FileInfo(path).Length > MaxFileBytes) continue;
                    text = File.ReadAllText(path);
                }
                catch { continue; }

                var doc = new Doc
                {
                    Path = path,
                    Text = text,
                    Status = SearchService.StatusOf(path),
                    Category = SearchService.CategoryOf(path),
                };
                var docId = idx.Docs.Count;
                idx.Docs.Add(doc);

                var tokens = Tokenize(text);
                doc.Length = tokens.Count;
                totalLength += tokens.Count;

                foreach (var t in tokens)
                {
                    if (!idx.Postings.TryGetValue(t, out var byDoc))
                    {
                        byDoc = new Dictionary<int, int>();
                        idx.Postings[t] = byDoc;
                    }
                    byDoc.TryGetValue(docId, out var n);
                    byDoc[docId] = n + 1;
                }
            }

            idx.AvgLength = idx.Docs.Count == 0 ? 1 : (double)totalLength / idx.Docs.Count;
            _cache = idx;
            return idx;
        }
    }

    private static IEnumerable<string> EnumerateIndexableFiles() => SearchService.EnumerateManagedFiles();

    // ── Tokenisation ─────────────────────────────────────────────────────

    private static bool IsCjk(char ch)
        => (ch >= '가' && ch <= '힣')   // Hangul syllables
        || (ch >= 'ᄀ' && ch <= 'ᇿ')   // Hangul jamo
        || (ch >= '㄰' && ch <= '㆏')   // Hangul compatibility jamo
        || (ch >= '一' && ch <= '鿿')   // CJK unified ideographs
        || (ch >= '぀' && ch <= 'ヿ');  // kana

    /// <summary>
    /// Word tokens for Latin runs, character bigrams for CJK runs.
    ///
    /// Korean does not put spaces between morphemes, so whitespace tokenisation would index
    /// "스코프를" and never match a search for "스코프". Bigrams are the same trade FTS5 makes for
    /// CJK: a slightly larger index in exchange for search that actually works on the language most
    /// of these notes are written in.
    /// </summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;

        var i = 0;
        var n = text.Length;
        var buf = new StringBuilder();

        while (i < n)
        {
            var ch = text[i];

            if (IsCjk(ch))
            {
                var start = i;
                while (i < n && IsCjk(text[i])) i++;
                var run = text.AsSpan(start, i - start);

                if (run.Length == 1) tokens.Add(run.ToString());
                else
                    for (var k = 0; k + 1 < run.Length; k++)
                        tokens.Add(run.Slice(k, 2).ToString());
                continue;
            }

            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                buf.Clear();
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '-') && !IsCjk(text[i]))
                {
                    buf.Append(char.ToLowerInvariant(text[i]));
                    i++;
                }
                if (buf.Length > 0) tokens.Add(buf.ToString());
                continue;
            }

            i++;
        }

        return tokens;
    }

    // ── Query ────────────────────────────────────────────────────────────

    private sealed class Term
    {
        public string Text = "";
        public bool Required;   // +term
        public bool Excluded;   // -term
        public bool Prefix;     // term*
    }

    private sealed class ParsedQuery
    {
        public List<Term> Terms = new();
        public List<string> Phrases = new();
        public bool IsEmpty => Terms.Count == 0 && Phrases.Count == 0;
    }

    /// <summary>
    /// Accepts <c>bare</c>, <c>+required</c>, <c>-excluded</c>, <c>prefix*</c> and
    /// <c>"exact phrase"</c>. Anything it does not recognise is treated as a bare term, so a
    /// malformed query degrades to a plain search rather than to no results.
    /// </summary>
    private static ParsedQuery ParseQuery(string query)
    {
        var pq = new ParsedQuery();
        if (string.IsNullOrWhiteSpace(query)) return pq;

        var i = 0;
        while (i < query.Length)
        {
            if (char.IsWhiteSpace(query[i])) { i++; continue; }

            if (query[i] == '"')
            {
                var end = query.IndexOf('"', i + 1);
                if (end < 0) end = query.Length;
                var phrase = query.Substring(i + 1, Math.Max(0, end - i - 1)).Trim();
                if (phrase.Length > 0)
                {
                    pq.Phrases.Add(phrase);

                    // The phrase itself is verified against the raw text later, but its tokens also
                    // have to enter the scoring pass: nothing else puts candidate documents on the
                    // board, and a query made only of phrases would otherwise score nobody and
                    // return nothing at all.
                    foreach (var t in Tokenize(phrase))
                        pq.Terms.Add(new Term { Text = t, Required = true });
                }
                i = end + 1;
                continue;
            }

            var start = i;
            while (i < query.Length && !char.IsWhiteSpace(query[i])) i++;
            var raw = query.Substring(start, i - start);

            var required = raw.StartsWith("+", StringComparison.Ordinal);
            var excluded = raw.StartsWith("-", StringComparison.Ordinal);
            if (required || excluded) raw = raw.Substring(1);

            var prefix = raw.EndsWith("*", StringComparison.Ordinal);
            if (prefix) raw = raw.Substring(0, raw.Length - 1);
            if (raw.Length == 0) continue;

            foreach (var t in Tokenize(raw))
            {
                pq.Terms.Add(new Term { Text = t, Required = required, Excluded = excluded, Prefix = prefix });
            }
        }

        return pq;
    }

    // ── Search ───────────────────────────────────────────────────────────

    public static List<SearchHit> Search(string query, int limit = 300)
    {
        var results = new List<SearchHit>();
        var pq = ParseQuery(query);
        if (pq.IsEmpty) return results;

        var idx = GetIndex();
        if (idx.Docs.Count == 0) return results;

        var n = idx.Docs.Count;
        var scores = new Dictionary<int, double>();
        var matchedTerms = new Dictionary<int, HashSet<string>>();
        var excluded = new HashSet<int>();
        var requiredTerms = pq.Terms.Where(t => t.Required && !t.Excluded).Select(t => t.Text).Distinct().ToList();

        foreach (var term in pq.Terms)
        {
            // Expand a prefix term to every indexed term that starts with it, then score each.
            var expansions = term.Prefix
                ? idx.Postings.Keys.Where(k => k.StartsWith(term.Text, StringComparison.Ordinal)).ToList()
                : new List<string> { term.Text };

            foreach (var exp in expansions)
            {
                if (!idx.Postings.TryGetValue(exp, out var postings)) continue;

                if (term.Excluded)
                {
                    foreach (var docId in postings.Keys) excluded.Add(docId);
                    continue;
                }

                var df = postings.Count;
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));

                foreach (var (docId, tf) in postings)
                {
                    var len = idx.Docs[docId].Length;
                    var norm = tf + K1 * (1 - B + B * len / idx.AvgLength);
                    var contribution = idf * (tf * (K1 + 1)) / (norm <= 0 ? 1 : norm);

                    scores.TryGetValue(docId, out var acc);
                    scores[docId] = acc + contribution;

                    if (!matchedTerms.TryGetValue(docId, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        matchedTerms[docId] = set;
                    }
                    set.Add(term.Text);
                }
            }
        }

        foreach (var (docId, score) in scores.OrderByDescending(kv => kv.Value))
        {
            if (excluded.Contains(docId)) continue;

            var doc = idx.Docs[docId];

            // A required term must be present in this document, not merely somewhere in the corpus.
            if (requiredTerms.Count > 0)
            {
                var got = matchedTerms.TryGetValue(docId, out var set) ? set : null;
                if (got is null || !requiredTerms.All(got.Contains)) continue;
            }

            // Phrases are checked against the raw text: the index knows tokens, not adjacency, and
            // a substring test is both exact and cheap at this corpus size.
            if (pq.Phrases.Count > 0 &&
                !pq.Phrases.All(p => doc.Text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                continue;

            var (line, snippet) = BuildSnippet(doc.Text, pq);
            results.Add(new SearchHit
            {
                File = doc.Path,
                Line = line,
                Preview = snippet,
                Status = doc.Status,
                Category = doc.Category,
                Score = score,
            });

            if (results.Count >= limit) break;
        }

        return results;
    }

    /// <summary>
    /// Cut a window around the best match. "Best" is the earliest position where the most distinct
    /// query terms occur close together, which in practice is the passage a reader wants to see.
    /// </summary>
    private static (int line, string snippet) BuildSnippet(string text, ParsedQuery pq)
    {
        // A phrase is the most specific thing the query asked for, so anchor on it when there is
        // one. Falling back to the earliest single token would open the window on an incidental
        // mention of one word and leave the passage the reader actually searched for off-screen.
        var best = EarliestOf(text, pq.Phrases);
        if (best < 0)
            best = EarliestOf(text, pq.Terms.Where(t => !t.Excluded).Select(t => t.Text));
        if (best < 0) best = 0;

        var start = Math.Max(0, best - SnippetRadius);
        var end = Math.Min(text.Length, best + SnippetRadius);

        // Prefer to start at a line boundary so the snippet does not open mid-word.
        var nl = text.LastIndexOf('\n', Math.Max(0, Math.Min(best, text.Length - 1)));
        if (nl >= 0 && best - nl < SnippetRadius) start = nl + 1;

        var snippet = text.Substring(start, Math.Max(0, end - start))
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Trim();

        while (snippet.Contains("  ", StringComparison.Ordinal))
            snippet = snippet.Replace("  ", " ", StringComparison.Ordinal);

        if (start > 0) snippet = "… " + snippet;
        if (end < text.Length) snippet += " …";

        // 1-based line number of the match, for the jump target.
        var line = 1;
        for (var i = 0; i < best && i < text.Length; i++)
            if (text[i] == '\n') line++;

        return (line, snippet);
    }

    /// <summary>Earliest occurrence of any needle, or -1 when none appear.</summary>
    private static int EarliestOf(string text, IEnumerable<string> needles)
    {
        var best = -1;
        foreach (var needle in needles)
        {
            if (string.IsNullOrEmpty(needle)) continue;
            var at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (best < 0 || at < best)) best = at;
        }
        return best;
    }

    /// <summary>Drop the cached index. The next search rebuilds from disk.</summary>
    public static void Invalidate()
    {
        lock (Gate) _cache = null;
    }
}
