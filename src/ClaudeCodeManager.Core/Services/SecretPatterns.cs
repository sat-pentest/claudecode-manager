using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ClaudeCodeManager.Core.Services;

public sealed record SecretHit(string Type, CredentialSeverity Severity, string RedactedPreview, int Index);

public enum CredentialSeverity { Info, Warning, Critical }

/// <summary>
/// Regex library for leaked credentials in text the manager already reads -- memory notes, skill
/// bodies, settings files.
///
/// Shared by the host security scan and the skill scan so a token is classified the same way
/// wherever it turns up. Matches are reported redacted: the manager's job is to say "there is a
/// live Telegram bot token sitting in a memory note", not to reprint it into a second file.
/// </summary>
public static class SecretPatterns
{
    private sealed record Pattern(string Type, CredentialSeverity Severity, Regex Regex);

    private static readonly Pattern[] Patterns =
    {
        new("AWS access key id",     CredentialSeverity.Critical, new Regex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        new("GitHub token",          CredentialSeverity.Critical, new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", RegexOptions.Compiled)),
        new("Slack token",           CredentialSeverity.Critical, new Regex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled)),
        new("Telegram bot token",    CredentialSeverity.Critical, new Regex(@"\b\d{8,10}:AA[A-Za-z0-9_-]{33}\b", RegexOptions.Compiled)),
        new("Anthropic API key",     CredentialSeverity.Critical, new Regex(@"\bsk-ant-[A-Za-z0-9_-]{20,}\b", RegexOptions.Compiled)),
        new("OpenAI API key",        CredentialSeverity.Critical, new Regex(@"\bsk-(?:proj-)?[A-Za-z0-9]{32,}\b", RegexOptions.Compiled)),
        new("Google API key",        CredentialSeverity.Critical, new Regex(@"\bAIza[0-9A-Za-z_-]{35}\b", RegexOptions.Compiled)),
        new("Private key block",     CredentialSeverity.Critical, new Regex(@"-----BEGIN (?:RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----", RegexOptions.Compiled)),
        new("JWT",                   CredentialSeverity.Warning,  new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled)),
        new("Connection string",     CredentialSeverity.Critical, new Regex(@"\b(?:mongodb(?:\+srv)?|postgres(?:ql)?|mysql|redis|amqp)://[^\s:@/]+:[^\s:@/]+@", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        new("Generic secret assign", CredentialSeverity.Warning,  new Regex(@"(?i)\b(?:api[_-]?key|secret|passwd|password|token)\b\s*[:=]\s*[""']?[A-Za-z0-9/+_\-]{16,}[""']?", RegexOptions.Compiled)),
    };

    /// <summary>
    /// Characters that cannot occur inside a real token or connection string, but are the whole
    /// vocabulary of a regex.
    ///
    /// This exists because the manager scans its own tooling: skill and agent definitions that
    /// detect credentials necessarily contain credential-shaped patterns, and matching
    /// <c>redis://[^"'\s]*:[^"'@</c> out of a detector's rule list and reporting it as a live
    /// database password is the fastest way to make the whole check untrustworthy. A signature is
    /// not the thing it detects.
    /// </summary>
    private static readonly char[] PatternOnlyChars = { '[', ']', '\\', '^', '|', '(', ')', '{', '}', '*', '?' };

    private static bool LooksLikePattern(string match) => match.IndexOfAny(PatternOnlyChars) >= 0;

    /// <summary>Scan text and return redacted hits. Order follows position in the text.</summary>
    public static List<SecretHit> Scan(string text)
    {
        var hits = new List<SecretHit>();
        if (string.IsNullOrEmpty(text)) return hits;

        foreach (var p in Patterns)
        {
            foreach (Match m in p.Regex.Matches(text))
            {
                if (LooksLikePattern(m.Value)) continue;
                hits.Add(new SecretHit(p.Type, p.Severity, Redact(m.Value), m.Index));
            }
        }
        hits.Sort((a, b) => a.Index.CompareTo(b.Index));
        return hits;
    }

    /// <summary>Keep just enough of the value to locate it in the file by eye.</summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var head = value.Length <= 6 ? value : value.Substring(0, 6);
        return head + "..." + $"[{value.Length} chars]";
    }
}
