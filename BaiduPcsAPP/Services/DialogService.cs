using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BaiduPcsAPP.Views;

namespace BaiduPcsAPP.Services;

/// <summary>Avalonia 对话框实现。需要持有主窗口引用以作为 owner。</summary>
public sealed class DialogService : IDialogService
{
    private readonly Window _owner;

    public DialogService(Window owner)
    {
        _owner = owner;
    }

    public async Task<string?> PromptAsync(string title, string message, string? initialValue = null)
    {
        var dialog = new InputDialog(title, message, initialValue ?? string.Empty)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var result = await dialog.ShowDialog<string?>(_owner);
        return result;
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ConfirmDialog(title, message)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var result = await dialog.ShowDialog<bool>(_owner);
        return result;
    }

    public Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ConfirmDialog(title, message, okText: "知道了", cancelVisible: false)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        return dialog.ShowDialog<bool>(_owner);
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
        });

        return files
            .Select(f => f.TryGetLocalPath() ?? f.Name)
            .ToList();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault();
        return folder is null ? null : folder.TryGetLocalPath() ?? folder.Name;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
        });

        return file?.TryGetLocalPath() ?? file?.Name;
    }

    public async Task CopyToClipboardAsync(string text)
    {
        var clipboard = _owner.Clipboard;
        if (clipboard is null)
        {
            throw new InvalidOperationException("系统剪贴板不可用");
        }

        var dataTransfer = new Avalonia.Input.DataTransfer();
        dataTransfer.Add(Avalonia.Input.DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(dataTransfer);
    }
}
