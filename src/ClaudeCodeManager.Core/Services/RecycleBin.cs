using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ClaudeCodeManager.Core.Services;

/// <summary>
/// Deletes files to the Recycle Bin.
///
/// Session logs are conversations, not scratch files: a mis-click used to end a record of work
/// with nothing behind it, because snapshots only mirror .md/.json/.mjs and never carried .jsonl.
/// Routing through the bin gives that click an undo.
///
/// The shell API is used rather than the VisualBasic wrapper because it takes the whole batch in
/// one call and, with the flags set here, never puts a dialog on screen — a modal error in the
/// middle of pruning forty files would be worse than the failure it reports.
///
/// The shell reports one status for the batch, so per-file outcomes are established afterwards by
/// checking what actually disappeared. That is the only honest way to say "deleted 3, failed 2".
/// </summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    public sealed record Outcome(int Deleted, long Freed, int Failed);

    /// <summary>
    /// Send every given file to the Recycle Bin, and report what actually went.
    /// </summary>
    public static Outcome Send(IEnumerable<string> paths)
    {
        // Size has to be read before the call — afterwards the file is gone.
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            try { if (File.Exists(p)) sizes[p] = new FileInfo(p).Length; }
            catch { /* unreadable — it will simply count as failed below */ }
        }
        if (sizes.Count == 0) return new Outcome(0, 0, 0);

        try
        {
            // The shell expects the list double-null terminated.
            var list = string.Join("\0", sizes.Keys) + "\0\0";
            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = list,
                pTo = null,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            };
            SHFileOperation(ref op);
        }
        catch
        {
            // Even a hard failure falls through to the survey below, which reports honestly
            // rather than guessing from a return code.
        }

        int deleted = 0, failed = 0;
        long freed = 0;
        foreach (var (path, size) in sizes)
        {
            bool gone;
            try { gone = !File.Exists(path); } catch { gone = false; }
            if (gone) { deleted++; freed += size; }
            else failed++;
        }
        return new Outcome(deleted, freed, failed);
    }

    public static Outcome Send(string path) => Send(new[] { path });
}
