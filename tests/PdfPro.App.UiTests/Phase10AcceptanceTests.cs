using System.Diagnostics;
using System.Text;
using FlaUI.Core.AutomationElements;
using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// Phase 10 acceptance scenarios (SPEC P10: OCR and the scan tools) on the real application. Run by hand like
/// <see cref="AcceptanceTests"/>; the OCR scenario needs Tesseract (tools/fetch-tools.ps1, or installed).
/// </summary>
public sealed class Phase10AcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(300);

    private readonly UiTestFolder _folder = new();

    public void Dispose()
    {
        var problems = _folder.LogProblems();
        _folder.Dispose();
        problems.ShouldBeEmpty("the application logged errors");
    }

    [Fact]
    public void AScan_BecomesSearchable_InVietnamese_WithItsImagesUntouched()
    {
        RequireEnabled();
        Assert.SkipUnless(TesseractInstalled(), "Tesseract is not installed (run tools/fetch-tools.ps1)");
        var path = _folder.Copy("scan-vi.pdf");
        var images = Jpegs(path);
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("scan-vi.pdf", Long);

        app.Ribbon("tools", "ocr.run");
        var dialog = app.WaitForWindow(Text("Ocr_Title"));
        app.IsEnabled("OcrApply", dialog).ShouldBeTrue("Vietnamese is ticked");
        var watch = Stopwatch.StartNew();
        app.Invoke("OcrApply", dialog);
        var done = app.WaitForWindow(Text("Ocr_Done_Title"), Long);
        output.WriteLine($"18 pages read in {watch.Elapsed.TotalSeconds:N1} s");
        done.FindFirstDescendant(cf => cf.ByName(Text("Common_Close")))!.AsButton().Invoke();
        app.WaitForTab("scan-vi.pdf *", Long);

        // The recognised text is found by the search panel, tones and all.
        app.Ribbon("annotate", "search.find");
        app.SetText("SearchQuery", "Ủy ban nhân dân");
        app.Invoke("SearchRun");
        AppUnderTest.WaitUntil(() => app.ItemCount("SearchResults") > 0, Long, "the search results");

        app.Ribbon("file", "file.save");
        app.WaitForTab("scan-vi.pdf", Long);
        PdfFile.PageTexts(path)[0].Normalize(NormalizationForm.FormC).ToLowerInvariant().ShouldContain("ủy ban nhân dân");
        Jpegs(path).ShouldBe(images, "the scanned images are kept byte for byte");
    }

    [Fact]
    public void TheBlankSheets_OfAScan_AreRemoved_InOneUndoableStep()
    {
        RequireEnabled();
        var path = _folder.Copy("scan-vi.pdf");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("scan-vi.pdf", Long);

        app.Ribbon("tools", "scan.removeBlankPages");
        var ask = app.WaitForWindow(Text("Scan_Blank_Title"), Long);
        ask.FindFirstDescendant(cf => cf.ByName(Text("Scan_Blank_Remove")))!.AsButton().Invoke();
        app.WaitForTab("scan-vi.pdf *", Long);
        app.Ribbon("edit", "edit.undo");
        app.WaitForTab("scan-vi.pdf", Long);
        app.Ribbon("edit", "edit.redo");
        app.WaitForTab("scan-vi.pdf *", Long);
        app.Ribbon("file", "file.save");
        app.WaitForTab("scan-vi.pdf", Long);

        PdfFile.PageCount(path).ShouldBe(14, "the separator sheets 4, 7, 10 and 14 are gone");
    }

    /// <summary>Tesseract where the app looks for it: next to it in tools, installed, or on PATH.</summary>
    private static bool TesseractInstalled()
    {
        var candidates = new List<string>
        {
            Path.Combine(Path.GetDirectoryName(Exe)!, "tools", "tesseract", "tesseract.exe"),
            Path.Combine(RepositoryRoot, "tools", "tesseract", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe"),
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, "tesseract.exe")));
        return candidates.Any(File.Exists);
    }

    /// <summary>The bytes of every JPEG image of the file, page by page.</summary>
    private static List<string> Jpegs(string path)
    {
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var found = new List<string>();
        foreach (var page in document.Pages.Cast<PdfSharp.Pdf.PdfPage>())
        {
            var xObjects = page.Resources.Elements.GetDictionary("/XObject");
            foreach (var item in xObjects?.Elements.Values ?? [])
            {
                if ((item as PdfSharp.Pdf.Advanced.PdfReference)?.Value is PdfSharp.Pdf.PdfDictionary image && image.Elements.GetName("/Filter") == "/DCTDecode")
                {
                    found.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Stream.Value)));
                }
            }
        }

        return found;
    }
}
