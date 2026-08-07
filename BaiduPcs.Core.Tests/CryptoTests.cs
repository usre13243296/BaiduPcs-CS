using BaiduPcs.Core.Crypto;

namespace BaiduPcs.Core.Tests;

public class NetdiskSignTests
{
    [Fact]
    public void DevUID_ProducesUppercaseMd5HexWithBar0Suffix()
    {
        // md5("abc") = 900150983cd24fb0d6963f7d28e17f72，转大写后追加 "|0"
        var result = NetdiskSign.DevUID("abc");
        Assert.Equal("900150983CD24FB0D6963F7D28E17F72|0", result);
    }

    [Fact]
    public void LocateDownloadSign_MatchesGoTestVector()
    {
        // Go 端 netdisksign/locatedownloadsign_test.go 的测试向量
        var rand = NetdiskSign.LocateDownloadSign(10086, "test_bduss", 1571140066, "O|1E67351CCE80B2CF48DB511CD77ACD9F");
        Assert.Equal("b6bb7a6f46899e181baea58798d4fdb889775c2c", rand);
    }

    [Fact]
    public void LocateDownloadUrlParam_ContainsAllFields()
    {
        var param = NetdiskSign.LocateDownloadUrlParam(10086, "test_bduss", 1571140066);
        Assert.StartsWith("time=1571140066&rand=", param);
        Assert.Contains("&devuid=", param);
        Assert.Contains("&cuid=", param);
    }

    [Fact]
    public void ShareSURLInfoSign_IsLowercaseHex()
    {
        var sign = NetdiskSign.ShareSURLInfoSign(123456789);
        Assert.Equal(32, sign.Length);
        Assert.Matches("^[a-f0-9]{32}$", sign);
    }

    [Fact]
    public void Sign2_MatchesGoBase64Vector()
    {
        // Go 端 sign2_test.go：期望 Base64 == 8RxCbsVeSzn2UjxJAAiV9QQs/WetOj2FJUGwjsMG6SgxFMWlLS/U1Q==
        var result = NetdiskSign.Sign2(
            "e8c7d729eea7b54551aa594f942decbe",
            "37dbe07ade9359c1aa70807e847f768c13360ad2");
        var base64 = Convert.ToBase64String(result);
        Assert.Equal("8RxCbsVeSzn2UjxJAAiV9QQs/WetOj2FJUGwjsMG6SgxFMWlLS/U1Q==", base64);
    }
}

public class Md5ObfuscatorTests
{
    [Fact]
    public void DecryptMd5_ValidHexMd5_ReturnsAsIs()
    {
        var md5 = "0123456789abcdef0123456789abcdef";
        Assert.Equal(md5, Md5Obfuscator.DecryptMd5(md5));
    }

    [Fact]
    public void DecryptMd5_Obfuscated_MatchesHandComputedResult()
    {
        // 手工按 Go 端算法推导的向量
        var result = Md5Obfuscator.DecryptMd5("012345678g1234567890123456789012");
        Assert.Equal("09b9f9b900000000dfd35dfd79b35753", result);
    }

    [Fact]
    public void DecryptMd5_Non32Length_ReturnsAsIs()
    {
        Assert.Equal("abc", Md5Obfuscator.DecryptMd5("abc"));
        Assert.Equal("", Md5Obfuscator.DecryptMd5(""));
    }

    [Fact]
    public void DecryptMd5_ResultLength_Is32()
    {
        // 使用各种非 hex 字符作为 index9，确保结果长度恒为 32
        foreach (var c in "ghijklmnopqrstuvwxyzGHIJKLMNOPQRSTUVWXYZ!@#$%")
        {
            var input = "012345678" + c + "1234567890123456789012";
            var output = Md5Obfuscator.DecryptMd5(input);
            Assert.Equal(32, output.Length);
        }
    }
}
