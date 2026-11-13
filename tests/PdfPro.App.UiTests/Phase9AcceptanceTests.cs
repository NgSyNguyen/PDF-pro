using FlaUI.Core.AutomationElements;
using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// Phase 9 acceptance scenarios (SPEC P9: optimizing and converting) on the real application. Run by hand like
/// <see cref="AcceptanceTests"/>.
/// </summary>
public sealed class Phase9AcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(120);

    private readonly UiTestFolder _folder = new();

    public void Dispose()
    {
        var problems = _folder.LogProblems();
        _folder.Dispose();
        problems.ShouldBeEmpty("the application logged errors");
    }

    [Fact]
    public void Compressing_ShowsOnePageBeforeAndAfter_ThenWritesACopyNoLarger_WithTheSamePages()
    {
        RequireEnabled();
        var path = _folder.Copy("scan-vi.pdf");
        var original = Sha256(path);
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("scan-vi.pdf", Long);

        app.Ribbon("convert", "optimize.compress");
        var dialog = app.WaitForWindow(Text("Compress_Title"));
        app.Select("CompressPreset_Screen", dialog);
        app.Invoke("CompressPreview", dialog);
        var summary = AppUnderTest.WaitFor(
            () => app.TryFind("CompressPreviewSummary", dialog) is { } text && !string.IsNullOrEmpty(text.Name) ? text.Name : null,
            Long,
            "the preview of the first page");
        output.WriteLine(summary);
        app.Toggle("CompressActualSize", dialog);

        app.Invoke("CompressApply", dialog);
        var compressed = Path.Combine(_folder.Documents, Format("Compress_Name", "scan-vi") + ".pdf");
        app.UseFileDialog(Text("Save_Dialog_Title"), compressed);
        var done = app.WaitForWindow(Text("Compress_Done_Title"), Long);
        done.FindFirstDescendant(cf => cf.ByName(Text("Common_Close")))!.AsButton().Invoke();

        new FileInfo(compressed).Length.ShouldBeLessThanOrEqualTo(new FileInfo(path).Length);
        PdfFile.PageCount(compressed).ShouldBe(PdfFile.PageCount(path));
        Sha256(path).ShouldBe(original, "the document's own file is not touched");
        output.WriteLine($"{new FileInfo(path).Length:N0} → {new FileInfo(compressed).Length:N0} bytes");
    }

    [Fact]
    public void AGreyCopy_KeepsTheVietnameseText_OfEveryPage()
    {
        RequireEnabled();
        var path = _folder.Copy("vanban-vi.pdf");
        var original = Sha256(path);
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("vanban-vi.pdf", Long);

        app.Ribbon("convert", "optimize.grayscale");
        var dialog = app.WaitForWindow(Text("Grayscale_Title"));
        app.IsEnabled("GrayscaleApply", dialog).ShouldBeTrue();
        app.Invoke("GrayscaleApply", dialog);
        var grey = Path.Combine(_folder.Documents, Format("Grayscale_Name", "vanban-vi") + ".pdf");
        app.UseFileDialog(Text("Save_Dialog_Title"), grey);
        var done = app.WaitForWindow(Text("Grayscale_Done_Title"), Long);
        done.FindFirstDescendant(cf => cf.ByName(Text("Common_Close")))!.AsButton().Invoke();

        PdfFile.PageTexts(grey).ShouldBe(PdfFile.PageTexts(path), "converting the colours keeps the text");
        Sha256(path).ShouldBe(original, "the document's own file is not touched");
    }

    [Fact]
    public void ThePages_BecomeOneMultiPageTiff_InTheChosenFolder()
    {
        RequireEnabled();
        var path = _folder.Copy("basic-vi.pdf");
        var folder = Path.Combine(_folder.Documents, "ảnh trang");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("basic-vi.pdf", Long);

        app.Ribbon("convert", "convert.toImages");
        var dialog = app.WaitForWindow(Text("ToImages_Title"));
        app.Select("ToImagesTiff", dialog);
        app.SetText("ToImagesDpi", "100", dialog);
        app.SetText("ToImagesFolder", folder, dialog);
        app.SetChecked("ToImagesOpenFolder", false, dialog);
        app.Invoke("ToImagesApply", dialog);
        var tiff = Path.Combine(folder, "basic-vi.tif");
        var elapsed = AppUnderTest.WaitUntil(() => File.Exists(tiff), Long, "the TIFF file");
        app.WaitForStatus(Format("ToImages_Done", folder), Long);
        output.WriteLine($"6 pages at 100 DPI as one TIFF: {elapsed.TotalMilliseconds:N0} ms, {new FileInfo(tiff).Length:N0} bytes");

        TiffPages(File.ReadAllBytes(tiff)).ShouldBe(PdfFile.PageCount(path));
        Directory.GetFiles(folder).ShouldBe([tiff], "no temporary file is left");
    }

    [Fact]
    public void AFolderOfScans_BecomesOnePdf_InNaturalOrder_WithTheJpegsUntouched()
    {
        RequireEnabled();
        var folder = Path.Combine(_folder.Documents, "ảnh scan");
        Directory.CreateDirectory(folder);
        var jpegs = Jpegs(Sample("scan-vi.pdf"), 3);
        string[] names = ["trang 10.jpg", "trang 2.jpg", "trang 1.jpg"];
        for (var i = 0; i < names.Length; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, names[i]), jpegs[i]);
        }

        var pdf = Path.Combine(_folder.Documents, "hồ sơ scan.pdf");
        using var app = AppUnderTest.Start(_folder);

        app.Ribbon("convert", "convert.fromImages");
        var dialog = app.WaitForWindow(Text("FromImages_Title"));
        app.Invoke("FromImagesAddFolder", dialog);
        app.UseFileDialog(Text("FromImages_Folder_Pick_Title"), folder);
        AppUnderTest.WaitUntil(() => app.ItemCount("FromImagesFiles", dialog) == 3, Long, "the three pictures in the list");
        app.SetText("FromImagesOutput", pdf, dialog);
        app.SetChecked("FromImagesOpenWhenDone", false, dialog);
        app.Invoke("FromImagesRun", dialog);
        var elapsed = AppUnderTest.WaitUntil(() => File.Exists(pdf), Long, "the PDF");
        app.WaitForStatus(Format("FromImages_Done", Path.GetFileName(pdf)), Long);
        output.WriteLine($"3 scanned JPEG pages into one PDF: {elapsed.TotalMilliseconds:N0} ms");

        // trang 1, trang 2, trang 10: the files written as jpegs[2], jpegs[1], jpegs[0].
        Jpegs(pdf, 3).ShouldBe([jpegs[2], jpegs[1], jpegs[0]], "natural order, and each JPEG byte for byte");
    }

    [Fact]
    public void TheVietnameseText_IsWrittenAsMarkdown_InReadingOrder()
    {
        RequireEnabled();
        var path = _folder.Copy("vanban-vi.pdf");
        var folder = Path.Combine(_folder.Documents, "văn bản");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("vanban-vi.pdf", Long);

        app.Ribbon("convert", "convert.toText");
        var dialog = app.WaitForWindow(Text("ToText_Title"));
        app.Select("ToTextMarkdown", dialog);
        app.SetText("ToTextFolder", folder, dialog);
        app.SetChecked("ToTextOpenFolder", false, dialog);
        app.Invoke("ToTextApply", dialog);
        var markdown = Path.Combine(folder, "vanban-vi.md");
        var elapsed = AppUnderTest.WaitUntil(() => File.Exists(markdown), Long, "the Markdown file");
        app.WaitForStatus(Format("ToText_Done", folder), Long);
        output.WriteLine($"14 pages as Markdown: {elapsed.TotalMilliseconds:N0} ms");

        var text = File.ReadAllText(markdown);
        text.ShouldContain("VIỆT NAM");
        text.ShouldBe(text.Normalize(System.Text.NormalizationForm.FormC), "NFC");
        text.IndexOf("CỘNG HÒA", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("Độc lập", StringComparison.Ordinal), "the national heading reads in order");
    }

    [Fact]
    public void AScanOfFiftyPages_IsCompressedForEBooks_ToLessThan15MB_AtAReadableResolution()
    {
        RequireEnabled();
        var path = Path.Combine(_folder.Documents, "scan 50 trang.pdf");
        ScanPdf.Write(path, 50);
        var size = new FileInfo(path).Length;
        output.WriteLine($"made-up scan: {size:N0} bytes");
        size.ShouldBeGreaterThan(40L * 1024 * 1024, "a heavy scan, as PROMPTS P9 has it (~80 MB)");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("scan 50 trang.pdf", Long);

        app.Ribbon("convert", "optimize.compress");
        var dialog = app.WaitForWindow(Text("Compress_Title"), Long);
        app.Select("CompressPreset_Ebook", dialog);
        app.Invoke("CompressApply", dialog);
        var compressed = Path.Combine(_folder.Documents, Format("Compress_Name", "scan 50 trang") + ".pdf");
        app.UseFileDialog(Text("Save_Dialog_Title"), compressed);
        var started = DateTime.UtcNow;
        var done = app.WaitForWindow(Text("Compress_Done_Title"), TimeSpan.FromMinutes(10));
        output.WriteLine($"compressed in {(DateTime.UtcNow - started).TotalSeconds:N1} s");
        done.FindFirstDescendant(cf => cf.ByName(Text("Common_Close")))!.AsButton().Invoke();

        var after = new FileInfo(compressed).Length;
        output.WriteLine($"{size:N0} → {after:N0} bytes");
        after.ShouldBeLessThan(15L * 1024 * 1024, "eBook: under 15 MB");
        PdfFile.PageCount(compressed).ShouldBe(50);
        ScanPdf.ImageResolutions(compressed).ShouldAllBe(dpi => dpi >= 140, "eBook keeps 150 DPI, at which the text stays readable");
    }

    [Fact]
    public void AWordDocument_BecomesAPdf_WithItsVietnameseText()
    {
        RequireEnabled();
        var soffice = new[] { "ProgramFiles", "ProgramW6432" }
            .Select(v => Environment.GetEnvironmentVariable(v))
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => Path.Combine(f!, "LibreOffice", "program", "soffice.exe"))
            .FirstOrDefault(File.Exists);
        Assert.SkipUnless(soffice is not null, "LibreOffice is not installed");
        var docx = Path.Combine(_folder.Documents, "tờ trình.docx");
        ScanPdf.WriteDocx(docx, "Cộng hòa xã hội chủ nghĩa Việt Nam", "Kính gửi Ủy ban nhân dân xã");
        var folder = Path.Combine(_folder.Documents, "bản PDF");
        using var app = AppUnderTest.Start(_folder);

        app.Ribbon("convert", "convert.officeToPdf");
        var dialog = app.WaitForWindow(Text("OfficeToPdf_Title"));
        app.Invoke("OfficeToPdfAddFiles", dialog);
        app.UseFileDialog(Text("OfficeToPdf_Pick_Title"), docx);
        AppUnderTest.WaitUntil(() => app.ItemCount("OfficeToPdfFiles", dialog) == 1, Long, "the document in the list");
        app.SetText("OfficeToPdfFolder", folder, dialog);
        app.SetChecked("OfficeToPdfOpenWhenDone", false, dialog);
        app.Invoke("OfficeToPdfRun", dialog);
        var pdf = Path.Combine(folder, "tờ trình.pdf");
        var elapsed = AppUnderTest.WaitUntil(() => File.Exists(pdf), Long, "the PDF");
        app.WaitForStatus(Format("OfficeToPdf_Done", folder), Long);
        output.WriteLine($"one Word document through LibreOffice: {elapsed.TotalSeconds:N1} s");

        // PdfFile.PageTexts joins the letters without the spaces between words.
        var text = string.Join('\n', PdfFile.PageTexts(pdf)).Normalize(System.Text.NormalizationForm.FormC);
        text.ShouldContain("CộnghòaxãhộichủnghĩaViệtNam");
        text.ShouldContain("KínhgửiỦybannhândânxã");
        Directory.GetFileSystemEntries(folder).ShouldBe([pdf], "LibreOffice's working folder is gone");
    }

    [Fact]
    public void ADocument_BecomesAPdfA2bCopy_WithItsTextAndAnOutputIntent()
    {
        RequireEnabled();
        var path = _folder.Copy("vanban-vi.pdf");
        var original = Sha256(path);
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("vanban-vi.pdf", Long);

        app.Ribbon("convert", "convert.toPdfA");
        var dialog = app.WaitForWindow(Text("PdfA_Title"));
        app.Select("PdfAA2B", dialog);

        // With veraPDF and Java installed, the copy is checked by veraPDF too, and passes.
        var validated = app.IsEnabled("PdfAValidate", dialog);
        if (validated)
        {
            app.SetChecked("PdfAValidate", true, dialog);
        }

        app.Invoke("PdfAApply", dialog);
        var copy = Path.Combine(_folder.Documents, Format("PdfA_Name", "vanban-vi") + ".pdf");
        app.UseFileDialog(Text("Save_Dialog_Title"), copy);
        var done = app.WaitForWindow(Text("PdfA_Done_Title"), Long);
        var report = string.Join('\n', done.FindAllDescendants().Select(e => e.Name));
        output.WriteLine(report);
        if (validated)
        {
            report.ShouldContain(Text("PdfA_VeraPdf_Pass")[..^"{0}.".Length], customMessage: "veraPDF finds the copy compliant");
        }

        done.FindFirstDescendant(cf => cf.ByName(Text("Common_Close")))!.AsButton().Invoke();

        using (var pdf = PdfSharp.Pdf.IO.PdfReader.Open(copy, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        {
            var metadata = (pdf.Internals.Catalog.Elements["/Metadata"] as PdfSharp.Pdf.Advanced.PdfReference)!.Value as PdfSharp.Pdf.PdfDictionary;
            System.Text.Encoding.UTF8.GetString(metadata!.Stream.Value).ShouldContain("<pdfaid:part>2</pdfaid:part>");
            pdf.Internals.Catalog.Elements.ContainsKey("/OutputIntents").ShouldBeTrue();
        }

        PdfFile.PageTexts(copy).ShouldBe(PdfFile.PageTexts(path), "the text is kept");
        Sha256(path).ShouldBe(original, "the document's own file is not touched");
    }

    /// <summary>The first <paramref name="count"/> JPEG images of a PDF's pages, as stored.</summary>
    private static List<byte[]> Jpegs(string path, int count)
    {
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var found = new List<byte[]>();
        foreach (var page in document.Pages.Cast<PdfSharp.Pdf.PdfPage>())
        {
            var xObjects = page.Resources.Elements.GetDictionary("/XObject");
            foreach (var item in xObjects?.Elements.Values ?? [])
            {
                if ((item as PdfSharp.Pdf.Advanced.PdfReference)?.Value is PdfSharp.Pdf.PdfDictionary image
                    && image.Elements.GetName("/Filter") == "/DCTDecode" && found.Count < count)
                {
                    found.Add(image.Stream.Value);
                }
            }
        }

        return found;
    }

    /// <summary>The number of directories (pages) of a little-endian TIFF.</summary>
    private static int TiffPages(byte[] file)
    {
        var pages = 0;
        var offset = BitConverter.ToUInt32(file, 4);
        while (offset != 0 && pages < 10_000)
        {
            pages++;
            offset = BitConverter.ToUInt32(file, (int)offset + 2 + (12 * BitConverter.ToUInt16(file, (int)offset)));
        }

        return pages;
    }
}
