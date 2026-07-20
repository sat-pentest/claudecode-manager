using System;
using System.Windows;
using ICSharpCode.AvalonEdit;

namespace ClaudeCodeManager.App.Services;

public static class AvalonEditHelper
{
    public static readonly DependencyProperty BindableTextProperty = DependencyProperty.RegisterAttached(
        "BindableText",
        typeof(string),
        typeof(AvalonEditHelper),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnBindableTextChanged));

    public static string GetBindableText(DependencyObject d) => (string)d.GetValue(BindableTextProperty);
    public static void SetBindableText(DependencyObject d, string v) => d.SetValue(BindableTextProperty, v);

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(AvalonEditHelper), new PropertyMetadata(false));

    private static void OnBindableTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor) return;
        var newText = e.NewValue as string ?? "";

        try
        {
            if (!(bool)editor.GetValue(HookedProperty))
            {
                editor.SetValue(HookedProperty, true);
                editor.Loaded += (_, _) => SyncIn(editor, GetBindableText(editor));
                editor.TextChanged += (_, _) =>
                {
                    try
                    {
                        if (editor.Document is null) return;
                        var current = editor.Document.Text;
                        if (GetBindableText(editor) != current)
                            SetBindableText(editor, current);
                    }
                    catch { /* swallow */ }
                };
            }
            SyncIn(editor, newText);
        }
        catch { /* swallow */ }
    }

    private static void SyncIn(TextEditor editor, string newText)
    {
        try
        {
            if (editor.Document is null) return;
            if (editor.Document.Text == newText) return;
            var caret = editor.CaretOffset;
            editor.Document.Text = newText ?? "";
            try { editor.CaretOffset = Math.Min(caret, (newText ?? "").Length); } catch { }
        }
        catch { /* swallow */ }
    }
}
