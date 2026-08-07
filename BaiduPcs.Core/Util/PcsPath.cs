using System.Text;

namespace BaiduPcs.Core.Util;

/// <summary>
/// 网盘路径工具（/ 分隔，与本地路径无关）。
/// </summary>
public static class PcsPath
{
    /// <summary>合并网盘路径段。</summary>
    public static string Join(params string[] parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            var trimmed = part.Trim('/');
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (sb.Length == 0)
            {
                sb.Append('/');
            }
            else if (sb[^1] != '/')
            {
                sb.Append('/');
            }

            sb.Append(trimmed);
        }

        if (sb.Length == 0)
        {
            return "/";
        }

        return sb.ToString();
    }

    /// <summary>获取父目录路径（根目录的父目录仍是根目录）。</summary>
    public static string GetParent(string path)
    {
        var cleaned = Clean(path);
        if (cleaned == "/")
        {
            return "/";
        }

        var lastSlash = cleaned.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : cleaned[..lastSlash];
    }

    /// <summary>取路径的最后一个段（文件名或目录名）。</summary>
    public static string GetName(string path)
    {
        var cleaned = Clean(path);
        if (cleaned == "/")
        {
            return "/";
        }

        var lastSlash = cleaned.LastIndexOf('/');
        return lastSlash >= 0 ? cleaned[(lastSlash + 1)..] : cleaned;
    }

    /// <summary>规范化路径：以 / 开头、去尾部斜杠（根目录除外）、折叠连续斜杠。</summary>
    public static string Clean(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        var segments = path.Split('/')
            .Where(s => s.Length > 0 && s != ".")
            .ToArray();

        var stack = new List<string>();
        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            else
            {
                stack.Add(segment);
            }
        }

        if (stack.Count == 0)
        {
            return "/";
        }

        return "/" + string.Join('/', stack);
    }
}
