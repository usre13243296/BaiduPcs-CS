using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BaiduPcs.Core.Models;
using BaiduPcsAPP.Services;
using BaiduPcsAPP.ViewModels;
using BaiduPcsAPP.Views;

namespace BaiduPcsAPP;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 全局异常兜底：任何未捕获/未观察异常都记录到日志，避免静默无响应
        LogUnhandledExceptions();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            var activeAccount = services.Store.ActiveAccount;

            if (activeAccount is not null)
            {
                ShowMainWindow(desktop, services, activeAccount);
            }
            else
            {
                ShowLoginWindow(desktop, services);
            }

            // 冒烟自检：BP_SMOKE_TEST=1 时启动 3 秒后自动退出（验证初始化不崩溃）
            if (Environment.GetEnvironmentVariable("BP_SMOKE_TEST") == "1")
            {
                var timer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3),
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    desktop.Shutdown();
                };
                timer.Start();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>注册全局未处理异常日志钩子，防止异常静默导致 UI 无响应。</summary>
    private static void LogUnhandledExceptions()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteLog("UnhandledException", e.ExceptionObject?.ToString() ?? string.Empty);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteLog("UnobservedTaskException", e.Exception?.ToString() ?? string.Empty);
            e.SetObserved();
        };

        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            WriteLog("DispatcherUnhandledException", e.Exception?.ToString() ?? string.Empty);
    }

    /// <summary>单份日志上限：超过后滚动为 app.1.log，避免长期运行把配置目录写满。</summary>
    private const long MaxLogBytes = 4 * 1024 * 1024;

    /// <summary>日志写锁：多线程同时 AppendAllText 会抛 IOException，最关键的异常行反而会丢。</summary>
    private static readonly object LogLock = new();

    /// <summary>追加一行日志到本地文件（串行化 + 按大小滚动）。</summary>
    private static void WriteLog(string category, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}] {message}{Environment.NewLine}";

            lock (LogLock)
            {
                var logDir = BaiduPcs.Core.Infrastructure.AppPaths.ConfigDir;
                Directory.CreateDirectory(logDir);
                var logFile = Path.Combine(logDir, "app.log");
                RollLogIfTooBig(logFile);
                File.AppendAllText(logFile, line);
            }

            Console.Error.WriteLine(line);
        }
        catch
        {
            // 日志写入失败时忽略，避免二次异常
        }
    }

    /// <summary>app.log 超过上限时滚动为 app.1.log（只保留一份历史）。滚动失败不阻断写日志。</summary>
    private static void RollLogIfTooBig(string logFile)
    {
        var info = new FileInfo(logFile);
        if (!info.Exists || info.Length < MaxLogBytes)
        {
            return;
        }

        try
        {
            var backup = Path.Combine(info.DirectoryName!, "app.1.log");
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(logFile, backup);
        }
        catch (IOException)
        {
            // 被占用等情况：继续向原文件追加，下次再试
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ShowLoginWindow(IClassicDesktopStyleApplicationLifetime desktop, AppServices services)
    {
        var window = new LoginWindow();
        var viewModel = new LoginViewModel(services, account =>
        {
            ShowMainWindow(desktop, services, account);
            window.Close();
        });
        window.DataContext = viewModel;
        desktop.MainWindow = window;
        window.Show();
    }

    private void ShowMainWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        AppServices services,
        BaiduAccount account)
    {
        var window = new MainWindow();
        var dialogs = new DialogService(window);
        var api = services.CreateApiClient(account);

        var transfers = new TransfersViewModel();
        var transferManager = new TransferManager(api, transfers);
        var fileBrowser = new FileBrowserViewModel(api, dialogs, transfers, transferManager, BaiduPcs.Core.Infrastructure.AppPaths.DefaultDownloadDir);
        var settings = new SettingsViewModel();

        var viewModel = new MainViewModel(account, fileBrowser, transfers, settings, () =>
        {
            ShowLoginWindow(desktop, services);
            window.Close();
        });

        window.DataContext = viewModel;
        desktop.MainWindow = window;
        window.Show();

        // 加载根目录与配额（异步，不阻塞窗口显示）
        _ = fileBrowser.InitializeAsync();
    }
}
