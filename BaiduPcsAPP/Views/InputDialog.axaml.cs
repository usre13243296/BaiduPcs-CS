using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BaiduPcsAPP.Views;

public partial class InputDialog : Window
{
    public InputDialog()
        : this(string.Empty, string.Empty)
    {
    }

    public InputDialog(string title, string message, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        InputBox.Text = initialValue;
        InputBox.Focus();
        InputBox.SelectAll();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close(InputBox.Text);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close((string?)null);
    }
}
