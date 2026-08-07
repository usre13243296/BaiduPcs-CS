using CommunityToolkit.Mvvm.ComponentModel;

namespace BaiduPcsAPP.ViewModels;

/// <summary>设置页（当前为占位，后续扩展配置项）。</summary>
public partial class SettingsViewModel : ViewModelBase
{
    public string Version => "v0.5.0";

    public string AppDescription => "基于 .NET 10 + Avalonia 的百度网盘客户端，核心功能围绕上传与下载。";

    public string DownloadDirText => $"默认下载目录：{BaiduPcs.Core.Infrastructure.AppPaths.DefaultDownloadDir}";
}
