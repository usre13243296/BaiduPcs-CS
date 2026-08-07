namespace BaiduPcs.Core.Api;

/// <summary>
/// 百度网盘错误码 → 可读消息映射（对应 Go 端 pcserror.FindPanErr / findPCSErr）。
/// </summary>
public static class ErrorMessageCatalog
{
    public const string StrSuccess = "操作成功";
    public const string StrInternalError = "内部错误";
    public const string StrRemoteError = "远端服务器返回错误";
    public const string StrNetError = "网络错误";
    public const string StrJsonParseError = "json 数据解析失败";

    /// <summary>解析 PCS 风格错误（error_code/error_msg）。</summary>
    public static (int Code, string Message) FindPcsError(int errorCode, string errorMsg)
    {
        return errorCode switch
        {
            0 => (errorCode, string.Empty),
            31045 => (errorCode, "操作失败, 可能百度帐号登录状态过期, 请尝试重新登录, 消息: " + errorMsg),
            31061 => (errorCode, "文件已存在"),
            31066 => (errorCode, "文件或目录不存在"),
            31079 => (errorCode, "秒传文件失败"),
            _ => (errorCode, errorMsg),
        };
    }

    /// <summary>根据 errno 解析网盘错误信息。</summary>
    public static string FindPanError(int errno)
    {
        return errno switch
        {
            0 => StrSuccess,
            -1 => "由于您分享了违反相关法律法规的文件，分享功能已被禁用，之前分享出去的文件不受影响。",
            -2 => "用户不存在,请刷新页面后重试",
            -3 => "文件不存在,请刷新页面后重试",
            -4 => "登录信息有误，请重新登录试试",
            -5 => "host_key和user_key无效",
            -6 => "请重新登录",
            -7 => "该分享已删除或已取消",
            -8 => "已存在同名文件",
            -9 => "文件不存在",
            -10 => "分享外链已经达到最大上限100000条，不能再次分享",
            -11 => "验证cookie无效",
            -12 => "访问密码错误",
            -14 => "对不起，短信分享每天限制20条，你今天已经分享完，请明天再来分享吧！",
            -15 => "对不起，邮件分享每天限制20封，你今天已经分享完，请明天再来分享吧！",
            -16 => "对不起，该文件已经限制分享！",
            -17 => "文件分享超过限制",
            -19 => "需要输入验证码",
            -21 => "分享已取消或分享信息无效",
            -30 => "文件已存在",
            -31 => "文件保存失败",
            -33 => "一次支持操作999个，减点试试吧",
            -62 => "可能需要输入验证码",
            -70 => "你分享的文件中包含病毒或疑似病毒，为了你和他人的数据安全，换个文件分享吧",
            2 => "请稍后再试, 或更换保存路径",
            3 => "未登录或帐号无效",
            4 => "存储好像出问题了，请稍候再试",
            105 => "啊哦，链接错误没找到文件，请打开正确的分享链接",
            108 => "文件名有敏感词，优化一下吧",
            110 => "分享次数超出限制，可以到“我的分享”中查看已分享的文件链接",
            112 => "页面已过期，请刷新后重试",
            113 => "签名错误",
            114 => "当前任务不存在，保存失败",
            115 => "该文件禁止分享",
            132 => "您的帐号可能存在安全风险，为了确保为您本人操作，请先进行安全验证。",
            9019 => "accesstoken未设置或过期, 请使用setastoken命令设置`",
            _ => "未知错误",
        };
    }
}
