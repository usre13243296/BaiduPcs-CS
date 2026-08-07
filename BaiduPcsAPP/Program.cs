using Avalonia;
using System;
using System.Text;

namespace BaiduPcsAPP;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 注册 CodePages 编码提供程序：百度 PCS 返回的 Content-Type 为 charset=utf8（非标准 UTF-8），
        // 不注册会导致 Encoding.GetEncoding("utf8") 抛异常，进而引发 UI 无响应。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
