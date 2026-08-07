using BaiduPcs.Core.Auth;
using BaiduPcs.Core.Util;

namespace BaiduPcs.Core.Tests;

public class CookieParserTests
{
    [Fact]
    public void Parse_NormalCookieString_ReturnsPairs()
    {
        var cookies = CookieParser.Parse("BDUSS=abc123; STOKEN=xyz");
        Assert.Equal(2, cookies.Count);
        Assert.Equal("BDUSS", cookies[0].Key);
        Assert.Equal("abc123", cookies[0].Value);
        Assert.Equal("STOKEN", cookies[1].Key);
        Assert.Equal("xyz", cookies[1].Value);
    }

    [Fact]
    public void Parse_TrimsSpaces()
    {
        var cookies = CookieParser.Parse("  BDUSS  =  value  ");
        Assert.Single(cookies);
        Assert.Equal("value", cookies[0].Value);
    }

    [Fact]
    public void Parse_Empty_ReturnsEmpty()
    {
        Assert.Empty(CookieParser.Parse(""));
        Assert.Empty(CookieParser.Parse(null!));
    }

    [Fact]
    public void Parse_MalformedSegments_Skipped()
    {
        var cookies = CookieParser.Parse("badcookie; BDUSS=ok");
        Assert.Single(cookies);
        Assert.Equal("BDUSS", cookies[0].Key);
    }

    [Fact]
    public void Session_SetCookie_DeduplicatesCaseInsensitive()
    {
        var session = BaiduSession.FromBduss("v1");
        session.SetCookie("bduss", "v2");
        Assert.Equal("v2", session.Bduss);
        Assert.Single(session.Cookies);
    }

    [Fact]
    public void Session_BuildCookieHeader_JoinsSemicolon()
    {
        var session = BaiduSession.FromBduss("v1");
        session.SetCookie("STOKEN", "v2");
        Assert.Equal("BDUSS=v1; STOKEN=v2", session.BuildCookieHeader());
    }
}

public class SizeFormatterTests
{
    [Theory]
    [InlineData(-1, "0B")]
    [InlineData(0, "0B")]
    [InlineData(1, "1B")]
    [InlineData(1023, "1023B")]
    [InlineData(1024, "1.000000KB")]
    [InlineData(1536, "1.500000KB")]
    [InlineData(1048576, "1.000000MB")]
    [InlineData(1073741824, "1.000000GB")]
    public void ConvertFileSize_BasicValues(long size, string expected)
    {
        Assert.Equal(expected, SizeFormatter.ConvertFileSize(size));
    }
}

public class PcsPathTests
{
    [Theory]
    [InlineData("/a/b", "/a/b")]
    [InlineData("a/b", "/a/b")]
    [InlineData("/a//b/", "/a/b")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/a/../b", "/b")]
    public void Clean_Normalizes(string input, string expected)
    {
        Assert.Equal(expected, PcsPath.Clean(input));
    }

    [Theory]
    [InlineData("/a/b/c", "/a/b")]
    [InlineData("/a", "/")]
    [InlineData("/", "/")]
    public void GetParent_ReturnsParent(string input, string expected)
    {
        Assert.Equal(expected, PcsPath.GetParent(input));
    }

    [Theory]
    [InlineData("/a/b.txt", "b.txt")]
    [InlineData("/a/b/", "b")]
    [InlineData("/", "/")]
    public void GetName_ReturnsName(string input, string expected)
    {
        Assert.Equal(expected, PcsPath.GetName(input));
    }

    [Fact]
    public void Join_CombinesSegments()
    {
        Assert.Equal("/a/b/c", PcsPath.Join("a", "/b/", "c"));
        Assert.Equal("/", PcsPath.Join("", "/"));
        Assert.Equal("/a", PcsPath.Join("/a", "/"));
    }
}
