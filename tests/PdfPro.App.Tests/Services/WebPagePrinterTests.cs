using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.App.Services.Web;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Operations;
using PdfPro.Core.Web;
using UglyToad.PdfPig;

namespace PdfPro.App.Tests.Services;

/// <summary>
/// HTML files of this computer printed by the WebView2 Runtime installed on it (skipped without one) — no network: the
/// pages are written here.
/// </summary>
public sealed class WebPagePrinterTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly WebView2PagePrinter _printer;

    public WebPagePrinterTests()
        => _printer = new WebView2PagePrinter(Path.Combine(Path.GetTempPath(), "pdfpro-tests-webview2"), NullLogger<WebView2PagePrinter>.Instance);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _printer.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task AVietnameseHtmlFile_BecomesAnA4Pdf_WithItsTextAndTitle()
    {
        Assert.SkipWhen(_printer.RuntimeVersion is null, "This Windows has no WebView2 Runtime.");
        var page = WritePage("thông báo.html", "Thông báo thử", Body(paragraphs: 3));
        var stages = new List<string>();

        var result = await _printer.PrintAsync(new Uri(page), _temp.Combine("ra", "thông báo.pdf"), new WebPrintSettings(), overwrite: false, new Progress<OperationProgress>(p => stages.Add(p.StageKey ?? string.Empty)), Token);

        result.Title.ShouldBe("Thông báo thử");
        stages.ShouldContain(WebView2PagePrinter.PrintingStage);
        File.Exists(result.Output).ShouldBeTrue();
        using var pdf = PdfDocument.Open(result.Output);
        var first = pdf.GetPage(1);
        first.Width.ShouldBe(595.3, 1.5, "A4 wide");
        first.Height.ShouldBe(841.9, 1.5, "A4 high");
        var text = string.Concat(pdf.GetPages().Select(p => string.Concat(p.Letters.Select(l => l.Value)))).Normalize(NormalizationForm.FormC);
        text.Replace(" ", string.Empty, StringComparison.Ordinal).ShouldContain("CỘNGHÒAXÃHỘICHỦNGHĨAVIỆTNAM");
        text.ShouldContain("1.234.000");
        Directory.GetFiles(Path.GetDirectoryName(result.Output)!).ShouldBe([result.Output], "nothing else is left beside it");
    }

    [Fact]
    public async Task ALongPage_FlowsOverSeveralPages_SidewaysWhenAsked_AndAnExistingFileIsKeptUnlessReplaced()
    {
        Assert.SkipWhen(_printer.RuntimeVersion is null, "This Windows has no WebView2 Runtime.");
        var page = WritePage("dài.html", "Dài", Body(paragraphs: 120));
        var output = _temp.Combine("dài.pdf");
        await File.WriteAllTextAsync(output, "cũ", Token);

        await Should.ThrowAsync<IOException>(() => _printer.PrintAsync(new Uri(page), output, new WebPrintSettings(), overwrite: false, null, Token));
        (await File.ReadAllTextAsync(output, Token)).ShouldBe("cũ");
        var result = await _printer.PrintAsync(new Uri(page), output, new WebPrintSettings(WebPaper.Letter, Landscape: true, WebMargins.Wide), overwrite: true, null, Token);

        using var pdf = PdfDocument.Open(result.Output);
        pdf.NumberOfPages.ShouldBeGreaterThan(2);
        pdf.GetPage(1).Width.ShouldBe(792, 1.5, "Letter sideways");
        pdf.GetPage(1).Height.ShouldBe(612, 1.5);
    }

    [Fact]
    public async Task AFileThatIsNotThere_DoesNotLoad_AndNothingIsWritten()
    {
        Assert.SkipWhen(_printer.RuntimeVersion is null, "This Windows has no WebView2 Runtime.");
        var output = _temp.Combine("không có.pdf");

        var failure = await Should.ThrowAsync<WebPrintException>(() => _printer.PrintAsync(new Uri(_temp.Combine("không có.html")), output, new WebPrintSettings(), overwrite: false, null, Token));

        failure.Failure.ShouldBe(WebPrintFailure.Unreachable);
        Directory.GetFiles(_temp.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyWebAddressesAndFilesOfThisComputer_ArePrinted()
        => await Should.ThrowAsync<ArgumentException>(() => _printer.PrintAsync(new Uri("ftp://example.com/a"), _temp.Combine("a.pdf"), new WebPrintSettings(), false, null, Token));

    private string WritePage(string name, string title, string body)
    {
        var path = _temp.Combine(name);
        File.WriteAllText(path, $"<!doctype html><html lang=\"vi\"><head><meta charset=\"utf-8\"><title>{title}</title><style>body{{font-family:Arial,sans-serif}}</style></head><body>{body}</body></html>", Encoding.UTF8);
        return path;
    }

    private static string Body(int paragraphs)
    {
        var builder = new StringBuilder("<h1>CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM</h1><p style=\"background:#dde\">Độc lập - Tự do - Hạnh phúc</p>");
        builder.Append("<table border=\"1\"><tr><th>Mục</th><th>Số tiền</th></tr><tr><td>Chi phí</td><td>1.234.000</td></tr></table>");
        for (var i = 1; i <= paragraphs; i++)
        {
            builder.Append("<p>Đoạn ").Append(i).Append(": Ủy ban nhân dân xã Mẫu Thử thông báo lịch tiếp công dân trong tháng.</p>");
        }

        return builder.ToString();
    }
}
