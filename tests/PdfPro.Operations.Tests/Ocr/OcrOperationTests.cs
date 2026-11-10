using System.Globalization;
using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Ocr;
using PdfPro.Operations.Ocr;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Tests.Ocr;

/// <summary>
/// ocr.run: pages without text get an invisible text layer over the place each word was found — on pages turned by
/// /Rotate, cropped, read upside down or straightened — while their content, images byte for byte, stays; pages with
/// text are skipped or their hidden text replaced; blank pages are not read; pages are read in parallel; a failing
/// page is reported, a cancelled run writes nothing. A scripted engine plays OCR (these tests run everywhere); the real
/// Tesseract, when installed, reads samples/scan-vi.pdf.
/// </summary>
public sealed class OcrOperationTests(PdfiumFixture pdfium)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheTextLayer_LiesWhereTheEngineFoundTheWord_OnPagesTurnedAndCropped()
    {
        var engine = new ScriptedEngine { At = (0.1, 0.2) };
        using var temp = new TempDirectory();
        var input = Inked(Samples.RotatedCropBox, temp);

        var output = await RunAsync(input, temp, new OcrParams(["vie"], ExistingTextMode.All, AutoRotate: false, Deskew: false), engine);

        engine.Calls.ShouldBe(4);
        for (var page = 0; page < 4; page++)
        {
            var (centre, size) = await WordAsync(output, page, "WORD");
            centre.X.ShouldBe(0.1 * size.Width, 0.02 * size.Width, $"page {page + 1}");
            centre.Y.ShouldBe(0.2 * size.Height, 0.02 * size.Height, $"page {page + 1}");
        }
    }

    [Theory]
    [InlineData(180, 0.9, 0.8)]
    [InlineData(90, 0.2, 0.9)]
    [InlineData(270, 0.8, 0.1)]
    public async Task APageReadTurned_HasItsWordsMappedBack(int turn, double x, double y)
    {
        var engine = new ScriptedEngine { At = (0.1, 0.2), Orientation = new OcrOrientation(turn, 20, "Latin") };
        using var temp = new TempDirectory();

        var output = await RunAsync(Samples.ScanVi, temp, new OcrParams(["vie"], Pages: [0], Deskew: false), engine);

        var (centre, size) = await WordAsync(output, 0, "WORD");
        centre.X.ShouldBe(x * size.Width, 0.02 * size.Width);
        centre.Y.ShouldBe(y * size.Height, 0.02 * size.Height);
        OcrOperation.ReportOf(engine.LastParams!).ShouldNotBeNull().Pages[0].Rotation.ShouldBe(turn);
    }

    [Fact]
    public async Task AnUnsureOrientation_IsCheckedByReadingBothWays()
    {
        // Upright reads better: the page stays upright although detection said 180.
        var engine = new ScriptedEngine { Orientation = new OcrOrientation(180, 0.7, "Latin"), Confidence = turned => turned ? 30 : 90 };
        using var temp = new TempDirectory();

        await RunAsync(Samples.ScanVi, temp, new OcrParams(["vie"], Pages: [0], Deskew: false), engine);

        engine.Calls.ShouldBe(2);
        var report = OcrOperation.ReportOf(engine.LastParams!).ShouldNotBeNull();
        (report.Pages[0].Rotation, report.Pages[0].MeanConfidence).ShouldBe((0, 90.0));
    }

    [Fact]
    public async Task PagesWithText_AreSkipped_OrTheirHiddenTextReplaced_NeverDoubled()
    {
        using var temp = new TempDirectory();
        var first = await RunAsync(Samples.ScanVi, temp, new OcrParams(["vie"], Pages: [0, 1], Deskew: false, AutoRotate: false), new ScriptedEngine { Word = "FIRST" });

        var skipping = new ScriptedEngine { Word = "SECOND" };
        var skipped = await RunAsync(first, temp, new OcrParams(["vie"], Pages: [0, 1, 2]), skipping, "skip.pdf");
        skipping.Calls.ShouldBe(1, "only the third page had no text");
        OcrOperation.ReportOf(skipping.LastParams!)!.Pages.Select(p => p.Outcome).ShouldBe([OcrPageOutcome.HadText, OcrPageOutcome.HadText, OcrPageOutcome.Recognized]);

        var replacing = new ScriptedEngine { Word = "THIRD" };
        var replaced = await RunAsync(first, temp, new OcrParams(["vie"], ExistingTextMode.Replace, Pages: [0, 1], AutoRotate: false), replacing, "replace.pdf");
        var texts = await TextsAsync(replaced, 0, 1);
        texts.ShouldAllBe(t => t.Contains("THIRD") && !t.Contains("FIRST"));
        OcrOperation.ReportOf(replacing.LastParams!)!.RemovedHiddenText.ShouldBe([0, 1]);
        LayerStreams(replaced, 0).ShouldBe(1);
    }

    [Fact]
    public async Task PaintedText_IsKept_WhenEveryPageIsRead()
    {
        using var temp = new TempDirectory();
        var engine = new ScriptedEngine();

        var output = await RunAsync(Samples.BasicVi, temp, new OcrParams(["vie"], ExistingTextMode.All, Pages: [0], AutoRotate: false, Deskew: false), engine);

        var (before, after) = ((await TextsAsync(Samples.BasicVi, 0))[0], (await TextsAsync(output, 0))[0]);
        after.ShouldContain("WORD");
        after.Normalize(NormalizationForm.FormC).ShouldContain(before.Normalize(NormalizationForm.FormC)[..40]);
        (await new OcrOperation(pdfium.Gate, new ScriptedEngine()).ApplyAsync(Samples.BasicVi, new OcrParams(["vie"]))).Pages.ShouldAllBe(p => p.Outcome == OcrPageOutcome.HadText);
    }

    [Fact]
    public async Task HiddenTextOfOtherPrograms_IsRemoved_PaintedTextAndTheRestKept()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("hidden.pdf");
        HiddenTextPdf(path);
        using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            InvisibleText.Remove(document.Pages[0]).ShouldBeTrue();
            InvisibleText.Remove(document.Pages[0]).ShouldBeFalse("nothing hidden is left");
            document.Save(temp.Combine("clean.pdf"));
        }

        var texts = await TextsAsync(temp.Combine("clean.pdf"), 0, 1);
        texts[0].ShouldContain("Painted");
        texts[0].ShouldContain("AfterQuote");
        texts[0].ShouldNotContain("Hidden");
        texts[0].ShouldNotContain("InForm");
        texts[0].ShouldNotContain("Quoted");
        texts[1].ShouldContain("InForm", Case.Sensitive, "the form is shared with the second page, which keeps it");
    }

    [Fact]
    public void Rewriting_DropsHiddenShows_AndKeepsMovesToTheNextLine()
    {
        var content = "q 3 Tr BT (a) Tj [(b)] TJ (c) ' 1 2 (d) \" ET Q BT (e) Tj ET /Layer Do /Other Do"u8.ToArray();

        var rewritten = InvisibleText.Rewrite(content, 0, name => name == "Layer", out var drawn).ShouldNotBeNull();

        Encoding.ASCII.GetString(rewritten).ShouldBe("q 3 Tr BT   T* 1 Tw 2 Tc T* ET Q BT (e) Tj ET  /Other Do");
        drawn.ShouldBe([("Other", 0)]);
        InvisibleText.Rewrite("BT (e) Tj ET"u8.ToArray(), 0, _ => false, out _).ShouldBeNull();
        Encoding.ASCII.GetString(InvisibleText.Rewrite("BT (e) Tj ET"u8.ToArray(), 3, _ => false, out _)!).ShouldBe("BT  ET", "a form drawn in render mode 3");
    }

    [Fact]
    public async Task TheImages_AreKeptByteForByte_AndBlankPagesAreNotRead()
    {
        using var temp = new TempDirectory();
        var engine = new ScriptedEngine();

        var output = await RunAsync(Samples.ScanVi, temp, new OcrParams(["vie"], Pages: [0, 3, 6], AutoRotate: false), engine);

        engine.Calls.ShouldBe(1, "pages 4 and 7 are separator sheets");
        OcrOperation.ReportOf(engine.LastParams!)!.Pages.Select(p => p.Outcome).ShouldBe([OcrPageOutcome.Recognized, OcrPageOutcome.Blank, OcrPageOutcome.Blank]);
        ImageStreams(output).ShouldBe(ImageStreams(Samples.ScanVi));
    }

    [Fact]
    public async Task Pages_AreReadInParallel_WithProgress()
    {
        using var temp = new TempDirectory();
        var engine = new ScriptedEngine { Delay = TimeSpan.FromMilliseconds(150) };
        var progress = new RecordingProgress();

        await RunAsync(Samples.ScanVi, temp, new OcrParams(["vie"], Pages: [0, 1, 2, 4, 5, 7], Parallelism: 3, AutoRotate: false, Deskew: false), engine, progress: progress);

        engine.Calls.ShouldBe(6);
        engine.MostAtOnce.ShouldBeGreaterThan(1);
        engine.MostAtOnce.ShouldBeLessThanOrEqualTo(3);
        progress.Reports[^1].ShouldBe(new OperationProgress(7, 7, "Stage_Ocr"));
        progress.Reports.Select(r => r.Completed).ShouldBeInOrder();
    }

    [Fact]
    public async Task AFailingPage_IsReported_ButAllFailing_Fails()
    {
        using var temp = new TempDirectory();
        var engine = new ScriptedEngine { FailOn = call => call == 1 };

        await RunAsync(Samples.ScanVi, temp, new OcrParams(["vie"], Pages: [0, 1], Parallelism: 1, AutoRotate: false, Deskew: false), engine);

        OcrOperation.ReportOf(engine.LastParams!)!.Pages.Select(p => p.Outcome).ShouldBe([OcrPageOutcome.Failed, OcrPageOutcome.Recognized]);
        var failing = new ScriptedEngine { FailOn = _ => true };
        (await Should.ThrowAsync<OcrException>(() => RunAsync(Samples.ScanVi, temp, new OcrParams(["vie"], Pages: [0, 1], AutoRotate: false), failing, "all.pdf"))).Failure.ShouldBe(OcrFailure.Failed);
    }

    [Fact]
    public async Task WithoutTheEngineOrALanguage_OrWithAWrongPage_NothingIsRead()
    {
        var engine = new ScriptedEngine();
        var operation = new OcrOperation(pdfium.Gate, engine);
        (await Should.ThrowAsync<OcrException>(() => operation.ApplyAsync(Samples.ScanVi, new OcrParams(["fra"])))).Detail.ShouldBe("fra");
        await Should.ThrowAsync<OperationValidationException>(() => operation.ApplyAsync(Samples.ScanVi, new OcrParams(["vie"], Pages: [18])));
        var absent = new OcrOperation(pdfium.Gate, new ScriptedEngine { Installed = false });
        (await Should.ThrowAsync<OcrException>(() => absent.ApplyAsync(Samples.ScanVi, new OcrParams(["vie"])))).Failure.ShouldBe(OcrFailure.NotInstalled);
        engine.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Cancelled_WritesNothing()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var engine = new ScriptedEngine { OnRecognize = cancel.Cancel };
        using var output = new MemoryStream();

        await Should.ThrowAsync<OperationCanceledException>(() => new OcrOperation(pdfium.Gate, engine)
            .ApplyToFileAsync(Samples.ScanVi, output, new OcrParams(["vie"], Pages: [0, 1, 2]), new RecordingProgress(), cancel.Token));

        output.Length.ShouldBe(0);
    }

    [Fact]
    public async Task TheRealTesseract_MakesTheScanSearchable_WithWordsOverTheirInk()
    {
        var engine = RealEngine();
        using var temp = new TempDirectory();
        var parameters = new OcrParams(["vie"], Pages: [0, 3, 11]);

        var output = await RunAsync(Samples.ScanVi, temp, parameters, engine);

        var report = OcrOperation.ReportOf(parameters).ShouldNotBeNull();
        report.Pages.Select(p => p.Outcome).ShouldBe([OcrPageOutcome.Recognized, OcrPageOutcome.Blank, OcrPageOutcome.Recognized]);
        report.Pages[0].MeanConfidence.ShouldNotBeNull().ShouldBeGreaterThan(75);
        report.Pages[2].Rotation.ShouldBe(180, "page 12 is upside down");
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(output))
        {
            pig.GetPage(1).Text.Normalize(NormalizationForm.FormC).ToLowerInvariant().ShouldContain("ủy ban nhân dân");
        }

        (await TextsAsync(output, 0))[0].Normalize(NormalizationForm.FormC).ToLowerInvariant().ShouldContain("ủy ban nhân dân");
        (await InkUnderCharactersAsync(output, 0)).ShouldBeGreaterThan(0.9);
        (await InkUnderCharactersAsync(output, 11)).ShouldBeGreaterThan(0.9);
        ImageStreams(output).ShouldBe(ImageStreams(Samples.ScanVi));
        report.Text.ShouldContain("\f");
    }

    [Fact]
    public async Task TheRealTesseract_StraightensATiltedPage_WhenAsked()
    {
        var engine = RealEngine();
        using var temp = new TempDirectory();
        var parameters = new OcrParams(["vie"], Pages: [7], StraightenPages: true);

        var output = await RunAsync(Samples.ScanVi, temp, parameters, engine);

        OcrOperation.ReportOf(parameters)!.StraightenedPages.ShouldBe([7]);
        var straightened = await RenderAsync(output, 7, 150);
        SkewDetector.Estimate(straightened).ShouldNotBeNull().Degrees.ShouldBe(0, 0.3, "the page leaned by -1.15°");
        (await InkUnderCharactersAsync(output, 7)).ShouldBeGreaterThan(0.9);
    }

    private async Task<string> RunAsync(string input, TempDirectory temp, OcrParams parameters, IOcrEngine engine, string name = "ocr.pdf", RecordingProgress? progress = null)
    {
        if (engine is ScriptedEngine scripted)
        {
            scripted.LastParams = parameters;
        }

        var path = temp.Combine(name);
        await using (var output = File.Create(path))
        {
            await new OcrOperation(pdfium.Gate, engine).ApplyToFileAsync(input, output, parameters, progress ?? new RecordingProgress(), Token);
        }

        return path;
    }

    private async Task<string[]> TextsAsync(string path, params int[] pages)
        => await pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                return pages.Select(document.GetPageText).ToArray();
            },
            PdfiumPriority.Normal,
            Token);

    /// <summary>Where the characters of <paramref name="word"/> are on the page as displayed (points, y down), and its displayed size.</summary>
    private Task<(PdfPoint Centre, PdfSize Size)> WordAsync(string path, int page, string word)
        => pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                var geometry = document.GetPageGeometry(page);
                var layer = document.GetPageTextLayer(page);
                var text = string.Concat(layer.Chars.Select(c => c.Text));
                var at = text.IndexOf(word, StringComparison.Ordinal);
                at.ShouldBeGreaterThanOrEqualTo(0, $"'{word}' is on page {page + 1}");
                var mapper = new PageCoordinateMapper(geometry, 1);
                var points = layer.Chars.Skip(at).Take(word.Length)
                    .Select(c => mapper.PdfToScreen(new PdfPoint((c.Box.Left + c.Box.Right) / 2, (c.Box.Bottom + c.Box.Top) / 2))).ToList();
                return (new PdfPoint(points.Average(p => p.X), points.Average(p => p.Y)), geometry.DisplaySize);
            },
            PdfiumPriority.Normal,
            Token);

    /// <summary>The share of the page's characters whose box covers dark pixels of the page as rendered.</summary>
    private async Task<double> InkUnderCharactersAsync(string path, int page)
    {
        const double Dpi = 150;
        var image = await RenderAsync(path, page, Dpi);
        var boxes = await pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                var mapper = new PageCoordinateMapper(document.GetPageGeometry(page), Dpi / 72);
                return document.GetPageTextLayer(page).Chars
                    .Where(c => !c.IsGenerated && !c.Box.IsEmpty && !string.IsNullOrWhiteSpace(c.Text))
                    .Select(c => mapper.PdfRectToScreen(c.Box)).ToList();
            },
            PdfiumPriority.Normal,
            Token);
        boxes.Count.ShouldBeGreaterThan(50);
        var inked = boxes.Count(box =>
        {
            for (var y = Math.Max(0, (int)box.Y); y < Math.Min(image.Height, (int)Math.Ceiling(box.Y + box.Height)); y++)
            {
                for (var x = Math.Max(0, (int)box.X); x < Math.Min(image.Width, (int)Math.Ceiling(box.X + box.Width)); x++)
                {
                    if (image[x, y] < 128)
                    {
                        return true;
                    }
                }
            }

            return false;
        });
        return (double)inked / boxes.Count;
    }

    private Task<GrayImage> RenderAsync(string path, int page, double dpi)
        => pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                using var bitmap = document.RenderPage(page, new PageRenderOptions { Scale = dpi / 72, Flags = PdfRenderFlags.Grayscale });
                return GrayImage.FromBgra(bitmap.Width, bitmap.Height, bitmap.Pixels, bitmap.Stride);
            },
            PdfiumPriority.Normal,
            Token);

    /// <summary>The bytes of every image stream of the file, page by page.</summary>
    private static List<string> ImageStreams(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var images = new List<string>();
        foreach (var page in document.Pages)
        {
            var xObjects = AsDictionary(AsDictionary(page.Elements["/Resources"])?.Elements["/XObject"]);
            foreach (var item in xObjects?.Elements.Values ?? [])
            {
                if (AsDictionary(item) is { } image && Name(image, "/Subtype") == "/Image")
                {
                    images.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Stream.Value)));
                }
            }
        }

        return images;
    }

    /// <summary>The document with a dark block in the middle of every page's visible box, so no page is blank.</summary>
    private static string Inked(string path, TempDirectory temp)
    {
        var inked = temp.Combine("inked.pdf");
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        foreach (var page in document.Pages)
        {
            var box = PdfPro.Operations.Materializing.PageBoxes.VisibleBox(page);
            var content = new PdfContent(document);
            content.CreateStream(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"q 0 g {box.Left + (box.Width * 0.4):0.##} {box.Bottom + (box.Height * 0.4):0.##} {box.Width * 0.2:0.##} {box.Height * 0.2:0.##} re f Q")));
            document.Internals.AddObject(content);
            var contents = new PdfArray(document);
            PdfPro.Operations.Stamping.PageStamper.ContentStreams(page).ForEach(contents.Elements.Add);
            contents.Elements.Add(content.Reference!);
            page.Elements["/Contents"] = contents;
        }

        document.Save(inked);
        return inked;
    }

    private static int LayerStreams(string path, int page)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return AsArray(document.Pages[page].Elements["/Contents"])?.Elements
            .Count(s => AsDictionary(s)?.Elements.ContainsKey(InvisibleText.MarkerKey) == true) ?? 0;
    }

    /// <summary>
    /// A page with painted text, hidden text (render mode 3, also shown by ' and "), and a form drawing hidden text that
    /// a second page shares.
    /// </summary>
    private static void HiddenTextPdf(string path)
    {
        using var document = new PdfDocument();
        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        document.Internals.AddObject(font);
        var fonts = new PdfDictionary(document);
        fonts.Elements["/F1"] = font.Reference;

        var form = new PdfDictionary(document);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = new PdfLiteral("[0 0 600 800]");
        var formResources = new PdfDictionary(document);
        formResources.Elements["/Font"] = fonts;
        form.Elements["/Resources"] = formResources;
        form.CreateStream("BT 3 Tr /F1 12 Tf 100 400 Td (InForm) Tj ET"u8.ToArray());
        document.Internals.AddObject(form);

        foreach (var content in new[]
        {
            "BT /F1 12 Tf 100 700 Td (Painted) Tj ET q BT 3 Tr /F1 12 Tf 100 600 Td (Hidden) Tj 14 TL (Quoted) ' 0 Tr (AfterQuote) Tj ET Q /X1 Do",
            "/X1 Do",
        })
        {
            var page = document.AddPage();
            var resources = new PdfDictionary(document);
            resources.Elements["/Font"] = fonts;
            var xObjects = new PdfDictionary(document);
            xObjects.Elements["/X1"] = form.Reference;
            resources.Elements["/XObject"] = xObjects;
            page.Elements["/Resources"] = resources;
            var stream = new PdfContent(document);
            stream.CreateStream(Encoding.ASCII.GetBytes(content));
            document.Internals.AddObject(stream);
            page.Elements["/Contents"] = stream.Reference;
        }

        document.Save(path);
    }

    private static TesseractEngine RealEngine()
    {
        var locator = new TesseractLocator(Path.Combine(Samples.RepositoryRoot, "tools"));
        Assert.SkipUnless(locator.Find(null, OcrDataKind.Best) is { HasOsd: true } found && found.Languages.Contains("vie"), "Tesseract with Vietnamese and orientation data is not installed");
        var runner = new PdfPro.Infrastructure.ExternalTools.ExternalToolRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfPro.Infrastructure.ExternalTools.ExternalToolRunner>.Instance);
        return new TesseractEngine(runner, locator, () => new TesseractSettings());
    }

    /// <summary>
    /// An OCR engine with a script: every page gives one word, <see cref="Word"/>, at <see cref="At"/> (share of the
    /// picture from its top-left corner), as invisible Helvetica text in a PDF of the picture's size.
    /// </summary>
    private sealed class ScriptedEngine : IOcrEngine
    {
        /// <summary>The pictures of rendered pages (as orientation detection saw them): a picture not among them was turned.</summary>
        private readonly HashSet<byte[]> _renders = new(ReferenceEqualityComparer.Instance);
        private int _running;
        private int _calls;

        public bool Installed { get; init; } = true;

        public string Word { get; init; } = "WORD";

        public (double X, double Y) At { get; init; } = (0.3, 0.3);

        public OcrOrientation? Orientation { get; init; }

        /// <summary>The confidence of a reading, by whether the picture was turned.</summary>
        public Func<bool, double> Confidence { get; init; } = _ => 90;

        public TimeSpan Delay { get; init; }

        public Func<int, bool> FailOn { get; init; } = _ => false;

        public Action? OnRecognize { get; init; }

        public OcrParams? LastParams { get; set; }

        public int Calls => _calls;

        public int MostAtOnce { get; private set; }

        public OcrEngineInfo? Describe() => Installed ? new OcrEngineInfo("Script", "script", OcrDataKind.Best, ["eng", "vie"], true) : null;

        public async Task<OcrPageResult> RecognizeAsync(OcrImage image, OcrOptions options, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var running = Interlocked.Increment(ref _running);
            lock (this)
            {
                MostAtOnce = Math.Max(MostAtOnce, running);
            }

            try
            {
                OnRecognize?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(Delay, cancellationToken);
                if (FailOn(call))
                {
                    throw new OcrException(OcrFailure.Failed, "scripted");
                }

                var (width, height) = (image.WidthInPoints, image.HeightInPoints);
                bool turned;
                lock (_renders)
                {
                    turned = _renders.Count > 0 && !_renders.Contains(image.Image.Pixels);
                }

                var confidence = Confidence(turned);
                return new OcrPageResult(
                    LayerPdf(width, height, Word, At.X * width, height - (At.Y * height)),
                    [new OcrWord(Word, new OcrBox(0, 0, 10, 10), confidence, 1, 1, 1)],
                    Word,
                    options.Hocr ? "<div class='ocr_page'/>" : null);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public Task<OcrOrientation?> DetectOrientationAsync(OcrImage image, CancellationToken cancellationToken)
        {
            lock (_renders)
            {
                _renders.Add(image.Image.Pixels);
            }

            return Task.FromResult(Orientation);
        }

        private static byte[] LayerPdf(double width, double height, string word, double x, double y)
        {
            using var document = new PdfDocument();
            var page = document.AddPage();
            page.Elements["/MediaBox"] = new PdfLiteral(string.Create(CultureInfo.InvariantCulture, $"[0 0 {width:0.###} {height:0.###}]"));
            var font = new PdfDictionary(document);
            font.Elements.SetName("/Type", "/Font");
            font.Elements.SetName("/Subtype", "/Type1");
            font.Elements.SetName("/BaseFont", "/Helvetica");
            var fonts = new PdfDictionary(document);
            fonts.Elements["/F1"] = font;
            var resources = new PdfDictionary(document);
            resources.Elements["/Font"] = fonts;
            page.Elements["/Resources"] = resources;
            var stream = new PdfContent(document);
            stream.CreateStream(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"BT 3 Tr /F1 10 Tf {x - 15:0.###} {y - 3.5:0.###} Td ({word}) Tj ET")));
            document.Internals.AddObject(stream);
            page.Elements["/Contents"] = stream.Reference;
            using var output = new MemoryStream();
            document.Save(output);
            return output.ToArray();
        }
    }
}

/// <summary>Running ocr.run on a file in tests.</summary>
internal static class OcrOperationTestExtensions
{
    public static async Task<OcrReport> ApplyAsync(this OcrOperation operation, string input, OcrParams parameters)
    {
        using var output = new MemoryStream();
        await operation.ApplyToFileAsync(input, output, parameters, new RecordingProgress(), TestContext.Current.CancellationToken);
        return OcrOperation.ReportOf(parameters)!;
    }
}
