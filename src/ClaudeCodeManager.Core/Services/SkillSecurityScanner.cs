using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public enum SkillIssueSeverity { Info, Warning, Critical }

public sealed class SkillFinding
{
    public string Rule { get; set; } = "";
    public SkillIssueSeverity Severity { get; set; }
    public string Description { get; set; } = "";
    public int Line { get; set; }
    public string Snippet { get; set; } = "";

    public string SeverityText => Severity.ToString().ToUpperInvariant();
    public string LocationText => Line > 0 ? $"line {Line}" : "";
}

public enum SkillReviewState
{
    /// <summary>Content matches what was reviewed and signed off.</summary>
    Reviewed,
    /// <summary>Never reviewed.</summary>
    New,
    /// <summary>Reviewed once, but the file has changed since.</summary>
    Changed,
}

public sealed class SkillScanResult
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Disabled { get; set; }
    public string ContentHash { get; set; } = "";
    public SkillReviewState ReviewState { get; set; }
    public List<SkillFinding> Findings { get; set; } = new();

    public int CriticalCount => Findings.Count(f => f.Severity == SkillIssueSeverity.Critical);
    public int WarningCount => Findings.Count(f => f.Severity == SkillIssueSeverity.Warning);
    public int InfoCount => Findings.Count(f => f.Severity == SkillIssueSeverity.Info);

    public bool HasFindings => Findings.Count > 0;

    /// <summary>
    /// What the operator still has to look at. A reviewed skill is settled however many rules it
    /// trips, because the rules describe capability, not intent — and the operator has already
    /// judged the intent.
    /// </summary>
    public bool NeedsAttention => ReviewState != SkillReviewState.Reviewed && HasFindings;

    public string StateText => ReviewState switch
    {
        SkillReviewState.Reviewed => "REVIEWED",
        SkillReviewState.Changed => "CHANGED",
        _ => "NEW",
    };

    public string SummaryText => HasFindings
        ? $"{CriticalCount} critical · {WarningCount} warning · {InfoCount} info"
        : "no rule hits";

    public string ShortHash => ContentHash.Length >= 12 ? ContentHash[..12] : ContentHash;
}

