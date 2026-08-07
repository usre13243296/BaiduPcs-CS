using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BaiduPcs.Core.Api;
using BaiduPcs.Core.Util;
using BaiduPcsAPP.Services;

namespace BaiduPcsAPP.ViewModels;

/// <summary>
/// 文件浏览器：当前目录、文件列表、导航与文件管理操作。
/// </summary>
public partial class FileBrowserViewModel : ViewModelBase
{
    private readonly BaiduPcsApiClient _api;
    private readonly IDialogService _dialogs;
    private readonly TransfersViewModel _transfers;
    private readonly TransferManager _transferManager;
    private readonly string _saveDir;

    [ObservableProperty]
    public partial string CurrentPath { get; set; } = "/";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string QuotaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ObservableCollection<FileItemViewModel> Files { get; set; } = new();

    [ObservableProperty]
    public partial FileItemViewModel? SelectedItem { get; set; }

    /// <summary>面包屑路径段。</summary>
    public IReadOnlyList<string> Breadcrumbs =>
        CurrentPath == "/"
            ? new[] { "/" }
            : BuildBreadcrumbs(CurrentPath);

    public bool HasSelection => SelectedItem is not null;

    public FileBrowserViewModel(
        BaiduPcsApiClient api,
        IDialogService dialogs,
        TransfersViewModel transfers,
        TransferManager transferManager,
        string saveDir)
    {
        _api = api;
        _dialogs = dialogs;
        _transfers = transfers;
        _transferManager = transferManager;
        _saveDir = saveDir;
    }

    private static string[] BuildBreadcrumbs(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new string[segments.Length];
        var current = "";
        for (var i = 0; i < segments.Length; i++)
        {
            current += "/" + segments[i];
            result[i] = current;
        }

        return result;
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(LoadAsync("/"), LoadQuotaAsync());
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await Task.WhenAll(LoadAsync(CurrentPath), LoadQuotaAsync());
    }

    [RelayCommand]
    private Task GoUpAsync() => LoadAsync(PcsPath.GetParent(CurrentPath));

    [RelayCommand]
    private Task NavigateToAsync(string path) => LoadAsync(path);

    [RelayCommand]
    private async Task OpenItemAsync(FileItemViewModel item)
    {
        if (item.IsDir)
        {
            await LoadAsync(item.Path);
        }
    }

    private async Task LoadAsync(string path)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var entries = await _api.ListFilesAsync(path);
            var items = entries
                .OrderByDescending(e => e.IsDir)
                .ThenBy(e => e.Filename, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => new FileItemViewModel(e))
                .ToList();

            Files = new ObservableCollection<FileItemViewModel>(items);
            CurrentPath = path;
            StatusText = $"{items.Count(e => !e.IsDir)} 个文件 · {items.Count(e => e.IsDir)} 个文件夹";
        }
        catch (Exception ex)
        {
            // 捕获所有异常（含解码/网络等非 API 异常），显示到 UI 而非逃逸到 UI 循环导致无响应
            ErrorMessage = ex is BaiduApiException apiEx ? apiEx.Message : $"加载失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadQuotaAsync()
    {
        try
        {
            var quota = await _api.GetQuotaInfoAsync();
            QuotaText = $"已用 {SizeFormatter.ConvertFileSize(quota.Used, 2)} / {SizeFormatter.ConvertFileSize(quota.Quota, 2)}";
        }
        catch (Exception)
        {
            // 配额加载失败不阻断 UI
            QuotaText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task CreateFolderAsync()
    {
        var name = await _dialogs.PromptAsync("新建文件夹", "请输入文件夹名称：");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var target = PcsPath.Join(CurrentPath, name.Trim());
        try
        {
            await _api.MkdirAsync(target);
            await LoadAsync(CurrentPath);
        }
        catch (BaiduApiException ex)
        {
            await _dialogs.ShowMessageAsync("创建失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "删除确认",
            $"确定要删除“{SelectedItem.Name}”吗？此操作不可恢复。");
        if (!ok)
        {
            return;
        }

        try
        {
            await _api.RemoveAsync(new[] { SelectedItem.Path });
            await LoadAsync(CurrentPath);
        }
        catch (BaiduApiException ex)
        {
            await _dialogs.ShowMessageAsync("删除失败", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RenameSelectedAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var newName = await _dialogs.PromptAsync("重命名", "请输入新名称：", SelectedItem.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == SelectedItem.Name)
        {
            return;
        }

        try
        {
            var target = PcsPath.Join(PcsPath.GetParent(SelectedItem.Path), newName.Trim());
            await _api.RenameAsync(SelectedItem.Path, target);
            await LoadAsync(CurrentPath);
        }
        catch (BaiduApiException ex)
        {
            await _dialogs.ShowMessageAsync("重命名失败", ex.Message);
        }
    }

    /// <summary>占位：上传引擎接入点（P5 实现真实上传）。</summary>
    [RelayCommand]
    private async Task UploadFilesAsync()
    {
        var files = await _dialogs.PickFilesAsync("选择要上传的文件");
        if (files.Count == 0)
        {
            return;
        }

        await _dialogs.ShowMessageAsync("提示", "上传引擎将在下一阶段实现。");
    }

    /// <summary>复制选中文件的下载直链（仅获取链接，不触发下载；与官方下载并列）。</summary>
    [RelayCommand]
    private async Task CopyDownloadLinkAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        try
        {
            var link = await _api.GetDownloadLinkAsync(SelectedItem.Path);
            if (string.IsNullOrEmpty(link.Dlink))
            {
                await _dialogs.ShowMessageAsync("获取链接失败", "未返回有效的下载链接。");
                return;
            }

            await _dialogs.CopyToClipboardAsync(link.Dlink);
            StatusText = "下载链接已复制到剪贴板";
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("获取链接失败", ex.Message);
        }
    }

    /// <summary>下载选中的文件（目录暂不支持）。经 TransferManager 入队调度，支持暂停/取消/断点续传。</summary>
    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (SelectedItem.IsDir)
        {
            await _dialogs.ShowMessageAsync("提示", "目录下载将在后续版本支持，请先选择单个文件。");
            return;
        }

        // 优先让用户选目录，取消则回退默认下载目录
        var saveDir = await _dialogs.PickFolderAsync("选择保存目录");
        saveDir ??= _saveDir;
        if (string.IsNullOrWhiteSpace(saveDir))
        {
            return;
        }

        if (!_transferManager.EnqueueDownload(SelectedItem.Path, saveDir))
        {
            await _dialogs.ShowMessageAsync("提示", "该文件已在下载队列中。");
        }
    }
}

