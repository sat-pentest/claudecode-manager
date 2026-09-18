namespace ClaudeCodeManager.Core.Models;

public enum CheckStatus
{
    /// <summary>Control is in place.</summary>
    Pass,
    /// <summary>Control is weaker than recommended but not absent.</summary>
    Warn,
    /// <summary>Control is absent or defeated.</summary>
    Fail,
    /// <summary>Could not be determined -- probe unavailable, denied, or not applicable here.</summary>
    Skip,
}

public enum CheckSeverity { Low, Medium, High, Critical }

public enum FixSafety
{
    /// <summary>The manager can apply this itself without elevation and without changing anything
    /// beyond the stated target.</summary>
    Safe,
    /// <summary>Needs an admin console, a reboot, or a decision the operator has to make. The
    /// manager only hands over the command.</summary>
    ManualOnly,
}

/// <summary>
/// One host-hardening probe result.
///
/// Deliberately separate from <see cref="Diagnostic"/>: a lint finding is about a file the manager
/// owns and can rewrite, while a security check is about machine state it mostly cannot, and needs
/// fields a linter has no use for -- a remediation command, whether that command is safe to run
/// unattended, and whether the operator has already looked at this and accepted it.
/// </summary>
public sealed class SecurityCheck
{
    /// <summary>Stable identifier. Used to persist accepted-risk marks across scans, so it must not
    /// change once shipped.</summary>
    public string Id { get; set; } = "";

    /// <summary>Group heading: HOST, NETWORK, CLAUDE, ENGAGEMENT.</summary>
    public string Category { get; set; } = "";

    public string Name { get; set; } = "";
    public CheckStatus Status { get; set; }
    public CheckSeverity Severity { get; set; }

    /// <summary>What was actually observed -- the value, not the verdict.</summary>
    public string Detail { get; set; } = "";

    /// <summary>Remediation command or action. Empty when the check passed.</summary>
    public string Fix { get; set; } = "";

    public FixSafety FixSafety { get; set; } = FixSafety.ManualOnly;

    /// <summary>Operator has reviewed this finding and is keeping the current state on purpose.
    /// Set from the UI, persisted by <c>AcceptedRiskStore</c>, never by a scan.</summary>
    public bool AcceptedRisk { get; set; }

    /// <summary>Why it was accepted. Shown in place of the fix so the reason survives the next scan
    /// and the operator does not have to re-derive it.</summary>
    public string AcceptedNote { get; set; } = "";

    /// <summary>What the list should sort and colour by: an accepted risk is no longer outstanding,
    /// however bad the underlying finding is.</summary>
    public CheckStatus EffectiveStatus => AcceptedRisk && Status != CheckStatus.Pass ? CheckStatus.Skip : Status;

    public bool IsOutstanding => !AcceptedRisk && (Status == CheckStatus.Fail || Status == CheckStatus.Warn);
    public bool HasFix => !string.IsNullOrWhiteSpace(Fix);
}
