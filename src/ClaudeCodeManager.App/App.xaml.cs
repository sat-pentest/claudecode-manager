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
        RegisterES6JavaScript();
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

    /// <summary>
    /// Register ES6-aware JavaScript highlighter so template literals (backticks) don't
    /// confuse `/*` patterns inside strings into block comments. Overrides AvalonEdit's
    /// built-in JavaScript definition so views using SyntaxHighlighting="JavaScript" pick
    /// this up automatically.
    /// </summary>
    private static void RegisterES6JavaScript()
    {
        try
        {
            var info = GetResourceStream(new Uri("pack://application:,,,/Themes/ES6JavaScript.xshd"));
            if (info?.Stream is null) return;
            using var reader = new XmlTextReader(info.Stream);
            var def = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            // Register under multiple names so both "JavaScript" (built-in override) and
            // "ES6JavaScript" (explicit) resolve to this definition.
            HighlightingManager.Instance.RegisterHighlighting(
                "JavaScript",
                new[] { ".js", ".mjs", ".cjs", ".jsx", ".ts" },
                def);
            HighlightingManager.Instance.RegisterHighlighting("ES6JavaScript", null, def);
        }
        catch (Exception ex)
        {
            Log("RegisterES6JavaScript", ex);
        }
    }
}
