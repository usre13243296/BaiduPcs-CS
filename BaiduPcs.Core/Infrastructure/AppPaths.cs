namespace BaiduPcs.Core.Infrastructure;

/// <summary>
/// 跨平台应用数据目录。Windows: %APPDATA%/BaiduPcsApp；
/// Linux: ~/.config/BaiduPcsApp；macOS: ~/Library/Application Support/BaiduPcsApp。
/// </summary>
public static class AppPaths
{
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BaiduPcsApp");

    /// <summary>账号配置文件</summary>
    public static string AccountStorePath => Path.Combine(ConfigDir, "accounts.json");

    /// <summary>下载默认保存目录</summary>
    public static string DefaultDownloadDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BaiduPcs");
}
