using System.Globalization;

namespace BaiduPcs.Core.Util;

/// <summary>
/// 文件大小格式化工具（对应 Go 端 pcsutil/converter size.go）。
/// </summary>
public static class SizeFormatter
{
    public const long Kb = 1L << 10;
    public const long Mb = 1L << 20;
    public const long Gb = 1L << 30;
    public const long Tb = 1L << 40;
    public const long Pb = 1L << 50;

    /// <summary>格式化文件大小，如 1.5MB；precision 默认 6 位小数（与 Go 端一致）。</summary>
    public static string ConvertFileSize(long size, int precision = 6)
    {
        if (size < 0)
        {
            return "0B";
        }

        if (size < Kb)
        {
            return $"{size}B";
        }

        if (size < Mb)
        {
            return (size / (double)Kb).ToString($"F{precision}", CultureInfo.InvariantCulture) + "KB";
        }

        if (size < Gb)
        {
            return (size / (double)Mb).ToString($"F{precision}", CultureInfo.InvariantCulture) + "MB";
        }

        if (size < Tb)
        {
            return (size / (double)Gb).ToString($"F{precision}", CultureInfo.InvariantCulture) + "GB";
        }

        if (size < Pb)
        {
            return (size / (double)Tb).ToString($"F{precision}", CultureInfo.InvariantCulture) + "TB";
        }

        return (size / (double)Pb).ToString($"F{precision}", CultureInfo.InvariantCulture) + "PB";
    }
}
