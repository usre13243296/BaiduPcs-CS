using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BaiduPcsAPP.Services;
using BaiduPcs.Core.Models;

namespace BaiduPcsAPP.ViewModels;

/// <summary>
/// 登录窗口：支持粘贴 BDUSS 登录，高级模式可附加完整 cookie 串。
/// </summary>
public partial class LoginViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly Action<BaiduAccount> _onLoggedIn;

    [ObservableProperty]
    public partial string BdsCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAdvancedMode { get; set; }

    [ObservableProperty]
    public partial string CookieString { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasErrorMessage => !string.IsNullOrEmpty(ErrorMessage);

    public bool CanLogin => !IsBusy;

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasErrorMessage));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanLogin));

    public LoginViewModel(AppServices services, Action<BaiduAccount> onLoggedIn)
    {
        _services = services;
        _onLoggedIn = onLoggedIn;
    }

    /// <summary>已有账号列表（可直接显示在登录界面）。</summary>
    public IReadOnlyList<BaiduAccount> Accounts => _services.Store.Accounts;

    [RelayCommand]
    private async Task LoginAsync()
    {
        var bduss = BdsCode.Trim();
        if (string.IsNullOrWhiteSpace(bduss))
        {
            ErrorMessage = "请先粘贴 BDUSS。获取方式：登录百度网盘网页版后，按 F12 → 应用 → Cookie → 复制 BDUSS 的值。";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var account = await _services.Login.LoginByBdussAsync(
                bduss,
                IsAdvancedMode ? CookieString : null);
            _onLoggedIn(account);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex is BaiduPcs.Core.Api.BaiduApiException apiEx
                ? apiEx.Message
                : $"登录失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