/// <summary>
/// Static rule scan over skill and agent definitions, plus a review baseline.
///
/// The rules are a straight port of the twelve a public skill registry applies before letting a
/// downloaded skill be installed — prompt injection, exfiltration wording, dangerous shell,
/// obfuscation, hidden HTML-comment instructions, path traversal, SSRF including the cloud metadata
/// address, and so on.
///
/// The baseline exists because those rules were written for a different threat model. They assume a
/// skill arriving from a stranger, where any mention of <c>curl | bash</c> or
/// <c>169.254.169.254</c> is a red flag. In a security practitioner's own library those strings are
/// the subject matter, and a scanner that paints the whole shelf red on every run is one nobody
/// reads twice. So each skill's SKILL.md is hashed, and the operator signs off once: after that the
/// skill is settled until its content actually changes. What the list surfaces is then the useful
/// question — <em>what is new or different since I last looked</em> — which is also the question
/// that catches a definition edited by something other than the operator.
/// </summary>
public static class SkillSecurityScanner
{
    private sealed record Rule(string Name, SkillIssueSeverity Severity, string Description, Regex Pattern);

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    private static readonly Rule[] Rules =
    {
        new("prompt-injection-system", SkillIssueSeverity.Critical,
            "Attempts to override system instructions",
            new Regex(@"\b(?:ignore\s+(?:all\s+)?previous\s+instructions?|forget\s+(?:all\s+)?(?:your\s+)?instructions?|you\s+are\s+now\s+(?:a|an)\s+(?:evil|unrestricted))", Opts)),

        new("prompt-injection-role", SkillIssueSeverity.Critical,
            "Role manipulation or safety bypass",
            new Regex(@"\b(?:act\s+as\s+(?:a\s+)?(?:root|admin|superuser)|you\s+(?:must|should)\s+(?:always\s+)?execute|bypass\s+(?:all\s+)?safety|disable\s+(?:all\s+)?(?:safety|security|filters?))", Opts)),

        // The registry's own version of this rule uses an unbounded [\s\S]*? between the fence and
        // the dangerous token, so an opening ```bash anywhere in a document matches a stray exec(
        // hundreds of lines later in unrelated prose or another language's block — which is exactly
        // what it did to a Frida skill here (fence at line 29, `this.exec(cmd)` at line 254).
        // (?:(?!`{3,})[\s\S])*? stops the match at the closing fence, so the token has to really be
        // inside the shell block.
        new("shell-exec-dangerous", SkillIssueSeverity.Critical,
            "Executable shell block with destructive or piped-download commands",
            new Regex(@"`{3,}\s*(?:bash|sh|zsh|shell)\s*\n(?:(?!`{3,})[\s\S])*?(?:rm\s+-rf|curl\s+.*\|\s*(?:bash|sh)|wget\s+.*\|\s*(?:bash|sh)|eval\s*\(|exec\s*\()", Opts)),

        new("data-exfiltration", SkillIssueSeverity.Critical,
            "Instruction to send data, files, or secrets outward",
            new Regex(@"\b(?:send\s+(?:all\s+)?(?:data|files?|contents?|secrets?|keys?|tokens?)\s+to|exfiltrate|upload\s+(?:all\s+)?(?:data|files?))", Opts)),

        new("credential-harvesting", SkillIssueSeverity.Warning,
            "Possible hardcoded credential in the definition",
            new Regex(@"\b(?:api[_-]?key|secret|password|token|credential)\s*[:=]\s*['""`]?\w{8,}", Opts)),

        new("obfuscated-content", SkillIssueSeverity.Warning,
            "Encoded or obfuscated content that could hide instructions",
            new Regex(@"(?:atob|btoa|Buffer\.from)\s*\(|\\x[0-9a-f]{2}(?:\\x[0-9a-f]{2}){5,}|\\u[0-9a-f]{4}(?:\\u[0-9a-f]{4}){5,}", Opts)),

        new("hidden-instructions", SkillIssueSeverity.Warning,
            "HTML comment carrying instructions — invisible in rendered form",
            new Regex(@"<!--[\s\S]*?(?:ignore|override|bypass|inject|execute)[\s\S]*?-->", Opts)),

        new("excessive-permissions", SkillIssueSeverity.Warning,
            "Elevated permissions or dangerous permission changes",
            new Regex(@"\b(?:sudo|chmod\s+777|chmod\s+\+x\s+/|chown\s+root)\b", Opts)),

        new("network-fetch", SkillIssueSeverity.Info,
            "References external URLs — confirm they are trusted",
            new Regex(@"\b(?:fetch|curl|wget|axios|http\.get|request\.get)\s*\(\s*['""`]https?://", Opts)),

        new("path-traversal", SkillIssueSeverity.Critical,
            "Repeated parent-directory traversal",
            new Regex(@"(?:\.\./){2,}|(?:\.\.\\){2,}|(?:%2e%2e%2f){2,}", Opts)),

        new("ssrf-internal-network", SkillIssueSeverity.Critical,
            "Fetches localhost or private-range addresses",
            new Regex(@"\b(?:fetch|curl|wget|axios(?:\.[a-z]+)?|https?\.\w+|request(?:\.\w+)?)\s*\(\s*['""`]https?://(?:localhost|127\.\d+\.\d+\.\d+|0\.0\.0\.0|10\.\d+\.\d+\.\d+|172\.(?:1[6-9]|2\d|3[01])\.\d+\.\d+|192\.168\.\d+\.\d+|169\.254\.\d+\.\d+|[^'""` ]*\.internal(?:/|['""`]))", Opts)),

        new("ssrf-metadata-endpoint", SkillIssueSeverity.Critical,
            "Targets a cloud instance-metadata endpoint",
            new Regex(@"169\.254\.169\.254|metadata\.google\.internal|fd00:ec2::254|instance-data", Opts)),
    };

    public static int RuleCount => Rules.Length;

    // -- Scanning ---------------------------------------------------------

