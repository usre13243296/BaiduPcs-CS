namespace BaiduPcs.Core.Api;

/// <summary>
/// 百度网盘 API 常量（对应 Go 端 baidupcs 包中的 URL/UA/AppID 常量）。
/// </summary>
public static class ApiConstants
{
    /// <summary>PCS API 地址</summary>
    public const string PcsBaiduCom = "pcs.baidu.com";

    /// <summary>网盘首页 API 地址</summary>
    public const string PanBaiduCom = "pan.baidu.com";

    /// <summary>网盘首页 API 地址 2</summary>
    public const string YunBaiduCom = "yun.baidu.com";

    /// <summary>百度网盘 appid</summary>
    public const string PanAppId = "250528";

    /// <summary>默认 AppID（pcs_config.json 默认值 266719）</summary>
    public const int DefaultAppId = 266719;

    /// <summary>网盘客户端 UA</summary>
    public const string NetdiskUa =
        "netdisk;P2SP;3.0.0.8;netdisk;11.12.3;ANG-AN00;android-android;10.0;JSbridge4.4.0;jointBridge;1.1.0;";

    /// <summary>默认浏览器 UA</summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_13_2) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/63.0.3239.132 Safari/537.36";

    /// <summary>路径分隔符</summary>
    public const string PathSeparator = "/";

    /// <summary>cookie 域</summary>
    public const string DotBaiduCom = ".baidu.com";

    /// <summary>share 接口所在域</summary>
    public const string ShareBaiduCom = "pan.baidu.com";

    /// <summary>shareid 所在的域</summary>
    public const string BaiduCom = "baidu.com";
}
