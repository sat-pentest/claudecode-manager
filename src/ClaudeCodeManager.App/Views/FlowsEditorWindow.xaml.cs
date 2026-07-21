using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeCodeManager.Core.Services;

namespace ClaudeCodeManager.App.Views;

public partial class FlowsEditorWindow : Window
{
    private string _diskText = "";
    private bool _suppressDirty;

    public FlowsEditorWindow()
    {
        InitializeComponent();
        PathText.Text = HarnessFlowsLoader.FlowsFilePath;
        LoadFromDisk();
        Editor.TextChanged += (_, _) => OnTextChanged();
        PreviewKeyDown += OnKeyDown;
        Loaded += (_, _) => { Editor.Focus(); Keyboard.Focus(Editor); };
    }

    private void LoadFromDisk()
    {
        _suppressDirty = true;
        try
        {
            var text = HarnessFlowsLoader.ReadRawText();
            if (string.IsNullOrEmpty(text))
            {
                text = BuildEmptyTemplate();
            }
            _diskText = text;
            Editor.Text = text;
            DirtyIndicator.Visibility = Visibility.Collapsed;
            Validate();
        }
        finally { _suppressDirty = false; }
    }

    private void OnTextChanged()
    {
        if (_suppressDirty) return;
        var dirty = !string.Equals(Editor.Text, _diskText, StringComparison.Ordinal);
        DirtyIndicator.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        Validate();
    }

    /// <summary>Live-validate the editor content and update the status pill.</summary>
    private bool Validate()
    {
        var (ok, err, count) = HarnessFlowsLoader.Validate(Editor.Text);
        if (ok)
        {
            StatusPill.Background = (Brush)FindResource("B.Success");
            StatusPillText.Text = "OK";
            StatusText.Text = $"valid JSON · {count} flow{(count == 1 ? "" : "s")}";
            StatusText.Foreground = (Brush)FindResource("B.TextMuted");
        }
        else
        {
            StatusPill.Background = (Brush)FindResource("B.Danger");
            StatusPillText.Text = "ERR";
            StatusText.Text = err;
            StatusText.Foreground = (Brush)FindResource("B.Danger");
        }
        return ok;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;   // status bar already shows the error
        try
        {
            var text = Editor.Text;
            await HarnessFlowsLoader.WriteRawTextAsync(text);
            _diskText = text;
            DirtyIndicator.Visibility = Visibility.Collapsed;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusPill.Background = (Brush)FindResource("B.Danger");
            StatusPillText.Text = "ERR";
            StatusText.Text = "저장 실패: " + ex.Message;
            StatusText.Foreground = (Brush)FindResource("B.Danger");
        }
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (DirtyIndicator.Visibility == Visibility.Visible)
        {
            var ok = ConfirmDialog.Show(this,
                "REVERT",
                "저장되지 않은 변경이 있습니다.\n디스크에서 다시 불러올까요?",
                ConfirmKind.Danger);
            if (!ok) return;
        }
        LoadFromDisk();
    }

    private void OnInsertTemplate(object sender, RoutedEventArgs e)
    {
        var snippet =
            "    {\n" +
            "      \"name\": \"New Flow Name\",\n" +
            "      \"description\": \"짧은 설명\",\n" +
            "      \"triggerHint\": \"\\\"발동 문구1\\\", \\\"발동 문구2\\\"\",\n" +
            "      \"steps\": [\n" +
            "        {\"kind\": \"SKILL\", \"name\": \"skill-name\", \"detail\": \"역할\", \"arrow\": \"↓ uses\"},\n" +
            "        {\"kind\": \"MCP\", \"name\": \"mcp-name\", \"detail\": \"역할\", \"arrow\": \"\"}\n" +
            "      ]\n" +
            "    },\n";
        Editor.Document.Insert(Editor.CaretOffset, snippet);
    }

    private void OnRevealInExplorer(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = HarnessFlowsLoader.FlowsFilePath;
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = "Explorer 실행 실패: " + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (DirtyIndicator.Visibility == Visibility.Visible)
        {
            var ok = ConfirmDialog.Show(this,
                "닫기",
                "저장되지 않은 변경이 있습니다.\n정말 닫을까요?",
                ConfirmKind.Danger);
            if (!ok) return;
        }
        DialogResult = false;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            OnSave(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnClose(sender, e);
            e.Handled = true;
        }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>Minimal starter JSON when the file doesn't exist yet.</summary>
    private static string BuildEmptyTemplate()
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"_comment\": \"HARNESS orchestration flows. Kind: SKILL | AGENT | MCP | EXTERNAL. Save (Ctrl+S) to write.\",");
        sb.AppendLine("  \"flows\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"name\": \"Example Flow\",");
        sb.AppendLine("      \"description\": \"짧은 설명\",");
        sb.AppendLine("      \"triggerHint\": \"\\\"발동 문구\\\"\",");
        sb.AppendLine("      \"steps\": [");
        sb.AppendLine("        {\"kind\": \"SKILL\", \"name\": \"skill-name\", \"detail\": \"역할\", \"arrow\": \"\"}");
        sb.AppendLine("      ]");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