    public static SkillScanResult ScanFile(string path, string name, bool disabled)
    {
        var result = new SkillScanResult { Name = name, Path = path, Disabled = disabled };

        string content;
        try { content = File.ReadAllText(path); }
        catch
        {
            result.ReviewState = SkillReviewState.New;
            return result;
        }

        result.ContentHash = Sha256(content);
        result.Findings = ScanContent(content);
        result.ReviewState = SkillReviewStore.StateOf(path, result.ContentHash);
        return result;
    }

    public static List<SkillFinding> ScanContent(string content)
    {
        var findings = new List<SkillFinding>();
        if (string.IsNullOrEmpty(content)) return findings;

        foreach (var rule in Rules)
        {
            var m = rule.Pattern.Match(content);
            if (!m.Success) continue;

            findings.Add(new SkillFinding
            {
                Rule = rule.Name,
                Severity = rule.Severity,
                Description = rule.Description,
                Line = LineOf(content, m.Index),
                Snippet = Clean(m.Value),
            });
        }

        return findings.OrderByDescending(f => (int)f.Severity).ThenBy(f => f.Line).ToList();
    }

    /// <summary>Scan every loaded skill, plus the agent definitions, which share the threat model.</summary>
    public static List<SkillScanResult> ScanAll(IEnumerable<Skill> skills)
    {
        var results = skills
            .Where(s => !string.IsNullOrEmpty(s.SkillFilePath) && File.Exists(s.SkillFilePath))
            .Select(s => ScanFile(s.SkillFilePath, s.Name, s.Disabled))
            .ToList();

        if (Directory.Exists(ClaudePaths.AgentsRoot))
        {
            foreach (var f in Directory.EnumerateFiles(ClaudePaths.AgentsRoot, "*.md", SearchOption.TopDirectoryOnly))
                results.Add(ScanFile(f, System.IO.Path.GetFileNameWithoutExtension(f) + " (agent)", false));
        }

        return results
            .OrderByDescending(r => r.NeedsAttention)
            .ThenByDescending(r => r.CriticalCount)
            .ThenByDescending(r => r.WarningCount)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int LineOf(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
            if (content[i] == '\n') line++;
        return line;
    }

    private static string Clean(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s.Length > 120 ? s[..120] + "…" : s;
    }

    internal static string Sha256(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>
/// Which skill definitions the operator has read and signed off, keyed by content hash.
///
/// Signing off records the hash, not a verdict about the rules: edit the skill and it returns to
/// the list as CHANGED. That makes the store useful for something the rules cannot do on their own
/// — noticing that a definition changed when the operator did not change it.
/// </summary>
public static class SkillReviewStore
{
    private sealed class Entry
    {
        [JsonPropertyName("hash")] public string Hash { get; set; } = "";
        [JsonPropertyName("reviewedAt")] public string ReviewedAt { get; set; } = "";
    }

    public static string FilePath => System.IO.Path.Combine(ClaudePaths.ManagerRoot, "skill-reviews.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath))
                   ?? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase); }
    }

    private static void Save(Dictionary<string, Entry> map)
        => AtomicFileWriter.Write(FilePath, JsonSerializer.Serialize(map, Options));

    public static SkillReviewState StateOf(string path, string hash)
    {
        var map = Load();
        if (!map.TryGetValue(path, out var e)) return SkillReviewState.New;
        return string.Equals(e.Hash, hash, StringComparison.OrdinalIgnoreCase)
            ? SkillReviewState.Reviewed
            : SkillReviewState.Changed;
    }

    public static void MarkReviewed(string path, string hash)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(hash)) return;
        var map = Load();
        map[path] = new Entry { Hash = hash, ReviewedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        Save(map);
    }

    public static void Forget(string path)
    {
        var map = Load();
        if (map.Remove(path)) Save(map);
    }

    public static void MarkAllReviewed(IEnumerable<SkillScanResult> results)
    {
        var map = Load();
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        foreach (var r in results.Where(r => !string.IsNullOrEmpty(r.ContentHash)))
            map[r.Path] = new Entry { Hash = r.ContentHash, ReviewedAt = stamp };
        Save(map);
    }
}
