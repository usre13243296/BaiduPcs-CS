using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BaiduPcsAPP.ViewModels;

/// <summary>
/// 传输面板（下载/上传队列）。任务来自 <see cref="Services.TransferManager"/>，
/// 订阅每个任务的 State 变化以刷新计数。
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

    /// <summary>任务列表是否可见（有任务且未收起）。</summary>
    public bool ShowTaskList => HasTasks && IsExpanded;

    /// <summary>「暂无传输任务」提示是否可见（无任务且未收起）。</summary>
    public bool ShowEmptyHint => !HasTasks && IsExpanded;

    public string ExpandText => IsExpanded ? "收起 ▾" : "展开 ▴";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandText));
        OnPropertyChanged(nameof(ShowTaskList));
        OnPropertyChanged(nameof(ShowEmptyHint));
    }

    /// <summary>收起 / 展开传输面板（原来这个按钮没有命令，点了没反应）。</summary>
    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    /// <summary>添加一个任务（线程安全，可在后台线程调用）。</summary>
    public void AddTask(TransferTaskViewModel task)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Tasks.Add(task);
            NotifyListState();
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
            NotifyListState();
            RefreshCounters();
        });
    }

    private void NotifyListState()
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasNoTasks));
        OnPropertyChanged(nameof(ShowTaskList));
        OnPropertyChanged(nameof(ShowEmptyHint));
    }

    private void RefreshCounters()
    {
        RunningCount = Tasks.Count(t => t.State == TransferTaskState.Running || t.State == TransferTaskState.Queued);
        CompletedCount = Tasks.Count(t => t.State == TransferTaskState.Completed);
        FailedCount = Tasks.Count(t => t.State == TransferTaskState.Failed);
    }
}
