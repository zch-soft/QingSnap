using System.Text;
using System.Text.RegularExpressions;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class ClipboardHtmlBuilderTests
{
    [Fact]
    public void CfHtmlHeaderPointsAtTheUtf8Fragment()
    {
        const string fragment = "<table><tr><td>截图 Ā</td></tr></table>";
        var bytes = ClipboardHtmlBuilder.BuildCfHtml(fragment);
        var text = Encoding.UTF8.GetString(bytes);

        var startHtml = ReadOffset(text, "StartHTML");
        var endHtml = ReadOffset(text, "EndHTML");
        var startFragment = ReadOffset(text, "StartFragment");
        var endFragment = ReadOffset(text, "EndFragment");

        Assert.Equal(bytes.Length, endHtml);
        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes[startFragment..endFragment]));
        Assert.True(startHtml < startFragment);
        Assert.True(startFragment < endFragment);
        Assert.Equal(
            "<html><head><meta charset=\"utf-8\"></head><body>\r\n<!--StartFragment-->",
            Encoding.UTF8.GetString(bytes[startHtml..startFragment]));
        Assert.Contains("Version:0.9", text);
    }

    [Fact]
    public void TableContainsOneCellPerGridPositionAndSkipsExcludedSlots()
    {
        var table = new ClipboardTable(
            2,
            2,
            new Dictionary<(int Row, int Column), ClipboardTableCell>
            {
                [(1, 1)] = new(@"C:\temp\a b\1_1.png", 320, 180),
                [(1, 2)] = new(@"C:\temp\a b\1_2.png", 320, 180),
                [(2, 1)] = new(@"C:\temp\a b\2_1.png", 320, 180)
            },
            4);

        var html = ClipboardHtmlBuilder.BuildFragment(table);

        Assert.Equal(4, Regex.Matches(html, "<td").Count);
        Assert.Equal(3, Regex.Matches(html, "<img").Count);
        Assert.Equal(2, Regex.Matches(html, "<tr>").Count);
        Assert.Contains("padding:4px", html);
        Assert.Contains("file:///C:/temp/a%20b/1_1.png", html);
        Assert.Contains("width=\"320\" height=\"180\"", html);
    }

    [Fact]
    public void FileUrlsAreEscapedForHtmlAttributes()
    {
        var url = ClipboardHtmlBuilder.ToFileUrl(@"C:\temp\a&b\shot.png");
        var table = new ClipboardTable(
            1,
            1,
            new Dictionary<(int Row, int Column), ClipboardTableCell>
            {
                [(1, 1)] = new(@"C:\temp\a&b\shot.png", 10, 10)
            },
            0);

        var html = ClipboardHtmlBuilder.BuildFragment(table);

        Assert.Contains("&amp;", html);
        Assert.DoesNotContain("a&b", html);
        Assert.StartsWith("file:///", url);
    }

    [Fact]
    public void HighDpiImagesAreScaledToCssPixels()
    {
        Assert.Equal(200, ClipboardHtmlBuilder.ToCssPixels(400, 192));
        Assert.Equal(400, ClipboardHtmlBuilder.ToCssPixels(400, 96));
        Assert.Equal(1, ClipboardHtmlBuilder.ToCssPixels(0.5, 96));
    }

    private static int ReadOffset(string text, string key)
    {
        var match = Regex.Match(text, $"{key}:(\\d+)");
        Assert.True(match.Success, $"没有找到 {key}");
        return int.Parse(match.Groups[1].Value);
    }
}
