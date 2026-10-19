using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Tests.Materializing;

/// <summary>Golden-file tests: plans over samples/ are written, then read back with PDFium and PDFsharp.</summary>
public sealed class MaterializerTests : IDisposable
{
    private readonly PdfiumFixture _pdfium;
    private readonly TempDirectory _temp = new();
    private readonly CountingRepairer _repairer;
    private readonly PdfSharpPlanMaterializer _materializer;

    public MaterializerTests(PdfiumFixture pdfium)
    {
        _pdfium = pdfium;
        _repairer = new CountingRepairer(new PdfiumSourceRepairer(pdfium.Gate));
        _materializer = new PdfSharpPlanMaterializer(_repairer);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task IdentityPlan_KeepsEveryPageAndTheDocumentStructure()
    {
        var output = await WriteAsync(Identity(6), Samples.LinksAnnots);

        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        (await PdfInspector.AllTextsAsync(document)).Select((t, i) => t.Contains($"Trang {i + 1} ", StringComparison.Ordinal)).ShouldAllBe(found => found);
        (await PdfInspector.BookmarksAsync(document)).ShouldBe(["Mục lục→0", "Ghi chú và đánh dấu→1", "Phụ lục→5"]);
        PdfInspector.Links(output).ShouldBe(
        [
            new LinkInfo(0, 3, null), new LinkInfo(0, 5, null), new LinkInfo(0, 2, null), new LinkInfo(0, null, "https://example.com/pdfpro"),
            new LinkInfo(2, 0, null),
        ]);
        PdfInspector.Catalog(output, c => c.Elements.ContainsKey("/PageLabels")).ShouldBeTrue();
        PdfInspector.Catalog(output, c => c.Elements.ContainsKey("/OpenAction")).ShouldBeTrue();
        _repairer.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Move_ReordersPagesAndLinksFollowTheirTargets()
    {
        var output = await WriteAsync(Identity(6).Move([3], 0), Samples.LinksAnnots);

        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        PageNumbers(await PdfInspector.AllTextsAsync(document)).ShouldBe([4, 1, 2, 3, 5, 6]);
        (await PdfInspector.BookmarksAsync(document)).ShouldBe(["Mục lục→1", "Ghi chú và đánh dấu→2", "Phụ lục→5"]);
        PdfInspector.Links(output).ShouldBe(
        [
            new LinkInfo(1, 0, null), new LinkInfo(1, 5, null), new LinkInfo(1, 3, null), new LinkInfo(1, null, "https://example.com/pdfpro"),
            new LinkInfo(3, 1, null),
        ]);
        PdfInspector.Catalog(output, c => c.Elements.ContainsKey("/PageLabels")).ShouldBeFalse("labels describe the old order");
    }

    [Fact]
    public async Task Move_ManyPagesOfALargeDocument_KeepsEveryPageOnce()
    {
        var plan = Identity(1000).Move([.. Enumerable.Range(0, 200)], 1000);

        var output = await WriteAsync(plan, Samples.Big1000);

        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        document.PageCount.ShouldBe(1000);
        var texts = await PdfInspector.TextsAsync(document, 0, 799, 800, 999);
        texts[0].ShouldContain("trang 201 / 1000");
        texts[1].ShouldContain("trang 1000 / 1000");
        texts[2].ShouldContain("trang 1 / 1000");
        texts[3].ShouldContain("trang 200 / 1000");
    }

    [Fact]
    public async Task Rotate_AddsToThePagesOwnRotationAndKeepsTheBoxes()
    {
        var output = await WriteAsync(Identity(4).Rotate([0, 1, 2, 3], 90), Samples.RotatedCropBox);

        await using var original = await PdfInspector.OpenAsync(_pdfium.Gate, Samples.RotatedCropBox);
        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        var before = await PdfInspector.GeometriesAsync(original);
        var after = await PdfInspector.GeometriesAsync(document);
        after.Select(g => g.RotationDegrees).ShouldBe([90, 180, 270, 0]);
        after.Select(g => g.Box).ShouldBe(before.Select(g => g.Box));
    }

    [Fact]
    public async Task Delete_RemovesPagesAndTheBookmarksThatPointedThere()
    {
        var output = await WriteAsync(Identity(6).Delete([1]), Samples.BasicVi);

        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        document.PageCount.ShouldBe(5);
        (await PdfInspector.BookmarksAsync(document)).ShouldBe(
        [
            "Trang bìa→0",
            "Chương I. Quy định chung→-",
            ">Điều 2. Giải thích từ ngữ→1",
            "Chương II. Tổ chức thực hiện→2",
            ">Điều 3. Trách nhiệm thi hành→2",
            ">Điều 4. Hiệu lực→3",
            "Phụ lục→4",
        ]);
    }

    [Fact]
    public async Task Delete_TakesTheDeletedPagesOutOfTheFile()
    {
        var output = await WriteAsync(Identity(1000).Delete([.. Enumerable.Range(100, 900)]), Samples.Big1000);

        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        document.PageCount.ShouldBe(100);
        (await PdfInspector.TextsAsync(document, 99))[0].ShouldContain("trang 100 / 1000");
        PdfInspector.PageObjectCount(output).ShouldBe(100, "deleted pages must not stay in the file");
        (await PdfInspector.BookmarksAsync(document)).ShouldBe(["Phần 1 (trang 1–100)→0"]);
    }

    [Fact]
    public async Task Delete_LinkTargets_RemovesOnlyThoseLinks()
    {
        var output = await WriteAsync(Identity(6).Delete([3, 5]), Samples.LinksAnnots);

        PdfInspector.Links(output).ShouldBe(
        [
            new LinkInfo(0, 2, null), new LinkInfo(0, null, "https://example.com/pdfpro"), new LinkInfo(2, 0, null),
        ]);
        PdfInspector.Catalog(output, c => c.Elements.ContainsKey("/OpenAction")).ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_TheOpeningPage_RemovesTheOpenAction()
    {
        var output = await WriteAsync(Identity(6).Delete([0]), Samples.LinksAnnots);

        PdfInspector.Catalog(output, c => c.Elements.ContainsKey("/OpenAction")).ShouldBeFalse();
        PdfInspector.Links(output).ShouldBeEmpty("the only link left pointed at the deleted first page");
    }

    [Fact]
    public async Task Duplicate_GivesTheCopyItsOwnAnnotations()
    {
        var output = await WriteAsync(Identity(6).Duplicate([1], RandomPageKeys.Instance), Samples.LinksAnnots);

        var annotations = PdfInspector.Annotations(output);
        annotations.Length.ShouldBe(7);
        annotations[1].Select(a => a.Subtype).ShouldBe(annotations[2].Select(a => a.Subtype));
        annotations[1].Length.ShouldBe(2);
        annotations[1].Select(a => a.ObjectNumber).Intersect(annotations[2].Select(a => a.ObjectNumber)).ShouldBeEmpty();
        annotations[1].ShouldAllBe(a => a.OwnerPage == 1);
        annotations[2].ShouldAllBe(a => a.OwnerPage == 2);
    }

    [Fact]
    public async Task Duplicate_OfAPageWithLinks_KeepsTheirTargets()
    {
        var output = await WriteAsync(Identity(6).Duplicate([0], RandomPageKeys.Instance), Samples.LinksAnnots);

        var links = PdfInspector.Links(output);
        links.Where(l => l.Page == 1).Select(l => (l.TargetPage, l.Uri)).ShouldBe(links.Where(l => l.Page == 0).Select(l => (l.TargetPage, l.Uri)));
        links.Where(l => l.Page == 0).Select(l => l.TargetPage).ShouldBe([4, 6, 3, null]);
    }

    [Fact]
    public async Task InsertBlank_AddsEmptyPagesOfTheRequestedSize()
    {
        var plan = Identity(6).InsertBlank(0, new PdfSize(595, 842), RandomPageKeys.Instance).InsertBlank(7, new PdfSize(612, 792), RandomPageKeys.Instance);

        var output = await WriteAsync(plan, Samples.BasicVi);

        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        document.PageCount.ShouldBe(8);
        document.PageSizes[0].ShouldBe(new PdfSize(595, 842));
        document.PageSizes[7].ShouldBe(new PdfSize(612, 792));
        (await PdfInspector.TextsAsync(document, 0, 7)).ShouldAllBe(t => t.Length == 0);
        (await PdfInspector.BookmarksAsync(document))[0].ShouldBe("Trang bìa→1");
    }

    [Fact]
    public async Task InsertFromAnotherFile_ImportsOnlyThosePages()
    {
        var plan = Identity(6).InsertFromSource(1, "s1", [499], RandomPageKeys.Instance);

        var output = await WriteAsync(plan, Samples.BasicVi, Samples.Big1000);

        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        document.PageCount.ShouldBe(7);
        (await PdfInspector.TextsAsync(document, 1))[0].ShouldContain("trang 500 / 1000");
        PdfInspector.PageObjectCount(output).ShouldBe(7, "importing one page must not bring the other 999");
    }

    [Fact]
    public async Task InsertFromAnotherFile_ReconnectsLinksBetweenInsertedPages()
    {
        // links-annots "Trang 1", "Trang 3", "Trang 4" appended to basic-vi (6 pages): output pages 6, 7, 8.
        var plan = Identity(6).InsertFromSource(6, "s1", [0, 2, 3], RandomPageKeys.Instance);

        var output = await WriteAsync(plan, Samples.BasicVi, Samples.LinksAnnots);

        PdfInspector.Links(output).ShouldBe(
        [
            new LinkInfo(6, 8, null), new LinkInfo(6, 7, null), new LinkInfo(6, null, "https://example.com/pdfpro"),
            new LinkInfo(7, 6, null),
        ]);
        PdfInspector.PageObjectCount(output).ShouldBe(9, "links to pages that were not inserted must not drag them in");
    }

    [Fact]
    public async Task InsertFromAnotherFile_KeepsTheForeignPageGeometry()
    {
        var plan = Identity(6).InsertFromSource(6, "s1", [0, 1, 2, 3], RandomPageKeys.Instance);

        var output = await WriteAsync(plan, Samples.BasicVi, Samples.RotatedCropBox);

        await using var original = await PdfInspector.OpenAsync(_pdfium.Gate, Samples.RotatedCropBox);
        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        (await PdfInspector.GeometriesAsync(document)).Skip(6).ShouldBe(await PdfInspector.GeometriesAsync(original));
    }

    [Theory]
    [InlineData(0, 255, 0, 0)]
    [InlineData(1, 0, 0, 255)]
    [InlineData(2, 0, 0, 0)]
    [InlineData(3, 0, 153, 0)]
    public async Task Crop_KeepsTheCornerTheUserSawOnEveryRotation(int page, int red, int green, int blue)
    {
        // Each page of rotated-cropbox has a coloured square in every corner; keep the displayed top-left 60 pt.
        var output = await WriteAsync(Identity(4).Crop([page], new PageCrop(0, 0, 60, 60)), Samples.RotatedCropBox);

        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        document.PageSizes[page].Width.ShouldBe(60, 0.01);
        document.PageSizes[page].Height.ShouldBe(60, 0.01);
        using var bitmap = await document.RenderPageAsync(page, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: CancellationToken);
        ShouldBeColor(bitmap.GetPixel(28, 28), red, green, blue);
        ShouldBeColor(bitmap.GetPixel(55, 55), 255, 255, 217);
    }

    [Fact]
    public async Task CropThenRotate_TurnsTheCroppedArea()
    {
        var plan = Identity(4).Crop([0], new PageCrop(0, 0, 60, 60)).Rotate([0], 90);

        var output = await WriteAsync(plan, Samples.RotatedCropBox);

        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        (await document.GetPageGeometryAsync(0, CancellationToken)).RotationDegrees.ShouldBe(90);
        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: CancellationToken);
        ShouldBeColor(bitmap.GetPixel(32, 28), 255, 0, 0); // the red top-left square is now top-right
        ShouldBeColor(bitmap.GetPixel(4, 55), 255, 255, 217);
    }

    [Fact]
    public async Task DamagedSource_IsRepairedByPdfium()
    {
        // A wrong startxref offset: PDFium rebuilds the cross-reference table, PDFsharp gives up.
        var damaged = _temp.Combine("damaged.pdf");
        await File.WriteAllBytesAsync(damaged, WithWrongStartXref(await File.ReadAllBytesAsync(Samples.BasicVi, CancellationToken)), CancellationToken);

        var output = await WriteAsync(Identity(6).Rotate([0], 90), damaged);

        await using var document = await PdfInspector.OpenAsync(_pdfium.Gate, output);
        document.PageCount.ShouldBe(6);
        (await document.GetPageGeometryAsync(0, CancellationToken)).RotationDegrees.ShouldBe(90);
        (await PdfInspector.BookmarksAsync(document)).Length.ShouldBe(9);
        _repairer.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task SourceNeitherLibraryCanRead_FailsAsUnreadable()
    {
        // broken.pdf lost its cross-reference table and trailer: even PDFium refuses it (the viewer says so too).
        var error = await Should.ThrowAsync<MaterializationException>(() => WriteAsync(Identity(1), Samples.Broken));

        error.Failure.ShouldBe(MaterializationFailure.SourceUnreadable);
        _repairer.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task UnreadableSource_FailsWithTheReason()
    {
        var garbage = _temp.Combine("garbage.pdf");
        await File.WriteAllTextAsync(garbage, "this is not a PDF", CancellationToken);

        var error = await Should.ThrowAsync<MaterializationException>(() => WriteAsync(Identity(1), garbage));

        error.Failure.ShouldBe(MaterializationFailure.SourceUnreadable);
        error.SourceId.ShouldBe("s0");
    }

    [Fact]
    public async Task EncryptedSource_IsRefused()
    {
        var error = await Should.ThrowAsync<MaterializationException>(() => WriteAsync(Identity(4), Samples.Encrypted));

        error.Failure.ShouldBe(MaterializationFailure.SourceEncrypted);
    }

    [Fact]
    public async Task MissingSource_IsReported()
    {
        var plan = Identity(6).InsertFromSource(0, "s1", [0], RandomPageKeys.Instance);

        var error = await Should.ThrowAsync<MaterializationException>(() => WriteAsync(plan, Samples.BasicVi, _temp.Combine("missing.pdf")));

        error.Failure.ShouldBe(MaterializationFailure.SourceMissing);
        error.SourceId.ShouldBe("s1");
    }

    [Fact]
    public async Task Cancellation_StopsBeforeWriting()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var output = new MemoryStream();

        await Should.ThrowAsync<OperationCanceledException>(() => _materializer.MaterializeAsync(
            Request(Identity(6), Samples.BasicVi), output, new RecordingProgress(), cancelled.Token));

        output.Length.ShouldBe(0);
    }

    [Fact]
    public async Task Progress_ReportsOpeningEveryPageAndWriting()
    {
        var progress = new RecordingProgress();
        using var output = new MemoryStream();

        await _materializer.MaterializeAsync(Request(Identity(6), Samples.BasicVi), output, progress, CancellationToken);

        progress.Reports[0].StageKey.ShouldBe(MaterializeStages.Open);
        progress.Reports.Where(r => r.StageKey == MaterializeStages.Pages).Select(r => r.Completed).ShouldBe([1, 2, 3, 4, 5, 6]);
        progress.Reports[^1].StageKey.ShouldBe(MaterializeStages.Write);
        output.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Request_MustNameAFileForEverySource()
    {
        var plan = Identity(6).InsertFromSource(0, "s1", [0], RandomPageKeys.Instance);

        await Should.ThrowAsync<ArgumentException>(() => WriteAsync(plan, Samples.BasicVi));
    }

    private static PagePlan Identity(int pageCount) => PagePlan.Identity("s0", pageCount, RandomPageKeys.Instance);

    private static byte[] WithWrongStartXref(byte[] pdf)
    {
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var start = text.LastIndexOf("startxref", StringComparison.Ordinal) + "startxref".Length;
        while (char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        var end = start;
        while (char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        var wrong = (long.Parse(text[start..end], System.Globalization.CultureInfo.InvariantCulture) + 7).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return System.Text.Encoding.Latin1.GetBytes(text[..start] + wrong + text[end..]);
    }

    private static int[] PageNumbers(IEnumerable<string> texts)
        => texts.Select(t => int.Parse(t.Split("Trang ", 2)[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static void ShouldBeColor((byte B, byte G, byte R) pixel, int red, int green, int blue)
    {
        ((double)pixel.R).ShouldBe(red, tolerance: 8);
        ((double)pixel.G).ShouldBe(green, tolerance: 8);
        ((double)pixel.B).ShouldBe(blue, tolerance: 8);
    }

    private MaterializeRequest Request(PagePlan plan, params string[] sourcePaths) => new()
    {
        Plan = plan,
        BaseSourceId = "s0",
        SourcePaths = sourcePaths.Select((path, i) => (Id: $"s{i}", path)).ToDictionary(s => s.Id, s => s.path),
        WorkingDirectory = _temp.Combine("work"),
    };

    private async Task<string> WriteAsync(PagePlan plan, params string[] sourcePaths)
    {
        var output = _temp.Combine($"out-{Guid.NewGuid():N}.pdf");
        await using (var stream = File.Create(output))
        {
            await _materializer.MaterializeAsync(Request(plan, sourcePaths), stream, new RecordingProgress(), CancellationToken);
        }

        return output;
    }

    /// <summary>Counts repairs, to prove which path a test took.</summary>
    private sealed class CountingRepairer(ISourceRepairer inner) : ISourceRepairer
    {
        public int Calls { get; private set; }

        public Task<string> RepairAsync(string sourcePath, string workingDirectory, CancellationToken cancellationToken)
        {
            Calls++;
            return inner.RepairAsync(sourcePath, workingDirectory, cancellationToken);
        }
    }
}
