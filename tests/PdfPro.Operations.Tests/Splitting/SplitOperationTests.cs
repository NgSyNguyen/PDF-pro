using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Splitting;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.Splitting;

/// <summary>Golden-file tests of every split mode over samples/: files, page counts, first-page text and names.</summary>
public sealed class SplitOperationTests(PdfiumFixture pdfium) : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<IPdfDocument> _documents = [];
    private readonly SplitOperation _operation = new(new PdfSharpPlanMaterializer(new PdfiumSourceRepairer(pdfium.Gate)), pdfium.Loader);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private string Output => _temp.Combine("out");

    public async ValueTask DisposeAsync()
    {
        foreach (var document in _documents)
        {
            await document.DisposeAsync();
        }

        _temp.Dispose();
    }

    [Fact]
    public void Operation_IsAnExport()
    {
        _operation.Id.ShouldBe("organize.split");
        _operation.Kind.ShouldBe(OperationKind.Export);
        _operation.ParamsType.ShouldBe(typeof(SplitParams));
    }

    [Fact]
    public async Task Marks_CutTheScanAtTheMarkedFirstPages_WithoutTheBlankSheets()
    {
        // The user marked pages 5, 8, 11 and 15 (page 1 always starts a file) and named the second file.
        var plan = PagePlan.Identity("s0", 18, RandomPageKeys.Instance).WithSplitMarks(new Dictionary<int, SplitMark>
        {
            [4] = new("Công văn 45: góp ý"),
            [7] = SplitMark.Unnamed,
            [10] = SplitMark.Unnamed,
            [14] = SplitMark.Unnamed,
        });
        var document = await OpenAsync(Samples.ScanVi, plan);
        var parameters = new SplitParams(SplitMode.FirstPages, SkipBlankPages: true);

        var preview = await PreviewAsync(document, parameters);
        var files = await WriteAsync(document, preview, parameters);

        Pages(preview).ShouldBe([[0, 1, 2], [4, 5], [7, 8], [10, 11, 12], [14, 15, 16, 17]]);
        preview.LeftOut.Order().ShouldBe([3, 6, 9, 13]);
        Names(files).ShouldBe(["scan-vi_001", "Công văn 45 góp ý", "scan-vi_003", "scan-vi_004", "scan-vi_005"]);
        (await PageCountsAsync(files)).ShouldBe([3, 2, 2, 3, 4]);
        foreach (var file in files)
        {
            await using var written = await pdfium.Loader.LoadAsync(file, null, CancellationToken);
            for (var page = 0; page < written.PageCount; page++)
            {
                (await BlankPageDetector.MeasurePageAsync(written, page, null, CancellationToken)).ShouldBeGreaterThan(BlankPageDetector.DefaultThresholdPercent);
            }
        }
    }

    [Fact]
    public async Task Marks_WithoutSkippingBlankPages_KeepTheSeparatorsAtTheEndOfEachFile()
    {
        var plan = PagePlan.Identity("s0", 18, RandomPageKeys.Instance).MarkSplit([4, 7, 10, 14], marked: true);

        var preview = await PreviewAsync(await OpenAsync(Samples.ScanVi, plan), new SplitParams(SplitMode.FirstPages));

        preview.Segments.Select(s => s.PageCount).ShouldBe([4, 3, 3, 4, 4]);
        preview.LeftOut.ShouldBeEmpty();
    }

    [Fact]
    public async Task FirstPagesList_OnFiles_IsWhatTheCommandLineUses()
    {
        var context = new OperationContext { InputPaths = [Samples.ScanVi], OutputDirectory = Output, WorkingDirectory = _temp.Combine("work") };

        var result = await _operation.ExecuteAsync(context, new SplitParams(SplitMode.FirstPages, FirstPages: "1, 5, 8, 11, 15", SkipBlankPages: true), new RecordingProgress(), CancellationToken);

        result.Succeeded.ShouldBeTrue();
        Names(result.OutputPaths).ShouldBe(["scan-vi_001", "scan-vi_002", "scan-vi_003", "scan-vi_004", "scan-vi_005"]);
        (await PageCountsAsync(result.OutputPaths)).ShouldBe([3, 2, 2, 3, 4]);
        result.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task BlankSeparators_FindTheSeparatorSheetsAndLeaveThemOut()
    {
        var parameters = new SplitParams(SplitMode.BlankSeparators);
        var document = await OpenAsync(Samples.ScanVi);

        var preview = await PreviewAsync(document, parameters);
        var files = await WriteAsync(document, preview, parameters);

        Pages(preview).ShouldBe([[0, 1, 2], [4, 5], [7, 8], [10, 11, 12], [14, 15, 16, 17]]);
        preview.LeftOut.Order().ShouldBe([3, 6, 9, 13]);
        (await PageCountsAsync(files)).ShouldBe([3, 2, 2, 3, 4]);
    }

    [Fact]
    public async Task BlankSeparators_AThresholdOfZero_FindsNoBlankPage()
    {
        var preview = await PreviewAsync(await OpenAsync(Samples.ScanVi), new SplitParams(SplitMode.BlankSeparators, BlankThresholdPercent: 0));

        preview.Segments.Count.ShouldBe(1);
        preview.Segments[0].PageCount.ShouldBe(18);
    }

    [Fact]
    public async Task Text_CutsWhereTheNumberLineMatches_AndNamesFilesAfterIt()
    {
        var parameters = new SplitParams(SplitMode.Text, TextPattern: @"số:\s*\S+", NamePattern: "{match}");
        var document = await OpenAsync(Samples.VanBanVi);

        var preview = await PreviewAsync(document, parameters);
        var files = await WriteAsync(document, preview, parameters);

        preview.NoText.ShouldBeFalse();
        preview.Segments.Select(s => s.First).ShouldBe([0, 3, 5, 7, 10]);
        Names(files).ShouldBe(["Số 12-QĐ-UBND", "Số 45-STTM-VP", "Số 07-TTr-PTC", "Số 03-BB-HĐNT", "Số 128-2026-HĐ-MT"]);
        (await PageCountsAsync(files)).ShouldBe([3, 2, 2, 3, 4]);
        await using var third = await PdfInspector.OpenAsync(pdfium.Gate, files[2]);
        (await PdfInspector.TextsAsync(third, 0))[0].ShouldContain("TỜ TRÌNH");
    }

    [Fact]
    public async Task Text_WithMatchCase_DoesNotMatchOtherCase()
    {
        var preview = await PreviewAsync(await OpenAsync(Samples.VanBanVi), new SplitParams(SplitMode.Text, TextPattern: @"số:\s*\d+", MatchCase: true));

        preview.Segments.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Text_OnAScan_SaysThereIsNoText()
    {
        var preview = await PreviewAsync(await OpenAsync(Samples.ScanVi), new SplitParams(SplitMode.Text, TextPattern: "Số"));

        preview.NoText.ShouldBeTrue();
        preview.Segments.Count.ShouldBe(1);
        SplitOperation.Warnings(preview).Select(m => m.Key).ShouldBe(["Split_NoText"]);
    }

    [Fact]
    public async Task Bookmarks_NameTheFilesWithTheirVietnameseTitles()
    {
        var parameters = new SplitParams(SplitMode.Bookmarks, NamePattern: "{bookmark}");
        var document = await OpenAsync(Samples.VanBanVi);

        var preview = await PreviewAsync(document, parameters);
        var files = await WriteAsync(document, preview, parameters);

        Names(files).ShouldBe(["Quyết định số 12-QĐ-UBND", "Công văn 45-STTM-VP", "Tờ trình 07-TTr-PTC", "Biên bản nghiệm thu 03-BB-HĐNT", "Hợp đồng 128-2026-HĐ-MT"]);
        files.ShouldAllBe(f => File.Exists(f));
        (await PageCountsAsync(files)).ShouldBe([3, 2, 2, 3, 4]);
        await using var contract = await PdfInspector.OpenAsync(pdfium.Gate, files[4]);
        (await PdfInspector.BookmarksAsync(contract)).ShouldBe(["Hợp đồng 128/2026/HĐ-MT→0"]);
    }

    [Fact]
    public async Task Bookmarks_ByLevel_SplitsAtTheMostSpecificTitle()
    {
        var document = await OpenAsync(Samples.BasicVi);
        var analysis = new SplitAnalysis(document);
        var level2 = new SplitParams(SplitMode.Bookmarks, BookmarkLevel: 2, NamePattern: "{bookmark}");
        await analysis.PrepareAsync(level2, new RecordingProgress(), CancellationToken);

        var top = _operation.Plan(analysis, level2 with { BookmarkLevel = 1 });
        var second = _operation.Plan(analysis, level2);
        var files = await WriteAsync(document, top, level2);

        analysis.MaxBookmarkLevel.ShouldBe(3);
        top.Segments.Select(s => s.Title).ShouldBe(["Trang bìa", "Chương I. Quy định chung", "Chương II. Tổ chức thực hiện", "Phụ lục"]);
        second.Segments.Select(s => s.Title).ShouldBe(
            ["Trang bìa", "Điều 1. Phạm vi điều chỉnh", "Điều 2. Giải thích từ ngữ", "Điều 3. Trách nhiệm thi hành", "Điều 4. Hiệu lực", "Phụ lục"]);
        await using var chapter = await PdfInspector.OpenAsync(pdfium.Gate, files[1]);
        chapter.PageCount.ShouldBe(2);
        (await PdfInspector.BookmarksAsync(chapter)).ShouldBe(
            ["Chương I. Quy định chung→0", ">Điều 1. Phạm vi điều chỉnh→0", ">>Khoản 1. Đối tượng áp dụng→0", ">Điều 2. Giải thích từ ngữ→1"]);
    }

    [Fact]
    public async Task Ranges_WriteOneFilePerPart()
    {
        var parameters = new SplitParams(SplitMode.Ranges, Ranges: "1-2; 3-6, 6", NamePattern: "{name} trang {first}-{last}");
        var document = await OpenAsync(Samples.BasicVi);

        var files = await WriteAsync(document, await PreviewAsync(document, parameters), parameters);

        Names(files).ShouldBe(["basic-vi trang 1-2", "basic-vi trang 3-6", "basic-vi trang 6-6"]);
        (await PageCountsAsync(files)).ShouldBe([2, 4, 1]);
    }

    [Fact]
    public async Task EveryN_KeepsLinksAndBookmarksInsideEachFile()
    {
        var parameters = new SplitParams(SplitMode.EveryN, PagesPerFile: 2);
        var document = await OpenAsync(Samples.LinksAnnots);

        var files = await WriteAsync(document, await PreviewAsync(document, parameters), parameters);

        (await PageCountsAsync(files)).ShouldBe([2, 2, 2]);
        PdfInspector.Links(files[0]).ShouldBe([new LinkInfo(0, null, "https://example.com/pdfpro")], "links to pages in other files are dropped");
        PdfInspector.Links(files[1]).ShouldBeEmpty();
        await using var first = await PdfInspector.OpenAsync(pdfium.Gate, files[0]);
        await using var last = await PdfInspector.OpenAsync(pdfium.Gate, files[2]);
        (await PdfInspector.BookmarksAsync(first)).ShouldBe(["Mục lục→0", "Ghi chú và đánh dấu→1"]);
        (await PdfInspector.BookmarksAsync(last)).ShouldBe(["Phụ lục→1"]);
    }

    [Fact]
    public async Task Size_EveryFileStaysUnderTheLimit()
    {
        // Every 5th page of big-1000.pdf: 200 pages sharing one ~460 kB image, each with a little text of its own.
        var parameters = new SplitParams(SplitMode.Size, MaxMegabytes: 0.6);
        var document = await OpenAsync(Samples.Big1000, EveryFifthPage());

        var preview = await PreviewAsync(document, parameters);
        var files = await WriteAsync(document, preview, parameters);

        preview.Segments.Count.ShouldBeInRange(2, 20);
        preview.OversizedFiles.ShouldBeEmpty();
        files.Count.ShouldBeGreaterThanOrEqualTo(preview.Segments.Count);
        files.ShouldAllBe(f => new FileInfo(f).Length <= 600_000);
        (await PageCountsAsync(files)).Sum().ShouldBe(200);
    }

    [Fact]
    public async Task Size_AFileOverTheLimit_IsCutInHalvesUntilItFits()
    {
        var parameters = new SplitParams(SplitMode.Size, MaxMegabytes: 0.5);
        var document = await OpenAsync(Samples.Big1000, EveryFifthPage());

        // One file of 40 pages, as if the estimate had been far too low.
        var files = await _operation.WriteAsync(document, [new([.. Enumerable.Range(0, 40)])], parameters, Output, [], new RecordingProgress(), CancellationToken);

        files.Count.ShouldBeGreaterThan(1);
        files.ShouldAllBe(f => new FileInfo(f).Length <= 500_000);
        (await PageCountsAsync(files)).Sum().ShouldBe(40);
        Names(files)[0].ShouldBe("big-1000_001");
    }

    [Fact]
    public async Task Size_APageTooBigOnItsOwn_IsReported()
    {
        var parameters = new SplitParams(SplitMode.Size, MaxMegabytes: 0.1);

        var preview = await PreviewAsync(await OpenAsync(Samples.Big1000, PagePlan.Create(EveryFifthPage().Take(3))), parameters);

        preview.Segments.Count.ShouldBe(3);
        preview.OversizedFiles.ShouldBe([0, 1, 2]);
        SplitOperation.Warnings(preview).Single().Arguments[0].ShouldBe(3);
    }

    [Fact]
    public async Task ExistingFiles_AreNumberedUnlessOverwritingIsAsked()
    {
        Directory.CreateDirectory(Output);
        await File.WriteAllTextAsync(Path.Combine(Output, "basic-vi_001.pdf"), "old", CancellationToken);
        var parameters = new SplitParams(SplitMode.EveryN, PagesPerFile: 3);
        var document = await OpenAsync(Samples.BasicVi);
        var preview = await PreviewAsync(document, parameters);

        var numbered = await WriteAsync(document, preview, parameters);
        var replaced = await WriteAsync(document, preview, parameters with { Overwrite = true });

        Names(numbered).ShouldBe(["basic-vi_001 (2)", "basic-vi_002"]);
        Names(replaced).ShouldBe(["basic-vi_001", "basic-vi_002"]);
        (await PageCountsAsync([Path.Combine(Output, "basic-vi_001.pdf")])).ShouldBe([3]);
        Directory.GetFiles(Output, "*.tmp").ShouldBeEmpty();
        Directory.GetFiles(Output, "*.bak").ShouldBeEmpty();
    }

    [Fact]
    public async Task TheDocumentsOwnFile_IsNeverReplaced()
    {
        var own = Path.Combine(Output, "basic-vi_001.pdf");
        Directory.CreateDirectory(Output);
        File.Copy(Samples.BasicVi, own);
        var parameters = new SplitParams(SplitMode.EveryN, PagesPerFile: 6, Overwrite: true);
        var document = await OpenAsync(Samples.BasicVi);

        var files = await _operation.WriteAsync(document, (await PreviewAsync(document, parameters)).Segments, parameters, Output, [own], new RecordingProgress(), CancellationToken);

        Names(files).ShouldBe(["basic-vi_001 (2)"]);
    }

    [Fact]
    public async Task Cancelling_LeavesNoFileBehind()
    {
        var parameters = new SplitParams(SplitMode.EveryN, PagesPerFile: 1);
        var document = await OpenAsync(Samples.BasicVi);
        var preview = await PreviewAsync(document, parameters);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        var progress = new CallbackProgress(p =>
        {
            if (p.StageKey == "Stage_SplitWrite" && p.Completed == 3)
            {
                cancel.Cancel();
            }
        });

        await Should.ThrowAsync<OperationCanceledException>(() => _operation.WriteAsync(document, preview.Segments, parameters, Output, [], progress, cancel.Token));

        Directory.GetFiles(Output).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailingRename_UndoesTheFilesAlreadyPublished()
    {
        Directory.CreateDirectory(Path.Combine(Output, "basic-vi_002.pdf")); // a folder in the way of the second file
        var parameters = new SplitParams(SplitMode.EveryN, PagesPerFile: 3, Overwrite: true);
        var document = await OpenAsync(Samples.BasicVi);
        var preview = await PreviewAsync(document, parameters);

        await Should.ThrowAsync<IOException>(() => _operation.WriteAsync(document, preview.Segments, parameters, Output, [], new RecordingProgress(), CancellationToken));

        Directory.GetFiles(Output).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(SplitMode.Ranges, "Ranges", null, null)]
    [InlineData(SplitMode.Ranges, "Ranges", "1-3, 9", null)]
    [InlineData(SplitMode.Text, "TextPattern", null, null)]
    [InlineData(SplitMode.Text, "TextPattern", "(unclosed", null)]
    [InlineData(SplitMode.FirstPages, "FirstPages", "0", null)]
    [InlineData(SplitMode.EveryN, "NamePattern", null, "{date}")]
    public async Task InvalidParameters_AreRefusedBeforeAnythingIsRead(SplitMode mode, string member, string? text, string? namePattern)
    {
        var parameters = new SplitParams(mode, Ranges: text, TextPattern: text, FirstPages: text, NamePattern: namePattern ?? "{name}");
        var analysis = new SplitAnalysis(await OpenAsync(Samples.BasicVi));

        var error = Should.Throw<OperationValidationException>(() => _operation.Plan(analysis, parameters));

        error.Errors.Select(e => e.MemberName).ShouldContain(member);
    }

    [Fact]
    public async Task Plan_WithoutPreparation_SaysWhatIsMissing()
    {
        var analysis = new SplitAnalysis(await OpenAsync(Samples.BasicVi));

        Should.Throw<InvalidOperationException>(() => _operation.Plan(analysis, new SplitParams(SplitMode.Bookmarks)));
        await analysis.PrepareAsync(new SplitParams(SplitMode.EveryN), new RecordingProgress(), CancellationToken);
        analysis.Bookmarks.ShouldBeNull("only what the mode needs is read");
        analysis.DarkPercents.ShouldBeNull();
    }

    private async Task<SplitDocument> OpenAsync(string path, PagePlan? plan = null)
    {
        var document = await pdfium.Loader.LoadAsync(path, password: null, CancellationToken);
        _documents.Add(document);
        return new SplitDocument
        {
            Plan = plan ?? PagePlan.Identity("s0", document.PageCount, RandomPageKeys.Instance),
            BaseSourceId = "s0",
            Documents = new Dictionary<string, IPdfDocument> { ["s0"] = document },
            SourcePaths = new Dictionary<string, string> { ["s0"] = path },
            Name = Path.GetFileNameWithoutExtension(path),
            WorkingDirectory = _temp.Combine("work"),
        };
    }

    private async Task<SplitPreview> PreviewAsync(SplitDocument document, SplitParams parameters)
    {
        var analysis = new SplitAnalysis(document);
        await analysis.PrepareAsync(parameters, new RecordingProgress(), CancellationToken);
        return _operation.Plan(analysis, parameters);
    }

    private Task<IReadOnlyList<string>> WriteAsync(SplitDocument document, SplitPreview preview, SplitParams parameters)
        => _operation.WriteAsync(document, preview.Segments, parameters, Output, [], new RecordingProgress(), CancellationToken);

    private async Task<int[]> PageCountsAsync(IReadOnlyList<string> files)
    {
        var counts = new int[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            await using var document = await pdfium.Loader.LoadAsync(files[i], null, CancellationToken);
            counts[i] = document.PageCount;
        }

        return counts;
    }

    private static PagePlan EveryFifthPage()
        => PagePlan.Create(PagePlan.Identity("s0", 1000, RandomPageKeys.Instance).Where((_, i) => i % 5 == 0));

    private static int[][] Pages(SplitPreview preview) => [.. preview.Segments.Select(s => s.Pages.ToArray())];

    private static string[] Names(IReadOnlyList<string> files) => [.. files.Select(f => Path.GetFileNameWithoutExtension(f))];

    private sealed class CallbackProgress(Action<OperationProgress> report) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => report(value);
    }
}
