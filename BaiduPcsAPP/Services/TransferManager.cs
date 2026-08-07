using BaiduPcs.Core.Api;
using BaiduPcs.Core.Download;
using BaiduPcs.Core.Util;
using BaiduPcsAPP.ViewModels;

namespace BaiduPcsAPP.Services;

/// <summary>
/// 传输任务管理器：队列调度（同时下载数上限）、引擎生命周期（创建/暂停/恢复/取消/重试）、
/// 断点续传（.part/.part.json）、完成重命名、同路径去重。
/// 所有任务状态变更统一经 Dispatcher 封送到 UI 线程。
/// </summary>
public sealed class TransferManager
{
    private readonly BaiduPcsApiClient _api;
    private readonly TransfersViewModel _transfers;
    private readonly SemaphoreSlim _slot;
    private readonly object _lock = new();
    private readonly HashSet<string> _activePaths = new(StringComparer.Ordinal);

    public TransferManager(BaiduPcsApiClient api, TransfersViewModel transfers, int maxConcurrentDownloads = 2)
    {
        _api = api;
        _transfers = transfers;
        _slot = new SemaphoreSlim(maxConcurrentDownloads);
    }

    /// <summary>提交下载任务；同路径已在队列/进行中返回 false（调用方提示）。</summary>
    public bool EnqueueDownload(string remotePath, string saveDir)
    {
        var name = remotePath.Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_activePaths.Add(remotePath))
            {
                return false;
            }
        }

        var task = new TransferTaskViewModel(name, this)
        {
            RemotePath = remotePath,
            SaveDir = saveDir,
            State = TransferTaskState.Queued,
        };
        _transfers.AddTask(task);
        _ = RunAsync(task);
        return true;
    }

    /// <summary>暂停：引擎门控挂起，立即持久化断点。</summary>
    public async Task PauseAsync(TransferTaskViewModel task)
    {
        var engine = task.Engine;
        if (engine is not null)
        {
            await engine.PauseAsync().ConfigureAwait(true);
            task.State = TransferTaskState.Paused;
        }
    }

    /// <summary>恢复：暂停 → 放行；失败 → 重新拉直链重试（断点续传保护进度）。</summary>
    public async Task ResumeAsync(TransferTaskViewModel task)
    {
        if (task.State == TransferTaskState.Paused)
        {
            var engine = task.Engine;
            if (engine is not null)
            {
                await engine.ResumeAsync().ConfigureAwait(true);
                task.State = TransferTaskState.Running;
            }
        }
        else if (task.State == TransferTaskState.Failed)
        {
            lock (_lock)
            {
                if (!_activePaths.Add(task.RemotePath))
                {
                    // 同路径已有新任务在跑：放弃重试
                    return;
                }
            }

            task.State = TransferTaskState.Queued;
            _ = RunAsync(task);
        }
    }

    /// <summary>取消：排队中直接置取消；运行/暂停中取消令牌（引擎清理 .part 与断点）。</summary>
    public void Cancel(TransferTaskViewModel task)
    {
        if (task.State == TransferTaskState.Queued)
        {
            task.State = TransferTaskState.Canceled; // RunAsync 拿到槽后跳过
        }
        else
        {
            task.Cts?.Cancel();
        }
    }

    /// <summary>删除任务行（运行/暂停中先取消，磁盘文件由取消路径清理）。</summary>
    public void Delete(TransferTaskViewModel task)
    {
        if (task.State is TransferTaskState.Running or TransferTaskState.Paused)
        {
            task.Cts?.Cancel();
        }

        _transfers.RemoveTask(task);
    }

    private async Task RunAsync(TransferTaskViewModel task)
    {
        try
        {
            await _slot.WaitAsync().ConfigureAwait(false);
            if (task.State == TransferTaskState.Canceled)
            {
                return; // 排队期间被取消
            }

            SetTask(task, t =>
            {
                t.State = TransferTaskState.Running;
                t.DetailText = string.Empty;
                t.Progress = 0;
                t.SpeedText = string.Empty;
            });

            using var cts = new CancellationTokenSource();
            task.Cts = cts;

            var urls = await _api.BuildDownloadUrlsAsync(task.RemotePath, cts.Token).ConfigureAwait(false);
            var partPath = Path.Combine(task.SaveDir, task.Name + ".part");
            var checkpointPath = partPath + ".json";

            using var engine = new Downloader(new DownloaderOptions
            {
                Urls = urls,
                CookieHeader = _api.Session.BuildCookieHeader(),
            });
            task.Engine = engine;

            var progress = new Progress<DownloaderProgress>(p => SetTask(task, t =>
            {
                t.Progress = p.Percent;
                t.SpeedText = $"{SizeFormatter.ConvertFileSize((long)p.SpeedBytesPerSecond, 2)}/s";
            }));

            await engine.DownloadAsync(partPath, checkpointPath, progress, cts.Token).ConfigureAwait(false);

            // 完成：重命名 .part → 正式文件
            var finalPath = Path.Combine(task.SaveDir, task.Name);
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(partPath, finalPath);

            SetTask(task, t =>
            {
                t.State = TransferTaskState.Completed;
                t.DetailText = finalPath;
                t.SpeedText = string.Empty;
            });
        }
        catch (OperationCanceledException)
        {
            // 取消：清理 .part 与断点（对齐方案 5.7：取消即删除）
            CleanPartFiles(task);
            SetTask(task, t =>
            {
                t.State = TransferTaskState.Canceled;
                t.SpeedText = string.Empty;
            });
        }
        catch (Exception ex)
        {
            SetTask(task, t =>
            {
                t.State = TransferTaskState.Failed;
                t.DetailText = ex is BaiduApiException apiEx ? apiEx.Message : ex.Message;
                t.SpeedText = string.Empty;
            });
        }
        finally
        {
            task.Engine = null;
            task.Cts?.Dispose();
            task.Cts = null;
            _slot.Release();
            lock (_lock)
            {
                _activePaths.Remove(task.RemotePath);
            }
        }
    }

    private static void CleanPartFiles(TransferTaskViewModel task)
    {
        try
        {
            var part = Path.Combine(task.SaveDir, task.Name + ".part");
            if (File.Exists(part))
            {
                File.Delete(part);
            }

            if (File.Exists(part + ".json"))
            {
                File.Delete(part + ".json");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响状态
        }
    }

    /// <summary>统一在 UI 线程更新任务状态（Avalonia 绑定跨线程通知不可靠）。</summary>
    private static void SetTask(TransferTaskViewModel task, Action<TransferTaskViewModel> action)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => action(task));
}
