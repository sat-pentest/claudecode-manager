using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Xml;
using ClaudeCodeManager.Core.Paths;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace ClaudeCodeManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ClaudePaths.EnsureManagerDirs();
        HookExceptions();
        RegisterMarkdownRust();
    }

    private void HookExceptions()
    {
        DispatcherUnhandledException += (_, ev) =>
        {
            Log("DispatcherUnhandled", ev.Exception);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            Log("AppDomainUnhandled", ev.ExceptionObject as Exception);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log("UnobservedTask", ev.Exception);
            ev.SetObserved();
        };
    }

    private static void Log(string source, Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n";
            File.AppendAllText(Path.Combine(ClaudePaths.LogsRoot, "crash.log"), line);
        }
        catch { /* nothing more we can do */ }
    }

    private static void RegisterMarkdownRust()
    {
        try
        {
            var info = GetResourceStream(new Uri("pack://application:,,,/Themes/MarkdownRust.xshd"));
            if (info?.Stream is null) return;
            using var reader = new XmlTextReader(info.Stream);
            var def = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting("MarkdownRust", new[] { ".md", ".markdown" }, def);
        }
        catch (Exception ex)
        {
            Log("RegisterMarkdownRust", ex);
        }
    }
}
