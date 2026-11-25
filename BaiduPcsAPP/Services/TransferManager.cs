using BaiduPcs.Core.Api;
using BaiduPcs.Core.Download;
using BaiduPcs.Core.Upload;
using BaiduPcs.Core.Util;
using BaiduPcsAPP.ViewModels;

namespace BaiduPcsAPP.Services;

/// <summary>
/// 传输任务管理器：队列调度（同时传输数上限）、引擎生命周期（创建/暂停/恢复/取消/重试）、
/// 下载断点续传（.part/.part.json）与上传会话续传（UploadSession）、完成落盘/合并、去重。
/// 所有任务状态变更统一经 Dispatcher 封送到 UI 线程。
/// </summary>
public sealed class TransferManager
{
    private readonly BaiduPcsApiClient _api;
    private readonly TransfersViewModel _transfers;
    private readonly SemaphoreSlim _slot;
    private readonly object _lock = new();
    private readonly HashSet<string> _activePaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeUploads = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// 提交上传任务（本地文件 → 当前远端目录）；同一本地文件已在队列/进行中返回 false。
    /// 重名由引擎自动递增处理（报告.txt → 报告2.txt → …）。
    /// </summary>
    public bool EnqueueUpload(string localPath, string remoteDir)
    {
        var name = Path.GetFileName(localPath);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_activeUploads.Add(localPath))
            {
                return false;
            }
        }

        var task = new TransferTaskViewModel(name, this)
        {
            LocalPath = localPath,
            RemoteDir = remoteDir,
            IsUpload = true,
            UploadSession = new UploadSession(),
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

    /// <summary>删除任务行：任何状态都先「停住」，再移除行并释放去重键。</summary>
    /// <remarks>
    /// 排队中的任务必须显式置为 <see cref="TransferTaskState.Canceled"/>，否则它拿到槽位后
    /// 仍会真正开始传输（<see cref="RunAsync"/> 只跳过 Canceled 状态）。
    /// </remarks>
    public void Delete(TransferTaskViewModel task)
    {
        if (task.State is TransferTaskState.Running or TransferTaskState.Paused)
        {
            task.Cts?.Cancel();
        }
        else if (task.State is TransferTaskState.Queued)
        {
            task.State = TransferTaskState.Canceled;
        }

        ReleaseDedupKey(task);
        _transfers.RemoveTask(task);
    }

    /// <summary>释放去重键（下载=远端路径，上传=本地文件路径）。重复调用无副作用。</summary>
    private void ReleaseDedupKey(TransferTaskViewModel task)
    {
        lock (_lock)
        {
            if (task.IsUpload)
            {
                _activeUploads.Remove(task.LocalPath);
            }
            else
            {
                _activePaths.Remove(task.RemotePath);
            }
        }
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

            if (task.IsUpload)
            {
                await RunUploadAsync(task, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await RunDownloadAsync(task, cts.Token).ConfigureAwait(false);
            }

            SetTask(task, t =>
            {
                t.State = TransferTaskState.Completed;
                t.SpeedText = string.Empty;
            });
        }
        catch (OperationCanceledException)
        {
            // 取消：下载清理 .part 与断点（对齐方案 5.7：取消即删除）；上传无本地文件，会话保留供续传
            if (!task.IsUpload)
            {
                CleanPartFiles(task);
            }

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
            ReleaseDedupKey(task);
        }
    }

    /// <summary>下载分支：拉直链 → 引擎下载到 .part → 完成重命名。</summary>
    private async Task RunDownloadAsync(TransferTaskViewModel task, CancellationToken ct)
    {
        var urls = await _api.BuildDownloadUrlsAsync(task.RemotePath, ct).ConfigureAwait(false);
        Directory.CreateDirectory(task.SaveDir);
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

        await engine.DownloadAsync(partPath, checkpointPath, progress, ct).ConfigureAwait(false);

        // 完成：重命名 .part → 正式文件。
        // 目标已存在时自动递增（与上传的重名策略一致），不覆盖用户已有文件；
        // File.Move 本身不覆盖，避免「先删后移」在失败时两边都没了。
        var finalPath = ResolveLocalTargetPath(task.SaveDir, task.Name);
        File.Move(partPath, finalPath);
        SetTask(task, t => t.DetailText = finalPath);
    }

    /// <summary>上传分支：引擎秒传/分片/合并；会话（uploadid + 已传分片）跨重试复用。</summary>
    private async Task RunUploadAsync(TransferTaskViewModel task, CancellationToken ct)
    {
        // 确保在线程池创建 Progress<T>（对齐下载分支）：槽位空闲时 RunAsync 可能同步继续在
        // UI 线程执行到这里，Progress 若捕获 UI SynchronizationContext 会导致回调双跳（线程池→UI→再 Post）
        await Task.Run(() => { }).ConfigureAwait(false);

        var uploader = new Uploader(new UploaderOptions
        {
            Api = _api,
            LocalFilePath = task.LocalPath,
            RemoteDir = task.RemoteDir,
        }, task.UploadSession ?? new UploadSession());
        task.Engine = uploader;

        var progress = new Progress<UploadProgress>(p => SetTask(task, t =>
        {
            t.Progress = p.Percent;
            t.SpeedText = $"{SizeFormatter.ConvertFileSize((long)p.SpeedBytesPerSecond, 2)}/s";
        }));

        await uploader.UploadAsync(progress, ct).ConfigureAwait(false);

        // 完成：展示最终远端路径；秒传命中时明确标注（方便区分秒传与真实上传）
        var target = uploader.TargetPath ?? task.RemoteDir;
        var detail = uploader.WasRapidUpload ? $"秒传命中：{target}" : target;
        SetTask(task, t => t.DetailText = detail);
    }

    /// <summary>
    /// 解析本地最终文件名：目标已存在则自动递增 <c>名字2.ext</c>、<c>名字3.ext</c>…，
    /// 与上传的重名策略保持一致。上限 1000，避免目录里同名文件过多时死循环。
    /// </summary>
    private static string ResolveLocalTargetPath(string dir, string name)
    {
        var target = Path.Combine(dir, name);
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            return target;
        }

        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrEmpty(stem))
        {
            // dotfile（如 .gitignore）：GetFileNameWithoutExtension 返回空，退化为整名加序号
            stem = name;
            ext = string.Empty;
        }

        for (var n = 2; n <= 1000; n++)
        {
            var candidate = Path.Combine(dir, $"{stem}{n}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"目标目录同名文件过多，无法自动改名：{target}");
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
