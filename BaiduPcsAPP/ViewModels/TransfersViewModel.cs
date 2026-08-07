using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BaiduPcsAPP.ViewModels;

/// <summary>
/// 传输面板（下载/上传队列）。当前为 UI 骨架，P4/P5 接入真实引擎。
/// </summary>
public partial class TransfersViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial ObservableCollection<TransferTaskViewModel> Tasks { get; set; } = new();

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [ObservableProperty]
    public partial int RunningCount { get; set; }

    [ObservableProperty]
    public partial int CompletedCount { get; set; }

    [ObservableProperty]
    public partial int FailedCount { get; set; }

    public bool HasTasks => Tasks.Count > 0;

    public bool HasNoTasks => !HasTasks;

    public string ExpandText => IsExpanded ? "收起 ▾" : "展开 ▴";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandText));

    /// <summary>添加一个任务（线程安全，可在后台线程调用）。</summary>
    public void AddTask(TransferTaskViewModel task)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Tasks.Add(task);
            OnPropertyChanged(nameof(HasTasks));
            OnPropertyChanged(nameof(HasNoTasks));
            task.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TransferTaskViewModel.State))
                {
                    RefreshCounters();
                }
            };
            RefreshCounters();
        });
    }

    /// <summary>移除一个任务行（线程安全，可在后台线程调用）。</summary>
    public void RemoveTask(TransferTaskViewModel task)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Tasks.Remove(task);
            OnPropertyChanged(nameof(HasTasks));
            OnPropertyChanged(nameof(HasNoTasks));
            RefreshCounters();
        });
    }

    private void RefreshCounters()
    {
        RunningCount = Tasks.Count(t => t.State == TransferTaskState.Running || t.State == TransferTaskState.Queued);
        CompletedCount = Tasks.Count(t => t.State == TransferTaskState.Completed);
        FailedCount = Tasks.Count(t => t.State == TransferTaskState.Failed);
    }
}
