namespace BaiduPcs.Core.Api;

/// <summary>API 错误类型（对应 Go 端 pcserror.ErrType）。</summary>
public enum ApiErrorType
{
    NoError = 0,
    InternalError,
    RemoteError,
    NetError,
    JsonParseError,
    Other,
}

/// <summary>
/// 百度网盘 API 统一异常（对应 Go 端 PCSErrInfo / PanErrorInfo）。
/// </summary>
public sealed class BaiduApiException : Exception
{
    public BaiduApiException(
        string operation,
        ApiErrorType errorType,
        string message,
        int remoteCode = 0,
        string remoteMessage = "",
        Exception? inner = null)
        : base(message, inner)
    {
        Operation = operation;
        ErrorType = errorType;
        RemoteCode = remoteCode;
        RemoteMessage = remoteMessage;
    }

    /// <summary>正在进行的操作名</summary>
    public string Operation { get; }

    /// <summary>错误类型</summary>
    public ApiErrorType ErrorType { get; }

    /// <summary>远端错误代码</summary>
    public int RemoteCode { get; }

    /// <summary>远端错误消息</summary>
    public string RemoteMessage { get; }

    public bool IsRemoteError => ErrorType == ApiErrorType.RemoteError;

    public bool IsNetError => ErrorType == ApiErrorType.NetError;

    /// <summary>构造一个网络错误</summary>
    public static BaiduApiException NetError(string operation, Exception inner) =>
        new(operation, ApiErrorType.NetError, $"{operation}: 网络错误, {inner.Message}", inner: inner);

    /// <summary>构造一个 JSON 解析错误</summary>
    public static BaiduApiException JsonParseError(string operation, Exception inner) =>
        new(operation, ApiErrorType.JsonParseError, $"{operation}: json 数据解析失败, {inner.Message}", inner: inner);

    /// <summary>构造一个远端错误（PCS 风格 error_code/error_msg）</summary>
    public static BaiduApiException PcsRemote(string operation, int errorCode, string errorMsg)
    {
        var (_, msg) = ErrorMessageCatalog.FindPcsError(errorCode, errorMsg);
        return new BaiduApiException(
            operation,
            ApiErrorType.RemoteError,
            $"{operation}: 遇到错误, 远端服务器返回错误, 代码: {errorCode}, 消息: {msg}",
            errorCode,
            msg);
    }

    /// <summary>构造一个远端错误（Pan 风格 errno）</summary>
    public static BaiduApiException PanRemote(string operation, int errno)
    {
        var msg = ErrorMessageCatalog.FindPanError(errno);
        return new BaiduApiException(
            operation,
            ApiErrorType.RemoteError,
            $"{operation}: 遇到错误, 远端服务器返回错误, 代码: {errno}, 消息: {msg}",
            errno,
            msg);
    }

    /// <summary>构造一个普通错误</summary>
    public static BaiduApiException Other(string operation, string message) =>
        new(operation, ApiErrorType.Other, $"{operation}, 遇到错误, {message}");
}
