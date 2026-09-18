using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeCodeManager.Core.Paths;

namespace ClaudeCodeManager.Core.Services;

public enum ScheduleKind { Daily, Weekly, Hourly }

/// <summary>What the operator typed. Persisted beside the task so it can be shown and edited.</summary>
public sealed class ScheduledRunSpec
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
    [JsonPropertyName("workingDir")] public string WorkingDir { get; set; } = "";
    [JsonPropertyName("kind")] public ScheduleKind Kind { get; set; } = ScheduleKind.Daily;
    /// <summary>HH:mm. Ignored for <see cref="ScheduleKind.Hourly"/>.</summary>
    [JsonPropertyName("time")] public string Time { get; set; } = "09:00";
    /// <summary>Weekly only. PowerShell day names: Monday…Sunday.</summary>
    [JsonPropertyName("daysOfWeek")] public List<string> DaysOfWeek { get; set; } = new();
    /// <summary>Hourly only.</summary>
    [JsonPropertyName("everyHours")] public int EveryHours { get; set; } = 6;
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";

    public string ScheduleText => Kind switch
    {
        ScheduleKind.Daily => $"매일 {Time}",
        ScheduleKind.Weekly => $"매주 {(DaysOfWeek.Count == 0 ? "Monday" : string.Join(",", DaysOfWeek))} {Time}",
        _ => $"{EveryHours}시간마다",
    };
}

/// <summary>A registered task: the spec plus whatever the Task Scheduler reports about it.</summary>
public sealed class ScheduledRun
{
    public ScheduledRunSpec Spec { get; set; } = new();

    public string Name => Spec.Name;
    public string State { get; set; } = "";        // Ready / Disabled / Running
    public DateTime? NextRun { get; set; }
    public DateTime? LastRun { get; set; }
    public int LastResult { get; set; }

    public bool IsEnabled => !string.Equals(State, "Disabled", StringComparison.OrdinalIgnoreCase);
    public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);

    public string NextRunText => NextRun is null ? "—" : NextRun.Value.ToString("MM-dd HH:mm");
    public string LastRunText => LastRun is null ? "아직 실행 안 됨" : LastRun.Value.ToString("MM-dd HH:mm");

    /// <summary>
    /// Task Scheduler result codes are unsigned hex in disguise; the three below are the ones an
    /// operator actually meets, and printing 267011 instead of "not yet run" helps nobody.
    /// </summary>
    public string ResultText => unchecked((uint)LastResult) switch
    {
        0 => "성공",
        0x41300 => "예약됨 (미실행)",
        0x41301 => "실행 중",
        0x41302 => "사용자가 종료",
        0x41303 => "아직 실행 안 됨",
        _ => $"실패 (0x{unchecked((uint)LastResult):X})",
    };

    public bool LastRunFailed => LastRun is not null && LastResult != 0
                                && unchecked((uint)LastResult) is not (0x41300 or 0x41301 or 0x41303);

    public string LogPath => ScheduledRunService.LogPathFor(Name);
    public bool HasLog => File.Exists(LogPath);
}

/// <summary>
/// Scheduled headless Claude Code runs, backed by the Windows Task Scheduler.
///
/// Three design points worth stating, because each was a failure mode first:
///
/// <list type="bullet">
/// <item><b>Everything lives under one task folder</b> (<c>\ClaudeCodeManager\</c>). Listing,
/// enabling and deleting are all scoped to it, so the manager can never disable or remove a task it
/// did not create — including the ones Windows and other software rely on.</item>
/// <item><b>The prompt never touches a command line.</b> It is written to a file and piped into
/// <c>claude -p</c> by a generated launcher script. Prompts contain quotes, newlines and Korean;
/// threading that through schtasks arguments is a quoting problem with no reliable answer.</item>
/// <item><b>Queried through PowerShell's scheduler cmdlets, not <c>schtasks /FO CSV</c>.</b> The CSV
/// and LIST output is localised — on a Korean Windows the column headers come back in Korean and a
/// header-based parser silently returns nothing. The cmdlets return objects with stable English
/// property names on every locale.</item>
/// </list>
///
/// These tasks run Claude unattended. The manager writes the launcher and hands the operator the
/// exact command it will run; deciding whether a prompt is safe to run with nobody watching is the
/// operator's call, and the UI says so at the point of creation.
/// </summary>
public static class ScheduledRunService
{
    /// <summary>Task Scheduler folder. The trailing separator is required by the cmdlets.</summary>
    public const string TaskFolder = @"\ClaudeCodeManager\";

