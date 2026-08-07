using System.Security.Cryptography;
using System.Text;

namespace BaiduPcs.Core.Crypto;

/// <summary>
/// 网盘签名工具（对应 Go 端 baidupcs/netdisksign）。
/// </summary>
public static class NetdiskSign
{
    // 对应 Go 端 locatedownloadsign.go 中硬编码的盐
    private static readonly byte[] SignSalt =
    {
        0x65, 0x62, 0x72, 0x63, 0x55, 0x59, 0x69, 0x75, 0x78, 0x61,
        0x5a, 0x76, 0x32, 0x58, 0x47, 0x75, 0x37, 0x4b, 0x49, 0x59,
        0x4b, 0x78, 0x55, 0x72, 0x71, 0x66, 0x6e, 0x4f, 0x66, 0x70,
        0x44, 0x46,
    };

    /// <summary>devuid = 大写 MD5(feature) + "|0"</summary>
    public static string DevUID(string feature)
    {
        var md5Bytes = MD5.HashData(Encoding.UTF8.GetBytes(feature));
        return Convert.ToHexString(md5Bytes) + "|0";
    }

    /// <summary>
    /// 计算 locatedownload 的 rand 值（与 Go 端 Sign 一致）。
    /// </summary>
    public static string LocateDownloadSign(long uid, string bduss, long unixSeconds, string devuid)
    {
        // bdussSha1Hex = SHA1(bduss) 小写十六进制
        var bdussSha1 = SHA1.HashData(Encoding.UTF8.GetBytes(bduss));
        var bdussSha1Hex = Convert.ToHexString(bdussSha1).ToLowerInvariant();

        var buffer = new List<byte>();
        buffer.AddRange(Encoding.ASCII.GetBytes(bdussSha1Hex));
        buffer.AddRange(Encoding.ASCII.GetBytes(uid.ToString()));
        buffer.AddRange(SignSalt);
        buffer.AddRange(Encoding.ASCII.GetBytes(unixSeconds.ToString()));
        buffer.AddRange(Encoding.ASCII.GetBytes(devuid));

        var randSha1 = SHA1.HashData(buffer.ToArray());
        return Convert.ToHexString(randSha1).ToLowerInvariant();
    }

    /// <summary>
    /// 生成 locatedownload 完整 URL 参数串：time=..&rand=..&devuid=..&cuid=..
    /// </summary>
    public static string LocateDownloadUrlParam(long uid, string bduss, long unixSeconds)
    {
        var devuid = DevUID(bduss);
        var rand = LocateDownloadSign(uid, bduss, unixSeconds, devuid);
        return $"time={unixSeconds}&rand={rand}&devuid={devuid}&cuid={devuid}";
    }

    /// <summary>ShareSURLInfo 签名：MD5(shareId + "_sharesurlinfo!@#") 小写十六进制字符串</summary>
    public static string ShareSURLInfoSign(long shareId)
    {
        var input = Encoding.UTF8.GetBytes(shareId.ToString() + "_sharesurlinfo!@#");
        var hash = MD5.HashData(input);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Sign2：魔改 RC4 流加密（与 Go 端 sign2.go 一致）。
    /// j 为 key（sign3），r 为待加密原文（sign1），返回 XOR 后的字节。
    /// </summary>
    public static byte[] Sign2(string key, string data)
    {
        var j = key.ToCharArray();
        var r = data.ToCharArray();
        var a = new int[256];
        var p = new int[256];
        var v = j.Length;
        var o = new byte[r.Length];

        if (v == 0)
        {
            return o;
        }

        for (var q = 0; q < 256; q++)
        {
            a[q] = j[q % v];
            p[q] = q;
        }

        var u = 0;
        for (var q = 0; q < 256; q++)
        {
            u = (u + p[q] + a[q]) % 256;
            (p[q], p[u]) = (p[u], p[q]);
        }

        u = 0;
        var i = 0;
        for (var q = 0; q < r.Length; q++)
        {
            i = (i + 1) % 256;
            u = (u + p[i]) % 256;
            (p[i], p[u]) = (p[u], p[i]);
            var k = p[(p[i] + p[u]) % 256];
            o[q] = (byte)(r[q] ^ k);
        }

        return o;
    }
}