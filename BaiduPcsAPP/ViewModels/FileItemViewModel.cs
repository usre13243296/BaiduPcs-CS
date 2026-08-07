using BaiduPcs.Core.Models;
using BaiduPcs.Core.Util;

namespace BaiduPcsAPP.ViewModels;

/// <summary>文件列表行（包装 FileEntry 用于 UI 展示）。</summary>
public sealed partial class FileItemViewModel : ViewModelBase
{
    public FileItemViewModel(FileEntry entry)
    {
        Entry = entry;
    }

    public FileEntry Entry { get; }

    public string Name => Entry.Filename;

    public string Path => Entry.Path;

    public bool IsDir => Entry.IsDir;

    public string IconText => IsDir ? "📁" : "📄";

    public string TypeText => IsDir
        ? "文件夹"
        : (System.IO.Path.GetExtension(Name) is { Length: > 0 } ext ? ext.TrimStart('.') + " 文件" : "文件");

    public string SizeText => IsDir ? "—" : SizeFormatter.ConvertFileSize(Entry.Size, 2);

    public string ModifiedText => Entry.Mtime > 0
        ? DateTimeOffset.FromUnixTimeSeconds(Entry.Mtime).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "—";
}