    public static string Root => Path.Combine(ClaudePaths.ManagerRoot, "scheduled");
    public static string LogsRoot => Path.Combine(Root, "logs");

    public static string SpecPathFor(string name) => Path.Combine(Root, Sanitize(name) + ".json");
    public static string PromptPathFor(string name) => Path.Combine(Root, Sanitize(name) + ".prompt.txt");
    public static string ScriptPathFor(string name) => Path.Combine(Root, Sanitize(name) + ".cmd");
    public static string LogPathFor(string name) => Path.Combine(LogsRoot, Sanitize(name) + ".log");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Task names become file names and a scheduler identifier, so keep them boring.</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "task" : s;
    }

    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "이름이 필요합니다.";
        if (Sanitize(name) != name.Trim()) return "영문/숫자/하이픈/밑줄만 사용하세요.";
        if (name.Trim().Length > 60) return "이름이 너무 깁니다 (60자 이하).";
        return null;
    }

    public static bool IsClaudeAvailable() => ResolveClaudeCommand() is not null;

    /// <summary>
    /// Locate the Claude CLI. The npm shim is what a scheduled task must invoke — the extensionless
    /// sibling is a shell script and cmd.exe cannot run it.
    /// </summary>
    public static string? ResolveClaudeCommand()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
        {
            var shim = Path.Combine(appData, "npm", "claude.cmd");
            if (File.Exists(shim)) return shim;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "claude.cmd");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    // -- Listing ----------------------------------------------------------

    /// <summary>
    /// Task Scheduler state has no file to fingerprint, so freshness is bounded by time plus
    /// explicit invalidation from every mutating call below.
    ///
    /// This is the single most expensive activation in the app: List() spawns a whole PowerShell
    /// process and runs Get-ScheduledTask + Get-ScheduledTaskInfo, synchronously on the UI thread.
    /// Measured at 1239 ms cold and up to 5119 ms under contention, i.e. that is how long the
    /// window was frozen after clicking SCHEDULE. Nothing outside this app normally edits these
    /// tasks, so a short TTL is nearly always correct and the Refresh button covers the rest.
    /// </summary>
    private static readonly TtlCache<List<ScheduledRun>> ListCache = new(TimeSpan.FromSeconds(30));

    /// <summary>Drop the memoized task list. Called by every mutation here, and by the Refresh
    /// command so the user always has a way to force a real query.</summary>
    public static void InvalidateListCache() => ListCache.Invalidate();

    public static List<ScheduledRun> List() => ListCache.Get(ListUncached);

    public static List<ScheduledRun> ListUncached()
    {
        var runs = new List<ScheduledRun>();

        // @() forces an array even for a single task, so the JSON shape does not change underneath
        // the parser the moment the list drops to one entry.
        var script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$tasks = Get-ScheduledTask -TaskPath {PowerShellRunner.SingleQuote(TaskFolder)}
$rows = @($tasks | ForEach-Object {{
    $i = $_ | Get-ScheduledTaskInfo
    [pscustomobject]@{{
        Name   = $_.TaskName
        State  = [string]$_.State
        Next   = if ($i.NextRunTime) {{ $i.NextRunTime.ToString('o') }} else {{ '' }}
        Last   = if ($i.LastRunTime) {{ $i.LastRunTime.ToString('o') }} else {{ '' }}
        Result = [int]$i.LastTaskResult
    }}
}})
ConvertTo-Json -InputObject @($rows) -Compress -Depth 3
";

        var json = PowerShellRunner.RunJson(script);
        var byName = new Dictionary<string, ScheduledRun>(StringComparer.OrdinalIgnoreCase);

        // ConvertTo-Json unwraps a single-element array into a bare object, so a folder holding
        // exactly one task arrives shaped differently from one holding two. The script pins the
        // array with -InputObject; this accepts the bare object as well, because the failure mode
        // is an empty list rather than an error and that is expensive to notice.
        var elements = json is null
            ? Enumerable.Empty<JsonElement>()
            : json.Value.ValueKind == JsonValueKind.Array
                ? json.Value.EnumerateArray().ToList().AsEnumerable()
                : json.Value.ValueKind == JsonValueKind.Object
                    ? new[] { json.Value }.AsEnumerable()
                    : Enumerable.Empty<JsonElement>();

        {
            foreach (var el in elements)
            {
                var name = Str(el, "Name");
                if (string.IsNullOrEmpty(name)) continue;

                byName[name] = new ScheduledRun
                {
                    State = Str(el, "State"),
                    NextRun = Time(el, "Next"),
                    LastRun = Time(el, "Last"),
                    LastResult = el.TryGetProperty("Result", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0,
                    Spec = new ScheduledRunSpec { Name = name },
                };
            }
        }

        // Merge the on-disk spec so the prompt and schedule survive; a task registered outside the
        // manager still lists, just without a spec.
        foreach (var run in byName.Values)
        {
            var spec = LoadSpec(run.Name);
            if (spec is not null) run.Spec = spec;
            runs.Add(run);
        }

        return runs.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTime? Time(JsonElement el, string name)
    {
        var s = Str(el, name);
        if (string.IsNullOrEmpty(s)) return null;
        if (!DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)) return null;
        // The scheduler reports 1899/1999 sentinels for "never ran" rather than a null.
        return dt.Year < 2000 ? null : dt.ToLocalTime();
    }

    private static ScheduledRunSpec? LoadSpec(string name)
    {
        try
        {
            var p = SpecPathFor(name);
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<ScheduledRunSpec>(File.ReadAllText(p), JsonOpts);
        }
        catch { return null; }
    }

    // -- Creation ---------------------------------------------------------

    /// <summary>The launcher the task will actually execute. Shown to the operator before creation.</summary>
    public static string BuildScript(ScheduledRunSpec spec)
    {
        var claude = ResolveClaudeCommand() ?? "claude.cmd";
        var prompt = PromptPathFor(spec.Name);
        var log = LogPathFor(spec.Name);
        var workdir = string.IsNullOrWhiteSpace(spec.WorkingDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : spec.WorkingDir;

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("rem Generated by ClaudeCode Manager — edits here are overwritten on save.");
        sb.AppendLine($"cd /d \"{workdir}\"");
        sb.AppendLine($"if not exist \"{Path.GetDirectoryName(log)}\" mkdir \"{Path.GetDirectoryName(log)}\"");
        sb.AppendLine($">> \"{log}\" echo ==========================================================");
        sb.AppendLine($">> \"{log}\" echo [RUN] %DATE% %TIME%");
        // The prompt is piped in rather than passed as an argument: it carries quotes, newlines and
        // non-ASCII text, none of which survive a command line intact.
        sb.AppendLine($"type \"{prompt}\" | \"{claude}\" -p --output-format text >> \"{log}\" 2>&1");
        sb.AppendLine($">> \"{log}\" echo [EXIT] %ERRORLEVEL%");
        return sb.ToString();
    }

    /// <summary>Create or replace a scheduled run. Returns null on success, else the error.</summary>
    public static string? Save(ScheduledRunSpec spec)
    {
        InvalidateListCache();
        var nameError = ValidateName(spec.Name);
        if (nameError is not null) return nameError;
        if (string.IsNullOrWhiteSpace(spec.Prompt)) return "프롬프트가 비어 있습니다.";
        if (ResolveClaudeCommand() is null) return "claude CLI를 찾을 수 없습니다 (%APPDATA%\\npm\\claude.cmd).";

        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(LogsRoot);

            if (string.IsNullOrWhiteSpace(spec.CreatedAt))
                spec.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            File.WriteAllText(PromptPathFor(spec.Name), spec.Prompt, new UTF8Encoding(true));
            File.WriteAllText(ScriptPathFor(spec.Name), BuildScript(spec), Encoding.Default);
            AtomicFileWriter.Write(SpecPathFor(spec.Name), JsonSerializer.Serialize(spec, JsonOpts));
        }
        catch (Exception ex)
        {
            return "파일 생성 실패: " + ex.Message;
        }

        var trigger = spec.Kind switch
        {
            ScheduleKind.Daily =>
                $"New-ScheduledTaskTrigger -Daily -At {PowerShellRunner.SingleQuote(spec.Time)}",
            ScheduleKind.Weekly =>
                $"New-ScheduledTaskTrigger -Weekly -DaysOfWeek {DaysArg(spec)} -At {PowerShellRunner.SingleQuote(spec.Time)}",
            _ =>
                // A repeating trigger needs a one-time anchor; starting a minute out avoids an
                // immediate fire the moment the task is registered. This has to stay a single
                // expression -- an assignment followed by a bare $t echoes the whole trigger
                // object onto stdout and buries the status line the caller parses.
                $"New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) " +
                $"-RepetitionInterval (New-TimeSpan -Hours {Math.Clamp(spec.EveryHours, 1, 23)})",
        };

        var script = $@"
$ErrorActionPreference = 'Stop'
try {{
    $action  = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/c ""' + {PowerShellRunner.SingleQuote(ScriptPathFor(spec.Name))} + '""')
    $trigger = {trigger}
    $set     = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries
    $null = Register-ScheduledTask -TaskName {PowerShellRunner.SingleQuote(Sanitize(spec.Name))} -TaskPath {PowerShellRunner.SingleQuote(TaskFolder)} -Action $action -Trigger $trigger -Settings $set -Force
    'OK'
}} catch {{
    'ERR: ' + $_.Exception.Message
}}
";

        var r = PowerShellRunner.Run(script);
        if (SucceededOk(r.StdOut)) return null;

        var output = LastMeaningfulLine(r.StdOut);
        if (output.Length > 0) return output;
        return r.StdErr.Trim() is { Length: > 0 } e ? e : "등록 실패";
    }

    private static string DaysArg(ScheduledRunSpec spec)
    {
        var days = spec.DaysOfWeek.Count > 0 ? spec.DaysOfWeek : new List<string> { "Monday" };
        return string.Join(",", days.Select(d => d.Trim()));
    }

    // -- Lifecycle --------------------------------------------------------

    public static string? Delete(string name, bool alsoRemoveFiles = true)
    {
        InvalidateListCache();
        var script = $@"
$ErrorActionPreference = 'Stop'
try {{
    Unregister-ScheduledTask -TaskName {PowerShellRunner.SingleQuote(Sanitize(name))} -TaskPath {PowerShellRunner.SingleQuote(TaskFolder)} -Confirm:$false
    'OK'
}} catch {{ 'ERR: ' + $_.Exception.Message }}
";
        var r = PowerShellRunner.Run(script);
        var ok = SucceededOk(r.StdOut);

        if (alsoRemoveFiles)
        {
            // Logs are deliberately kept: they are the record of what already ran.
            foreach (var p in new[] { SpecPathFor(name), PromptPathFor(name), ScriptPathFor(name) })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        if (ok) return null;
        var msg = LastMeaningfulLine(r.StdOut);
        return msg.Length > 0 ? msg : "삭제 실패";
    }

    public static string? SetEnabled(string name, bool enabled)
    {
        InvalidateListCache();
        var verb = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        var script = $@"
$ErrorActionPreference = 'Stop'
try {{
    $null = {verb} -TaskName {PowerShellRunner.SingleQuote(Sanitize(name))} -TaskPath {PowerShellRunner.SingleQuote(TaskFolder)}
    'OK'
}} catch {{ 'ERR: ' + $_.Exception.Message }}
";
        var r = PowerShellRunner.Run(script);
        return SucceededOk(r.StdOut) ? null : LastMeaningfulLine(r.StdOut);
    }

    public static string? RunNow(string name)
    {
        InvalidateListCache();
        var script = $@"
$ErrorActionPreference = 'Stop'
try {{
    Start-ScheduledTask -TaskName {PowerShellRunner.SingleQuote(Sanitize(name))} -TaskPath {PowerShellRunner.SingleQuote(TaskFolder)}
    'OK'
}} catch {{ 'ERR: ' + $_.Exception.Message }}
";
        var r = PowerShellRunner.Run(script);
        return SucceededOk(r.StdOut) ? null : LastMeaningfulLine(r.StdOut);
    }

    /// <summary>
    /// The scripts end by emitting OK or an ERR line. Cmdlets that leak object output ahead of it
    /// would defeat a StartsWith check, so look for the marker on a line of its own.
    /// </summary>
    private static bool SucceededOk(string stdout)
        => stdout.Split('\n').Any(l => l.Trim() == "OK");

    private static string LastMeaningfulLine(string stdout)
        => stdout.Split('\n')
                 .Select(l => l.Trim())
                 .LastOrDefault(l => l.Length > 0) ?? "";

    public static string ReadLogTail(string name, int maxLines = 400)
    {
        var p = LogPathFor(name);
        if (!File.Exists(p)) return "(로그 없음 — 아직 실행되지 않았습니다)";
        try
        {
            var lines = File.ReadAllLines(p);
            return lines.Length <= maxLines
                ? string.Join(Environment.NewLine, lines)
                : string.Join(Environment.NewLine, lines.Skip(lines.Length - maxLines));
        }
        catch (Exception ex) { return "(로그를 읽을 수 없습니다: " + ex.Message + ")"; }
    }
}
