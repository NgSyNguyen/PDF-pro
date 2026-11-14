using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using NSubstitute;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Operations;
using PdfPro.Core.Word;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Security;
using PdfPro.Operations.Tests.TestSupport;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Converting;

/// <summary>
/// "PDF sang Word" (convert.toWord): editable paragraphs in reading order with their fonts, weights and alignment,
/// ruled tables as Word tables, pictures, a page break per page; the document read back by a real word processor where
/// one is installed (WPS Office on the development computer).
/// </summary>
public sealed class ToWordOperationTests : IDisposable
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AVietnameseDocument_BecomesParagraphsInReadingOrder_WithTheirStyles()
    {
        var output = _temp.Combine("văn bản.docx");

        var result = await new ToWordOperation().WriteAsync(Samples.VanBanVi, null, output, new ToWordParams(), null, Token);

        result.Output.ShouldBe(output);
        result.PageCount.ShouldBe(14);
        result.Paragraphs.ShouldBeGreaterThan(50);
        result.PagesWithoutText.ShouldBeEmpty();
        var body = Body(output);
        var text = string.Join('\n', Paragraphs(body));
        text.ShouldBe(text.Normalize(NormalizationForm.FormC));
        text.IndexOf("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", StringComparison.Ordinal).ShouldBeGreaterThanOrEqualTo(0);
        text.IndexOf("Độc lập", StringComparison.Ordinal).ShouldBeGreaterThan(text.IndexOf("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", StringComparison.Ordinal), "read in order");
        body.Descendants(W + "br").Count(b => (string?)b.Attribute(W + "type") == "page").ShouldBe(13, "a page break between pages");

        // The two halves of the administrative heading stay side by side: one row of a table without borders.
        var heading = body.Descendants(W + "tr").First(r => Cells(r).Any(c => c.Contains("ỦY BAN NHÂN DÂN", StringComparison.Ordinal)));
        Cells(heading).ShouldContain(c => c.Contains("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", StringComparison.Ordinal));
        heading.Ancestors(W + "tbl").First().Descendants(W + "tblBorders").Elements().ShouldAllBe(b => (string?)b.Attribute(W + "val") == "nil");
        text.ShouldNotContain("Trang 1/", customMessage: "the running footer of the PDF's pages is left out");
        body.Descendants(W + "b").ShouldNotBeEmpty("bold text stays bold");
        body.Descendants(W + "jc").Select(j => (string?)j.Attribute(W + "val")).ShouldContain("center", "the centred heading stays centred");
        var fonts = body.Descendants(W + "rFonts").Select(f => (string?)f.Attribute(W + "ascii")).Distinct().ToList();
        fonts.ShouldNotBeEmpty();
        fonts.ShouldAllBe(f => f != null && !f.Contains('+') && !f.EndsWith("MT", StringComparison.Ordinal), "family names, not PDF font names");
        Directory.GetFileSystemEntries(_temp.Path).ShouldBe([output], "nothing else is left");
    }

    [Fact]
    public async Task ARuledTable_BecomesAWordTable_AndAnUnruledOneStaysText()
    {
        var output = _temp.Combine("bảng.docx");

        var result = await new ToWordOperation().WriteAsync(Samples.Tables, null, output, new ToWordParams(), null, Token);

        result.Tables.ShouldBe(1);
        var body = Body(output);
        var table = body.Descendants(W + "tbl").Where(t => t.Descendants(W + "tblStyle").Any()).ShouldHaveSingleItem("one ruled table");
        var rows = table.Elements(W + "tr").Select(r => r.Elements(W + "tc").Select(c => string.Concat(c.Descendants(W + "t").Select(t => t.Value))).ToList()).ToList();
        rows.Count.ShouldBe(7);
        rows[0].ShouldBe(["STT", "Họ và tên", "Đơn vị", "Số tiền (đồng)"]);
        rows[1].ShouldBe(["1", "Nguyễn Văn An", "Phòng Kế toán", "1.250.000"]);
        var text = string.Join('\n', body.Descendants(W + "p").Where(p => !p.Ancestors(W + "tbl").Any(t => t == table)).Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value))));
        text.ShouldNotContain("Nguyễn Văn An", customMessage: "the table's text is not repeated as paragraphs");
        text.ShouldContain("Giấy in A4", customMessage: "the table without rules stays text (its columns side by side)");

        var withoutTables = await new ToWordOperation().WriteAsync(Samples.Tables, null, _temp.Combine("chữ.docx"), new ToWordParams(Tables: false), null, Token);
        withoutTables.Tables.ShouldBe(0);
        string.Join('\n', Paragraphs(Body(withoutTables.Output))).ShouldContain("Nguyễn Văn An");
    }

    [Fact]
    public async Task AScan_BecomesItsPictures_AndItsPagesAreReportedForOcr()
    {
        var output = _temp.Combine("bản quét.docx");

        var result = await new ToWordOperation().WriteAsync(Samples.ScanVi, null, output, new ToWordParams(Pages: "1-3"), null, Token);

        result.PageCount.ShouldBe(3);
        result.Paragraphs.ShouldBe(0);
        result.PagesWithoutText.ShouldBe([0, 1, 2]);
        result.Pictures.ShouldBe(3);
        using var zip = ZipFile.OpenRead(output);
        var media = zip.Entries.Where(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal)).ToList();
        media.Count.ShouldBe(3);
        media.ShouldAllBe(e => e.FullName.EndsWith(".jpeg", StringComparison.Ordinal), "JPEG scans stay JPEG");
        using var pdf = PigDocument.Open(Samples.ScanVi);
        var original = pdf.GetPage(1).GetImages().Single().RawMemory.ToArray();
        using var first = zip.GetEntry("word/media/image1.jpeg")!.Open();
        using var copy = new MemoryStream();
        await first.CopyToAsync(copy, Token);
        copy.ToArray().ShouldBe(original, "byte for byte");

        var none = await new ToWordOperation().WriteAsync(Samples.ScanVi, null, _temp.Combine("không ảnh.docx"), new ToWordParams(Pages: "1", Pictures: false), null, Token);
        none.Pictures.ShouldBe(0);
    }

    [Fact]
    public async Task APageRange_AndAProtectedFile_AreTakenIntoAccount()
    {
        var part = await new ToWordOperation().WriteAsync(Samples.BasicVi, null, _temp.Combine("hai trang.docx"), new ToWordParams(Pages: "2-3"), null, Token);
        part.PageCount.ShouldBe(2);
        Body(part.Output).Descendants(W + "br").Count().ShouldBe(1);

        (await new ToWordOperation().WriteAsync(Samples.Encrypted, "test123", _temp.Combine("mật.docx"), new ToWordParams(), null, Token)).Paragraphs.ShouldBeGreaterThan(0);
        await Should.ThrowAsync<DocumentPasswordException>(() => new ToWordOperation().WriteAsync(Samples.Encrypted, null, _temp.Combine("không.docx"), new ToWordParams(), null, Token));
        await Should.ThrowAsync<OperationValidationException>(() => new ToWordOperation().WriteAsync(Samples.BasicVi, null, _temp.Combine("sai.docx"), new ToWordParams(Pages: "9"), null, Token));
        File.Exists(_temp.Combine("không.docx")).ShouldBeFalse();
        File.Exists(_temp.Combine("sai.docx")).ShouldBeFalse();
        Directory.GetFiles(_temp.Path, "*.tmp").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ABCDEF+TimesNewRomanPSMT", "Times New Roman")]
    [InlineData("TimesNewRomanPS-BoldMT", "Times New Roman")]
    [InlineData("Arial,Bold", "Arial")]
    [InlineData("ArialMT", "Arial")]
    [InlineData("QWERTY+NotoSans-Regular", "Noto Sans")]
    [InlineData("BeVietnamPro-SemiBold", "Be Vietnam Pro")]
    [InlineData("SegoeUI", "Segoe UI")]
    [InlineData("Calibri-BoldItalic", "Calibri")]
    [InlineData(".VnTime", ".VnTime")]
    [InlineData(null, "Times New Roman")]
    public void PdfFontNames_BecomeFamilyNames(string? pdf, string family)
        => ToWordOperation.FamilyOf(pdf).ShouldBe(family);

    [Fact]
    public async Task TheDocument_OpensInAWordProcessor_WithItsText()
    {
        var automation = new OfficeAutomation();
        var application = automation.Find(OfficeDocumentFamily.Text);
        Assert.SkipUnless(OperatingSystem.IsWindows() && application is not null, "No word processor (Microsoft Word, WPS Office) is installed");
        var docx = _temp.Combine("hợp đồng.docx");
        await new ToWordOperation().WriteAsync(Samples.ContractV1, null, docx, new ToWordParams(), null, Token);
        var tables = _temp.Combine("bảng.docx");
        await new ToWordOperation().WriteAsync(Samples.Tables, null, tables, new ToWordParams(), null, Token);
        var locator = Substitute.For<ILibreOfficeLocator>();
        var operation = new OfficeToPdfOperation(Substitute.For<IExternalToolRunner>(), locator, automation);

        var back = await operation.ConvertAsync(docx, _temp.Combine("hợp đồng lại.pdf"), new OfficeToPdfParams(Converter: OfficeConverter.OfficeSuite), Token);
        var tablesBack = await operation.ConvertAsync(tables, _temp.Combine("bảng lại.pdf"), new OfficeToPdfParams(Converter: OfficeConverter.OfficeSuite), Token);

        Squeezed(back.Output).ShouldContain("HỢPĐỒNG");
        Squeezed(tablesBack.Output).ShouldContain("NguyễnVănAn");
        Squeezed(tablesBack.Output).ShouldContain("1.250.000");
    }

    [Fact]
    public void TheWriter_KeepsWhatXmlCannotHoldOut_AndWritesTheParts()
    {
        using var stream = new MemoryStream();
        DocxWriter.Write(stream, new DocxContent(
            [
                new DocxParagraph([new DocxRun("Tiêu đề", Bold: true, Size: 16)], DocxAlignment.Center, HeadingLevel: 1),
                new DocxParagraph([new DocxRun("Chữ\u0001 thường\tcó tab", Color: "C00000"), new DocxRun(" nghiêng", Italic: true)], DocxAlignment.Justify, SpaceBefore: 6, LeftIndent: 18, FirstLineIndent: -9),
                new DocxTable([["A", "B"], ["1"]]),
                new DocxPicture(PdfPro.Core.Imaging.PngCodec.EncodeGray(PdfPro.Core.Imaging.GrayImage.White(40, 2)), false, 2000, 100),
            ],
            DocxPageSetup.A4,
            Title: "Thử"));

        using var zip = new ZipArchive(new MemoryStream(stream.ToArray()));
        zip.Entries.Select(e => e.FullName).ShouldBe(["[Content_Types].xml", "_rels/.rels", "docProps/core.xml", "word/_rels/document.xml.rels", "word/styles.xml", "word/document.xml", "word/media/image1.png"], ignoreOrder: true);
        var document = XDocument.Load(zip.GetEntry("word/document.xml")!.Open());
        var text = string.Concat(document.Descendants(W + "t").Select(t => t.Value));
        text.ShouldContain("Chữ thường");
        text.ShouldNotContain("\u0001");
        document.Descendants(W + "tab").ShouldHaveSingleItem();
        document.Descendants(W + "tc").Count().ShouldBe(4, "the short row is filled");
        document.Descendants(W + "ind").Single().Attribute(W + "hanging")!.Value.ShouldBe("180");
        var extent = document.Descendants(XName.Get("extent", "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing")).Single();
        (long.Parse(extent.Attribute("cx")!.Value, System.Globalization.CultureInfo.InvariantCulture) / 12700.0).ShouldBe(DocxPageSetup.A4.ContentWidth, 0.5, "a picture wider than the margins is made smaller");
        XDocument.Load(zip.GetEntry("docProps/core.xml")!.Open()).ToString().ShouldNotContain("creator");
    }

    private static XElement Body(string docx)
    {
        using var zip = ZipFile.OpenRead(docx);
        using var stream = zip.GetEntry("word/document.xml")!.Open();
        return XDocument.Load(stream).Root!.Element(W + "body")!;
    }

    private static IEnumerable<string> Paragraphs(XElement body)
        => body.Descendants(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value))).Where(t => t.Length > 0);

    private static List<string> Cells(XElement row)
        => [.. row.Elements(W + "tc").Select(c => string.Join('\n', c.Elements(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value)))))];

    private static string Squeezed(string pdf)
    {
        using var document = PigDocument.Open(pdf);
        return string.Concat(document.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text)).Normalize(NormalizationForm.FormC);
    }
}
