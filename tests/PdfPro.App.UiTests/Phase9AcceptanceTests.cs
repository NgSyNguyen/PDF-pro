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
