using System.Globalization;
using System.Text;

namespace BaiduPcs.Core.Crypto;

/// <summary>网盘 MD5 反混淆（对应 Go 端 baidupcs.DecryptMD5）。</summary>
public static class Md5Obfuscator
{
    public static string DecryptMd5(string rawMd5)
    {
        if (rawMd5.Length != 32)
        {
            return rawMd5;
        }

        var keyChar = rawMd5[9];
        if (IsHex(keyChar))
        {
            return rawMd5;
        }

        var sliceFirst = ((int)keyChar - 'g').ToString("x");
        var sliceSecond = rawMd5[..9] + sliceFirst + rawMd5[10..];

        var sb = new StringBuilder(32);
        for (var i = 0; i < sliceSecond.Length; i++)
        {
            var c = sliceSecond[i];
            if (c == '-')
            {
                sb.Append((15 & i).ToString("x"));
                continue;
            }

            var ok = byte.TryParse(c.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n);
            if (!ok)
            {
                return rawMd5;
            }

            sb.Append((n ^ (15 & i)).ToString("x"));
        }

        var s = sb.ToString();
        return s[8..16] + s[0..8] + s[24..32] + s[16..24];
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
}