namespace BaiduPcsAPP.Services;

/// <summary>UI 对话框抽象，供 ViewModel 调用，避免 ViewModel 依赖具体窗口。</summary>
public interface IDialogService
{
    /// <summary>弹出文本输入框，返回输入值；取消返回 null。</summary>
    Task<string?> PromptAsync(string title, string message, string? initialValue = null);

    /// <summary>弹出确认框。</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>弹出提示框。</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>选择多个文件。</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title);

    /// <summary>选择目录。</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>选择保存文件的路径。</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName);

    /// <summary>复制文本到系统剪贴板。</summary>
    Task CopyToClipboardAsync(string text);
}
