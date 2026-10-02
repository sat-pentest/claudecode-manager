using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeCodeManager.Core.Models;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// Sorts skills into a small fixed set of groups for the SKILLS module.
///
/// The category comes from two places, in order: an explicit <c>category:</c> in the skill's
/// frontmatter wins, and when there is none a heuristic reads the name and description. The
/// heuristic is deliberately shallow — on the live skill set it placed 24 of 25 correctly from the
/// name alone — so grouping works out of the box and the frontmatter field only has to be set for
/// the odd one the heuristic gets wrong.
/// </summary>
public static class SkillCategory
{
    /// <summary>Display order is significance to this operator: recon first, ops last.</summary>
    public static readonly string[] Order =
        { "RECON", "EXPLOIT", "REPORT", "VIDEO", "DESIGN", "OPS", "OTHER" };

    private static readonly HashSet<string> Known =
        new(Order, StringComparer.OrdinalIgnoreCase);

    // Name substrings → category, checked in this order (first hit wins).
    private static readonly (string needle, string cat)[] NameRules =
    {
        ("recon", "RECON"), ("triage", "RECON"), ("hunt", "RECON"), ("tableau", "RECON"),
        ("bypass", "EXPLOIT"), ("sqli", "EXPLOIT"), ("frida", "EXPLOIT"), ("pinning", "EXPLOIT"),
        ("report", "REPORT"),
        ("hyperframes", "VIDEO"), ("media-use", "VIDEO"), ("dashboard-motion", "VIDEO"),
        ("frontend", "DESIGN"), ("taste", "DESIGN"), ("design", "DESIGN"),
        ("backup", "OPS"), ("compact", "OPS"), ("config", "OPS"),
    };

    // Description keyword fallbacks, only consulted when the name matched nothing.
    private static readonly (string needle, string cat)[] DescRules =
    {
        ("정적 정찰", "RECON"), ("취약점", "RECON"), ("static analysis", "RECON"),
        ("우회", "EXPLOIT"), ("exploit", "EXPLOIT"),
        ("리포트", "REPORT"), ("report", "REPORT"),
        ("hyperframes", "VIDEO"), ("animation", "VIDEO"), ("video", "VIDEO"),
        ("design", "DESIGN"), ("frontend", "DESIGN"),
    };

    public static string Of(Skill s)
    {
        // Explicit override wins — but only when it names a category we render; a typo shouldn't
        // create a phantom one-skill group, so an unknown value falls through to the heuristic.
        if (!string.IsNullOrWhiteSpace(s.Category))
        {
            var c = s.Category!.Trim().ToUpperInvariant();
            if (Known.Contains(c)) return c;
        }

        var name = (s.Name ?? "").ToLowerInvariant();
        foreach (var (needle, cat) in NameRules)
            if (name.Contains(needle)) return cat;

        var desc = (s.Description ?? "").ToLowerInvariant();
        foreach (var (needle, cat) in DescRules)
            if (desc.Contains(needle)) return cat;

        return "OTHER";
    }

    /// <summary>True when a skill sits in the category only because the heuristic put it there —
    /// used by the UI to show which assignments are inferred versus pinned.</summary>
    public static bool IsAuto(Skill s)
        => string.IsNullOrWhiteSpace(s.Category) || !Known.Contains(s.Category!.Trim().ToUpperInvariant());

    /// <summary>Rank for sorting groups in <see cref="Order"/>; unknowns sort last.</summary>
    public static int Rank(string category)
    {
        var i = Array.FindIndex(Order, c => c.Equals(category, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? Order.Length : i;
    }
}
