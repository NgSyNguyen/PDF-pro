using System.Text;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Imaging;
using PdfPro.Core.Ocr;
using PdfPro.Ocr;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using UglyToad.PdfPig;

namespace PdfPro.Operations.Tests.Ocr;

/// <summary>
/// Tesseract: found where its installers and tools/fetch-tools.ps1 put it, run without a shell with the languages
/// asked, its text-only PDF, words and confidences read back; orientation detection. The real program, when
/// installed with Vietnamese, recognises a page of the scan sample.
/// </summary>
public sealed class TesseractEngineTests(PdfiumFixture pdfium)
{
    private const string Tools = @"C:\PdfPro\tools";
    private const string Program = @"C:\PdfPro\tools\tesseract\tesseract.exe";
    private const string Best = @"C:\PdfPro\tools\tesseract\tessdata-best";

    private const string Tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
        + "1\t1\t0\t0\t0\t0\t0\t0\t2480\t3508\t-1\t\n"
        + "5\t1\t1\t1\t1\t1\t0\t0\t2480\t15\t95.000000\t \n"
        + "5\t1\t3\t1\t1\t1\t526\t293\t71\t50\t95.803368\tU\u0309Y\n"
        + "5\t1\t3\t1\t1\t2\t611\t307\t102\t36\t94.584106\tBAN\r\n"
        + "5\t1\t3\t1\t2\t1\t600\t400\t90\t40\t62.5\tnhân\n";

    private readonly IExternalToolRunner _runner = Substitute.For<IExternalToolRunner>();
    private readonly ITesseractLocator _locator = Substitute.For<ITesseractLocator>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static OcrImage Page => new(GrayImage.White(40, 20), 300);

    [Fact]
    public void Words_AreReadFromTheTsvReport_WithoutBlanks_AsNfc()
    {
        var words = TesseractOutput.Words(Tsv);

        words.Select(w => w.Text).ShouldBe(["ỦY", "BAN", "nhân"]);
        words[0].Text.IsNormalized(NormalizationForm.FormC).ShouldBeTrue();
        words[0].Box.ShouldBe(new OcrBox(526, 293, 71, 50));
        words[0].Confidence.ShouldBe(95.803368, 1e-6);
        (words[2].Block, words[2].Paragraph, words[2].Line).ShouldBe((3, 1, 2));
        new OcrPageResult([], words, "", null).MeanConfidence.ShouldNotBeNull().ShouldBe((95.803368 + 94.584106 + 62.5) / 3, 1e-6);
        new OcrPageResult([], [], "", null).MeanConfidence.ShouldBeNull();
    }

    [Fact]
    public void Orientation_IsReadFromWhatDetectionPrints()
    {
        const string Printed = "Page number: 0\nOrientation in degrees: 90\nRotate: 270\nOrientation confidence: 10.99\nScript: Latin\nScript confidence: 0.44\n";

        TesseractOutput.Orientation(Printed).ShouldBe(new OcrOrientation(270, 10.99, "Latin"));
        TesseractOutput.Orientation("Too few characters. Skipping this page\n").ShouldBeNull();
        TesseractOutput.Orientation("Rotate: 45\n").ShouldBeNull();
    }

