using System;
using System.Windows;
using System.Windows.Input;

namespace ClaudeCodeManager.App.Views;

public partial class InputDialog : Window
{
    public bool OK { get; private set; }
    public string Value { get; private set; } = "";

    /// <summary>Optional validator. Return non-null error to reject input; null to accept.</summary>
    public Func<string, string?>? Validator { get; set; }

    private InputDialog(string title, string message, string initial)
    {
        InitializeComponent();
        HeaderText.Text = title.ToUpperInvariant();
        MessageText.Text = message;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            Keyboard.Focus(InputBox);
            InputBox.SelectAll();
        };
    }

    /// <summary>Show a themed input dialog. Returns (ok, value); value is null on cancel.</summary>
    public static (bool ok, string value) Show(Window? owner, string title, string message, string initial = "",
        Func<string, string?>? validator = null)
    {
        var d = new InputDialog(title, message, initial) { Validator = validator };
        d.Owner = owner ?? Application.Current?.MainWindow;
        d.ShowDialog();
        return (d.OK, d.Value);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var v = (InputBox.Text ?? "").Trim();
        if (Validator is not null)
        {
            var err = Validator(v);
            if (err is not null)
            {
                ErrorText.Text = err;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
        }
        Value = v;
        OK = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        OK = false;
        Close();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnOk(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { OnCancel(sender, e); e.Handled = true; }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
