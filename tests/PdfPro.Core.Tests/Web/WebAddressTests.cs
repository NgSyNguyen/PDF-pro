using PdfPro.Core.Web;

namespace PdfPro.Core.Tests.Web;

/// <summary>What "Trang web sang PDF" takes for a page, and the name it gives the PDF.</summary>
public sealed class WebAddressTests
{
    [Theory]
    [InlineData("https://www.example.com/tin-tuc?id=3", "https://www.example.com/tin-tuc?id=3")]
    [InlineData("  http://example.com  ", "http://example.com/")]
    [InlineData("www.example.com", "https://www.example.com/")]
    [InlineData("example.com/gioi-thieu", "https://example.com/gioi-thieu")]
    [InlineData("localhost:8080/bao-cao", "https://localhost:8080/bao-cao")]
    [InlineData("\"https://example.com/a\"", "https://example.com/a")]
    public void WebAddresses_AreReadAsPages(string text, string expected)
        => WebAddress.Parse(text)!.AbsoluteUri.ShouldBe(expected);

    [Fact]
    public void VietnameseDomains_AreReadAsPages()
        => WebAddress.Parse("bộ-ngoại-giao.vn/tin-tức")!.IdnHost.ShouldStartWith("xn--");

    [Theory]
    [InlineData(@"C:\Tài liệu\trang.html")]
    [InlineData("file:///C:/Tai%20lieu/trang.htm")]
    public void HtmlFiles_AreReadAsPages(string text)
    {
        var page = WebAddress.Parse(text).ShouldNotBeNull();

        page.IsFile.ShouldBeTrue();
        Path.IsPathFullyQualified(page.LocalPath).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:ai@example.com")]
    [InlineData("ms-settings:privacy")]
    [InlineData("ftp://example.com/a")]
    [InlineData("file://server/share/a.html")]
    [InlineData(@"\\server\share\a.html")]
    [InlineData("trang.html")]
    [InlineData("./trang.html")]
    [InlineData("hello world")]
    [InlineData("https://intranet/")]
    [InlineData("example")]
    public void Others_AreNotPages(string? text)
        => WebAddress.Parse(text).ShouldBeNull();

    [Theory]
    [InlineData("https://www.example.com/tin-tuc/gioi-thieu.html", null, "example.com - gioi-thieu")]
    [InlineData("https://example.com/", null, "example.com")]
    [InlineData("https://example.com/a", "Thông báo: lịch nghỉ Tết?", "Thông báo lịch nghỉ Tết")]
    [InlineData("file:///C:/web/bao%20cao.html", null, "bao cao")]
    public void ThePdf_IsNamedAfterTheTitle_ElseTheAddress(string address, string? title, string expected)
        => WebAddress.FileNameOf(new Uri(address), title).ShouldBe(expected);

    [Theory]
    [InlineData("trang.HTML", true)]
    [InlineData("trang.htm", true)]
    [InlineData("luu.mhtml", true)]
    [InlineData("tai-lieu.pdf", false)]
    [InlineData(null, false)]
    public void HtmlFiles_AreKnownByTheirExtension(string? path, bool html)
        => WebAddress.IsHtmlFile(path).ShouldBe(html);
}