    [Theory]
    [InlineData(null, OcrDataKind.Best, Program, Best, OcrDataKind.Best)]
    [InlineData(null, OcrDataKind.Fast, Program, @"C:\PdfPro\tools\tesseract\tessdata-fast", OcrDataKind.Fast)]
    [InlineData(@"D:\OCR\Tesseract-OCR", OcrDataKind.Best, @"D:\OCR\Tesseract-OCR\tesseract.exe", Best, OcrDataKind.Best)]
    [InlineData(@"D:\OCR\Tesseract-OCR\tesseract.exe", OcrDataKind.Fast, @"D:\OCR\Tesseract-OCR\tesseract.exe", @"C:\PdfPro\tools\tesseract\tessdata-fast", OcrDataKind.Fast)]
    public void OnWindows_Tesseract_AndTheDataAsked_AreFound(string? preferred, OcrDataKind data, string program, string folder, OcrDataKind? kind)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Program, @"D:\OCR\Tesseract-OCR\tesseract.exe", @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            Best + @"\pdf.ttf", @"C:\PdfPro\tools\tesseract\tessdata-fast\pdf.ttf",
        };
        var folders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Best] = ["vie", "eng", "osd"],
            [@"C:\PdfPro\tools\tesseract\tessdata-fast"] = ["vie", "osd"],
        };

        var found = Windows(files, folders).Find(preferred, data).ShouldNotBeNull();

        Slashes(found.Program).ShouldBe(program);
        Slashes(found.DataFolder).ShouldBe(folder);
        found.Data.ShouldBe(kind);
        found.Languages.ShouldNotContain("osd");
        found.HasOsd.ShouldBeTrue();
    }

    [Fact]
    public void OnWindows_AnInstalledTesseract_UsesItsOwnData_WhenNothingWasFetched()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\Tesseract-OCR\tesseract.exe", @"C:\Program Files\Tesseract-OCR\tessdata\pdf.ttf",
            @"C:\Other\tessdata-best\pdf.ttf",
        };
        var data = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Program Files\Tesseract-OCR\tessdata"] = ["eng", "osd", "vie"],
            [@"C:\Other\tessdata-best"] = ["vie"],
        };

        var found = Windows(files, data).Find(null, OcrDataKind.Best).ShouldNotBeNull();

        Slashes(found.DataFolder).ShouldBe(@"C:\Program Files\Tesseract-OCR\tessdata");
        found.Data.ShouldBeNull();
        found.Languages.ShouldBe(["eng", "vie"]);
    }

    [Fact]
    public void Tesseract_IsNotFound_WithoutPdfTtf_OrLanguages_OrWhereItWasSaidToBe()
    {
        var data = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [Best] = ["vie"] };
        Windows(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Program }, data).Find(null, OcrDataKind.Best).ShouldBeNull("no pdf.ttf: no PDF output");

        var onlyOsd = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [Best] = ["osd"] };
        Windows(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Program, Best + @"\pdf.ttf" }, onlyOsd).Find(null, OcrDataKind.Best).ShouldBeNull();

        var complete = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [Best] = ["vie"] };
        Windows(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Program, Best + @"\pdf.ttf" }, complete).Find(@"D:\Nowhere", OcrDataKind.Best).ShouldBeNull();
    }

    [Fact]
    public async Task APage_IsRecognisedInOneRun_WithTheLanguagesAsked_AndNothingIsLeftBehind()
    {
        using var temp = new TempDirectory();
        _locator.Find(null, OcrDataKind.Fast).Returns(new TesseractInstallation("tess", "/data", OcrDataKind.Fast, ["eng", "vie"], true));
        ExternalToolRequest? ran = null;
        string? picture = null;
        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            ran = call.Arg<ExternalToolRequest>();
            var arguments = ran.Arguments;
            picture = arguments[2];
            File.Exists(picture).ShouldBeTrue();
            PngCodec.Decode(File.ReadAllBytes(picture)).Width.ShouldBe(40);
            ImageResolution.ReadPng(File.ReadAllBytes(picture)).ShouldNotBeNull().X.ShouldBe(300, 0.01);
            var output = arguments[3];
            File.WriteAllBytes(output + ".pdf", "%PDF-1.5"u8.ToArray());
            File.WriteAllText(output + ".tsv", Tsv);
            File.WriteAllText(output + ".txt", "Ủy ban\n\f");
            return new ExternalToolResult("tess", 0, string.Empty, "Detected 2 diacritics", TimeSpan.Zero, false);
        });
        var engine = new TesseractEngine(_runner, _locator, () => new TesseractSettings(null, OcrDataKind.Fast));

        var result = await engine.RecognizeAsync(Page, new OcrOptions(["vie", "eng"]) { WorkingFolder = temp.Path }, Token);

        result.Text.ShouldBe("Ủy ban");
        result.Words.Count.ShouldBe(3);
        result.TextLayer.ShouldBe("%PDF-1.5"u8.ToArray());
        result.Hocr.ShouldBeNull();
        var request = ran.ShouldNotBeNull();
        request.FileName.ShouldBe("tess");
        var list = request.Arguments.ToList();
        list[list.IndexOf("--tessdata-dir") + 1].ShouldBe("/data");
        list[list.IndexOf("-l") + 1].ShouldBe("vie+eng");
        list[list.IndexOf("--dpi") + 1].ShouldBe("300");
        list.ShouldContain("textonly_pdf=1");
        list.ShouldContain("tessedit_create_tsv=1");
        list.ShouldNotContain("tessedit_create_hocr=1");
        request.EnvironmentVariables["OMP_THREAD_LIMIT"].ShouldBe("1");
        Directory.EnumerateFileSystemEntries(temp.Path).ShouldBeEmpty("the page picture and Tesseract's files are removed");
    }

    [Fact]
    public async Task WhatGoesWrong_IsSaid()
    {
        using var temp = new TempDirectory();
        var engine = new TesseractEngine(_runner, _locator, () => new TesseractSettings());
        var options = new OcrOptions(["vie"]) { WorkingFolder = temp.Path };
        _locator.Find(Arg.Any<string?>(), Arg.Any<OcrDataKind>()).Returns((TesseractInstallation?)null);
        (await Should.ThrowAsync<OcrException>(() => engine.RecognizeAsync(Page, options, Token))).Failure.ShouldBe(OcrFailure.NotInstalled);
        engine.Describe().ShouldBeNull();

        _locator.Find(Arg.Any<string?>(), Arg.Any<OcrDataKind>()).Returns(new TesseractInstallation("tess", "/data", null, ["eng"], false));
        var missing = await Should.ThrowAsync<OcrException>(() => engine.RecognizeAsync(Page, options, Token));
        (missing.Failure, missing.Detail).ShouldBe((OcrFailure.LanguageMissing, "vie"));
        (await Should.ThrowAsync<OcrException>(() => engine.DetectOrientationAsync(Page, Token))).Detail.ShouldBe("osd");
        await Should.ThrowAsync<ArgumentException>(() => engine.RecognizeAsync(Page, new OcrOptions(["-c x=1"]), Token));
        await Should.ThrowAsync<ArgumentException>(() => engine.RecognizeAsync(Page, new OcrOptions([]), Token));

        _locator.Find(Arg.Any<string?>(), Arg.Any<OcrDataKind>()).Returns(new TesseractInstallation("tess", "/data", null, ["vie"], true));
        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(new ExternalToolResult("tess", 1, string.Empty, "Error opening data file", TimeSpan.Zero, false));
        (await Should.ThrowAsync<OcrException>(() => engine.RecognizeAsync(Page, options, Token))).Failure.ShouldBe(OcrFailure.Failed);

        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ExternalToolTimeoutException("tess", TimeSpan.FromMinutes(3), string.Empty));
        (await Should.ThrowAsync<OcrException>(() => engine.RecognizeAsync(Page, options, Token))).Failure.ShouldBe(OcrFailure.TimedOut);

        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ExternalToolNotFoundException("tess"));
        (await Should.ThrowAsync<OcrException>(() => engine.RecognizeAsync(Page, options, Token))).Failure.ShouldBe(OcrFailure.NotInstalled);

        _runner.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(new ExternalToolResult("tess", 1, string.Empty, "Too few characters. Skipping this page\nError during processing.", TimeSpan.Zero, false));
        (await engine.DetectOrientationAsync(Page, Token)).ShouldBeNull("too little text to tell");
        Directory.EnumerateFileSystemEntries(temp.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheRealTesseract_ReadsAVietnameseScan_IntoAnInvisibleTextLayer()
    {
        var engine = RealEngine();
        var page = await RenderAsync(Samples.ScanVi, 0, 300);

        var result = await engine.RecognizeAsync(page, new OcrOptions(["vie"]) { Hocr = true }, Token);

        result.Text.ToLowerInvariant().ShouldContain("ủy ban nhân dân");
        result.MeanConfidence.ShouldNotBeNull().ShouldBeGreaterThan(70);
        result.Hocr.ShouldNotBeNull().ShouldContain("ocrx_word");
        using var layer = PdfDocument.Open(result.TextLayer);
        var layerPage = layer.GetPage(1);
        layerPage.Width.ShouldBe(page.WidthInPoints, 0.5);
        layerPage.Height.ShouldBe(page.HeightInPoints, 0.5);
        layerPage.Text.Normalize(NormalizationForm.FormC).ShouldContain("NHÂN");
        layerPage.GetImages().ShouldBeEmpty("only text");
    }

    [Fact]
    public async Task TheRealTesseract_TellsAnUpsideDownPage()
    {
        var engine = RealEngine();
        Assert.SkipUnless(engine.Describe() is { DetectsOrientation: true }, "Tesseract has no orientation data");
        var upsideDown = await RenderAsync(Samples.ScanVi, 11, 300);

        var orientation = await engine.DetectOrientationAsync(upsideDown, Token);

        orientation.ShouldNotBeNull().Rotate.ShouldBe(180);
        (await engine.DetectOrientationAsync(new OcrImage(GrayImage.White(2480, 3508), 300), Token)).ShouldBeNull("a blank page");
    }

    private static TesseractLocator Windows(HashSet<string> files, Dictionary<string, string[]> data)
        => new(
            Tools,
            path => files.Contains(Slashes(path)),
            folder => data.TryGetValue(Slashes(folder), out var names) ? names : [],
            variable => variable switch { "ProgramFiles" => @"C:\Program Files", "LOCALAPPDATA" => @"C:\Users\lan\AppData\Local", _ => null },
            windows: true);

    private static string Slashes(string path) => path.Replace('/', '\\');

    private static TesseractEngine RealEngine()
    {
        var locator = new TesseractLocator(Path.Combine(Samples.RepositoryRoot, "tools"));
        Assert.SkipUnless(locator.Find(null, OcrDataKind.Best) is { } found && found.Languages.Contains("vie"), "Tesseract with Vietnamese is not installed");
        var runner = new PdfPro.Infrastructure.ExternalTools.ExternalToolRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfPro.Infrastructure.ExternalTools.ExternalToolRunner>.Instance);
        return new TesseractEngine(runner, locator, () => new TesseractSettings());
    }

    private Task<OcrImage> RenderAsync(string path, int page, double dpi)
        => pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                using var bitmap = document.RenderPage(page, new PageRenderOptions { Scale = dpi / 72, Flags = PdfRenderFlags.None });
                return new OcrImage(GrayImage.FromBgra(bitmap.Width, bitmap.Height, bitmap.Pixels, bitmap.Stride), dpi);
            },
            PdfiumPriority.Normal,
            Token);
}
