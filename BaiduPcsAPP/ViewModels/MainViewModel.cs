using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BaiduPcs.Core.Models;
using BaiduPcsAPP.Services;

namespace BaiduPcsAPP.ViewModels;

/// <summary>
/// 主窗口外壳：导航、账号信息、文件浏览与传输面板。
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly Action _onLogout;

    public MainViewModel(
        BaiduAccount account,
        FileBrowserViewModel fileBrowser,
        TransfersViewModel transfers,
        SettingsViewModel settings,
        Action onLogout)
    {
        Account = account;
        FileBrowser = fileBrowser;
        Transfers = transfers;
        Settings = settings;
        _onLogout = onLogout;
    }

    public BaiduAccount Account { get; }

    public string AccountName => string.IsNullOrEmpty(Account.Name) ? $"UID {Account.Uid}" : Account.Name;

    public string AvatarText => string.IsNullOrEmpty(Account.Name) ? "?" : Account.Name[..1].ToUpperInvariant();

    public FileBrowserViewModel FileBrowser { get; }

    public TransfersViewModel Transfers { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>当前导航页索引：0=文件，1=传输，2=设置</summary>
    [ObservableProperty]
    public partial int SelectedNavIndex { get; set; }

    /// <summary>主区域页面（根据导航切换，由 ViewLocator 映射为视图）。</summary>
    public object? SelectedPage => SelectedNavIndex switch
    {
        0 => FileBrowser,
        1 => Transfers,
        _ => Settings,
    };

    /// <summary>是否显示底部传输面板（文件页显示）。</summary>
    public bool IsTransferDockVisible => SelectedNavIndex == 0;

    public string TopBarTitle => SelectedNavIndex switch
    {
        1 => "传输管理",
        2 => "设置",
        _ => "文件",
    };

    partial void OnSelectedNavIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedPage));
        OnPropertyChanged(nameof(IsTransferDockVisible));
        OnPropertyChanged(nameof(TopBarTitle));
    }

    /// <summary>
    /// 导航命令。不用 <c>RelayCommand&lt;int&gt;</c>：XAML 里的 <c>CommandParameter="0"</c> 是字符串，
    /// 与 int 参数不匹配会导致「传输 / 设置」页点了没反应（v0.6.0 的已知缺陷）。
    /// </summary>
    [RelayCommand]
    private void ShowFiles() => SelectedNavIndex = 0;

    [RelayCommand]
    private void ShowTransfers() => SelectedNavIndex = 1;

    [RelayCommand]
    private void ShowSettings() => SelectedNavIndex = 2;

    [RelayCommand]
    private void Logout()
    {
        _onLogout();
    }
}
