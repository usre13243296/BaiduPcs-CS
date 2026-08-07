using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BaiduPcsAPP.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
        : this(string.Empty, string.Empty)
    {
    }

    public ConfirmDialog(string title, string message, string okText = "确定", bool cancelVisible = true)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        CancelButton.IsVisible = cancelVisible;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
