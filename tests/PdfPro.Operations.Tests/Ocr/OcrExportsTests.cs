using System.Text;
using System.Xml.Linq;
using PdfPro.Operations.Ocr;

namespace PdfPro.Operations.Tests.Ocr;

/// <summary>An OCR run saved as plain text, and as one hOCR document of all its pages.</summary>
public sealed class OcrExportsTests
{
    private static string Hocr(string word) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xml:lang="en" lang="en">
         <head>
          <title></title>
          <meta name='ocr-system' content='tesseract 5.3.4' />
         </head>
         <body>
          <div class='ocr_page' id='page_1' title='image "/tmp/pdfpro-ocr-0123/page.png"; bbox 0 0 2480 3508; ppageno 0; scan_res 300 300'>
           <div class='ocr_carea' id='block_1_1' title="bbox 285 293 2023 433">
            <p class='ocr_par' id='par_1_1' lang='vie'>
             <span class='ocr_line' id='line_1_1'>
              <span class='ocrx_word' id='word_1_1' title='bbox 309 293 406 340; x_wconf 96'>{word}</span>
             </span>
            </p>
           </div>
          </div>
         </body>
        </html>
        """;

    private static readonly OcrReport Report = new(
        [
            new OcrPageReport(0, OcrPageOutcome.Recognized, 91, 1, 0, 0, "Ủy ban\nnhân dân", Hocr("Ủy")),
            new OcrPageReport(1, OcrPageOutcome.Blank, null, 0, 0, 0, string.Empty),
            new OcrPageReport(4, OcrPageOutcome.Recognized, 45, 1, 180, 0, "Điều 1.", Hocr("Điều")),
            new OcrPageReport(5, OcrPageOutcome.HadText, null, 0, 0, 0, string.Empty),
        ],
        60,
        [],
        [],
        TimeSpan.FromSeconds(3));

    [Fact]
    public void TheText_IsThePagesText_AFormFeedBetweenPages_WindowsLineEnds()
    {
        Encoding.UTF8.GetString(OcrExports.Text(Report)).ShouldBe("Ủy ban\r\nnhân dân\fĐiều 1.");
        OcrExports.Text(Report)[0].ShouldNotBe((byte)0xEF, "no byte order mark");
        Report.LowConfidence.Select(p => p.PageIndex).ShouldBe([4]);
        Report.Turned.Select(p => p.PageIndex).ShouldBe([4]);
    }

    [Fact]
    public void TheHocr_HoldsEveryPage_WithUniqueIds_AndNoTemporaryPath()
    {
        var hocr = OcrExports.Hocr(Report).ShouldNotBeNull();

        var document = XDocument.Parse(hocr);
        var pages = document.Descendants().Where(e => (string?)e.Attribute("class") == "ocr_page").ToList();
        pages.Select(p => (string?)p.Attribute("id")).ShouldBe(["page_1", "page_5"]);
        pages[1].Attribute("title")!.Value.ShouldBe("bbox 0 0 2480 3508; ppageno 4; scan_res 300 300");
        var ids = document.Descendants().Select(e => (string?)e.Attribute("id")).OfType<string>().ToList();
        ids.Distinct().Count().ShouldBe(ids.Count);
        ids.ShouldContain("word_5_1");
        hocr.ShouldNotContain("/tmp/");
        document.Descendants().Where(e => (string?)e.Attribute("class") == "ocrx_word").Select(e => e.Value).ShouldBe(["Ủy", "Điều"]);
        OcrExports.Hocr(Report with { Pages = [Report.Pages[1]] }).ShouldBeNull();
    }
}
