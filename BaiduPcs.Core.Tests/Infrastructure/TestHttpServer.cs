using System.Net;

namespace BaiduPcs.Core.Tests.Infrastructure;

/// <summary>记录到的请求。</summary>
public sealed record RecordedRequest(string Path, string? Range, string? UserAgent, int StatusCode, string? ContentRange);

/// <summary>
/// 本地 HTTP 测试服务器：注册文件（支持 Range/206 + x-bs-file-size 头）或自定义行为
/// （限流 / 错误码 / 断流等），并记录所有请求供断言。
/// 下载引擎测试全部走本地服务器，不访问真实网络。
/// </summary>
public sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly List<Func<HttpListenerContext, TestHttpServer, Task<bool>>> _behaviors = new();
    private readonly object _lock = new();

    /// <summary>收到的请求记录（响应处理完成后追加）。</summary>
    public List<RecordedRequest> Requests { get; } = new();

    public string BaseUrl { get; }

    public TestHttpServer()
    {
        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(HandleLoop);
    }

    /// <summary>注册一个文件（支持 Range/206、x-bs-file-size、Content-Range 头）。</summary>
    public void AddFile(string path, byte[] content)
    {
        _files[path] = content;
    }

    /// <summary>读取已注册文件内容（供行为里使用）。</summary>
    public byte[]? GetFile(string path)
        => _files.TryGetValue(path, out var content) ? content : null;

    /// <summary>注册自定义行为（按注册顺序执行，返回 true 表示已处理并短路；返回 false 交给后续行为/文件）。</summary>
    public void AddBehavior(Func<HttpListenerContext, TestHttpServer, Task<bool>> handler)
    {
        _behaviors.Add(handler);
    }

    /// <summary>指定路径的已记录请求次数。</summary>
    public int RequestCount(string path)
    {
        lock (_lock)
        {
            return Requests.Count(r => r.Path == path);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        _listener.Close();
        try
        {
            _loop.Wait(1000);
        }
        catch
        {
        }
    }

    private async Task HandleLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(ctx));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var range = ctx.Request.Headers["Range"];
        var ua = ctx.Request.Headers["User-Agent"];

        try
        {
            foreach (var behavior in _behaviors)
            {
                if (await behavior(ctx, this))
                {
                    return;
                }
            }

            if (_files.TryGetValue(path, out var content))
            {
                ServeFile(ctx, content, range);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
        catch
        {
            // 断流等模拟场景会抛 IO 异常，忽略并照常记录
        }
        finally
        {
            // 记录实际状态码：行为短路（未走文件响应）时 StatusCode 为行为设置的值（默认 200）
            var contentRange = ctx.Response.Headers["Content-Range"];
            lock (_lock)
            {
                Requests.Add(new RecordedRequest(path, range, ua, (int)ctx.Response.StatusCode, contentRange));
            }
        }
    }

    /// <summary>支持 Range 的文件响应：206 + x-bs-file-size + Content-Range；无 Range 时 200 全量。</summary>
    private void ServeFile(HttpListenerContext ctx, byte[] content, string? rangeHeader)
    {
        var resp = ctx.Response;
        resp.AddHeader("x-bs-file-size", content.Length.ToString());
        resp.AddHeader("Accept-Ranges", "bytes");

        if (rangeHeader is not null && TryParseRange(rangeHeader, content.Length, out var start, out var end))
        {
            resp.StatusCode = 206;
            resp.AddHeader("Content-Range", $"bytes {start}-{end - 1}/{content.Length}");
            resp.ContentLength64 = end - start;
            resp.OutputStream.Write(content, (int)start, (int)(end - start));
        }
        else
        {
            resp.StatusCode = 200;
            resp.ContentLength64 = content.Length;
            resp.OutputStream.Write(content, 0, content.Length);
        }

        resp.Close();
    }

    /// <summary>解析 "bytes=start-end"（HTTP 闭区间），输出半开区间 [start, end)。</summary>
    private static bool TryParseRange(string header, long total, out long start, out long end)
    {
        start = 0;
        end = 0;

        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header["bytes=".Length..];
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        if (spec[..dash].Length > 0 && !long.TryParse(spec[..dash], out start))
        {
            return false;
        }

        var endPart = spec[(dash + 1)..];
        if (endPart.Length > 0 && !long.TryParse(endPart, out end))
        {
            return false;
        }

        end = end <= 0 ? total : end + 1; // 闭区间 → 开区间
        end = Math.Min(end, total);
        return start < end;
    }

    private static int GetFreePort()
    {
        using var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }
}
