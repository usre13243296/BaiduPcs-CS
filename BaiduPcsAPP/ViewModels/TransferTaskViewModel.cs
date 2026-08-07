using CommunityToolkit.Mvvm.ComponentModel;

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

/// <summary>传输任务行（下载/上传共用）。P4/P5 接入真实引擎。</summary>
public partial class TransferTaskViewModel : ViewModelBase
{
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

    public TransferTaskViewModel(string name)
    {
        Name = name;
    }

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
}
