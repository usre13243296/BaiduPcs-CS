using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BaiduPcs.Core.Transfer;
using BaiduPcs.Core.Upload;
using BaiduPcsAPP.Services;

namespace BaiduPcsAPP.ViewModels;

/// <summary>传输任务状态。</summary>
public enum TransferTaskState
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// 传输任务行（下载/上传共用）。挂接 TransferManager 驱动真实引擎：
/// 暂停/恢复/取消/删除命令 + 错误详情 + 状态驱动的按钮可用性。
/// </summary>
public partial class TransferTaskViewModel : ViewModelBase
{
    private readonly TransferManager? _manager;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial TransferTaskState State { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string SpeedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailText { get; set; } = string.Empty;

    /// <summary>引擎实例（运行中由 TransferManager 设置；下载/上传共用接口）。</summary>
    internal ITransferEngine? Engine { get; set; }

    /// <summary>取消令牌（由 TransferManager 管理）。</summary>
    internal CancellationTokenSource? Cts { get; set; }

    /// <summary>
    /// 取消时是否清理临时文件。点「取消」保持 false（保留 .part/.part.json 供续传），
    /// 点「删除任务」时置 true。由 <see cref="Services.TransferManager"/> 维护。
    /// </summary>
    internal bool PurgeFilesOnCancel { get; set; }

    /// <summary>远程路径与保存目录（下载任务：失败重试/完成重命名用）。</summary>
    internal string RemotePath { get; set; } = string.Empty;

    internal string SaveDir { get; set; } = string.Empty;

    /// <summary>上传任务：本地文件路径与远端目标目录。</summary>
    internal string LocalPath { get; set; } = string.Empty;

    internal string RemoteDir { get; set; } = string.Empty;

    /// <summary>是否上传任务（下载分支有 .part 清理/重命名，上传分支无）。</summary>
    internal bool IsUpload { get; set; }

    /// <summary>上传会话（进程内断点续传：uploadid + 已传分片）。</summary>
    internal UploadSession? UploadSession { get; set; }

    public TransferTaskViewModel(string name, TransferManager? manager = null)
    {
        Name = name;
        _manager = manager;
    }

    public bool HasDetail => !string.IsNullOrEmpty(DetailText);

    // ======================== 按钮可用性（状态驱动，对齐方案 6.2 矩阵） ========================

    public bool CanPause => State == TransferTaskState.Running;

    public bool CanResume => State is TransferTaskState.Paused or TransferTaskState.Failed;

    public bool CanCancel => State is TransferTaskState.Queued or TransferTaskState.Running or TransferTaskState.Paused;

    public bool CanDelete => State is TransferTaskState.Queued or TransferTaskState.Paused
        or TransferTaskState.Completed or TransferTaskState.Failed or TransferTaskState.Canceled;

    public string StateText => State switch
    {
        TransferTaskState.Queued => "排队中",
        TransferTaskState.Running => "进行中",
        TransferTaskState.Paused => "已暂停",
        TransferTaskState.Completed => "已完成",
        TransferTaskState.Failed => "失败",
        TransferTaskState.Canceled => "已取消",
        _ => string.Empty,
    };

    partial void OnStateChanged(TransferTaskState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanDelete));
    }

    partial void OnDetailTextChanged(string value) => OnPropertyChanged(nameof(HasDetail));

    // ======================== 操作命令（转发给 TransferManager） ========================

    [RelayCommand]
    private Task PauseAsync() => _manager?.PauseAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ResumeAsync() => _manager?.ResumeAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void Cancel() => _manager?.Cancel(this);

    [RelayCommand]
    private void Delete() => _manager?.Delete(this);
}
