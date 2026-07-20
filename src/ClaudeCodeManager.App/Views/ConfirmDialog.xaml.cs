using System.Windows;
using System.Windows.Input;

namespace ClaudeCodeManager.App.Views;

public enum ConfirmKind { Normal, Danger, Info }

public partial class ConfirmDialog : Window
{
    public bool Result { get; private set; }

    private ConfirmDialog(string title, string message, ConfirmKind kind, bool showCancel)
    {
        InitializeComponent();
        HeaderText.Text = title.ToUpperInvariant();
        MessageText.Text = message;
        if (!showCancel) CancelButton.Visibility = Visibility.Collapsed;
        if (kind == ConfirmKind.Danger)
            OkButton.Style = (Style)FindResource("Btn.Pixel.Danger");
    }

    /// <summary>Show a themed confirm dialog. Returns true on OK, false on Cancel/close.</summary>
    public static bool Show(Window? owner, string title, string message,
        ConfirmKind kind = ConfirmKind.Normal, bool showCancel = true)
    {
        var d = new ConfirmDialog(title, message, kind, showCancel);
        d.Owner = owner ?? Application.Current?.MainWindow;
        d.ShowDialog();
        return d.Result;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnOk(object sender, RoutedEventArgs e) { Result = true; Close(); }
    private void OnCancel(object sender, RoutedEventArgs e) { Result = false; Close(); }
}
