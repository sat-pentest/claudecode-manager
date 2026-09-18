using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClaudeCodeManager.Core.Services;

public sealed record PsResult(bool Ok, string StdOut, string StdErr);

/// <summary>
/// Runs a PowerShell script and hands back its output.
///
/// Scripts are passed as <c>-EncodedCommand</c>, which sidesteps every layer of quoting between
/// here and the shell — the scripts in this codebase carry both quote styles, backslash registry
/// paths and Windows file paths, and escaping those through a command line by hand is a reliable
/// source of silent breakage.
/// </summary>
public static class PowerShellRunner
{
    public static PsResult Run(string script, int timeoutMs = 30_000)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            if (p is null) return new PsResult(false, "", "could not start powershell.exe");

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return new PsResult(false, stdout, "timed out after " + timeoutMs + " ms");
            }

            return new PsResult(p.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new PsResult(false, "", ex.Message);
        }
    }

    /// <summary>
    /// Run a script whose last statement is a ConvertTo-Json and parse the result.
    ///
    /// ConvertTo-Json emits a bare object rather than a one-element array when the pipeline yields
    /// exactly one item, so callers that expect a list get an object out of nowhere the moment a
    /// second entry is deleted. Prefixing the output with the array subexpression operator at the
    /// call site is the fix; this just parses whatever comes back.
    /// </summary>
    public static JsonElement? RunJson(string script, int timeoutMs = 30_000)
    {
        var r = Run(script, timeoutMs);
        if (string.IsNullOrWhiteSpace(r.StdOut)) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    /// <summary>Quote a value for embedding in a single-quoted PowerShell string literal.</summary>
    public static string SingleQuote(string? value)
        => "'" + (value ?? "").Replace("'", "''") + "'";
}
