using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BaiduPcsAPP.ViewModels;

/// <summary>设置页（当前为占位，后续扩展配置项）。</summary>
public partial class SettingsViewModel : ViewModelBase
{
    /// <summary>版本号（从程序集 InformationalVersion 读取，随 csproj 的 Version 属性发布）。</summary>
    public string Version { get; } = GetVersion();

    public string AppDescription => "基于 .NET 10 + Avalonia 的百度网盘客户端，核心功能围绕上传与下载。";

    public string DownloadDirText => $"默认下载目录：{BaiduPcs.Core.Infrastructure.AppPaths.DefaultDownloadDir}";

    private static string GetVersion()
    {
        var info = typeof(SettingsViewModel).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrEmpty(info) ? "v0.5.9" : "v" + info.Split('+')[0];
    }
}
