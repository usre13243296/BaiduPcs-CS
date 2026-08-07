using Avalonia.Controls;
using Avalonia.Input;
using BaiduPcsAPP.ViewModels;

namespace BaiduPcsAPP.Views;

public partial class FileBrowserView : UserControl
{
    public FileBrowserView()
    {
        InitializeComponent();
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is FileBrowserViewModel vm && FileList.SelectedItem is FileItemViewModel item)
        {
            vm.OpenItemCommand.Execute(item);
        }
    }
}
