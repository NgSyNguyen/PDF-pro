using PdfPro.Core.Operations;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Merging;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.Merging;

/// <summary>Golden-file tests of merging samples/: page order, bookmarks, links, table of contents, sizes, passwords.</summary>
public sealed class MergeOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private const string OwnerPassword = "chu-so-huu-456"; // fake owner password of samples/encrypted.pdf

    private readonly TempDirectory _temp = new();
    private readonly MergeOperation _operation = new(new PdfSharpPlanMaterializer(new PdfiumSourceRepairer(pdfium.Gate)), pdfium.Loader);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operation_IsAnExport()
    {
        _operation.Id.ShouldBe("organize.merge");
        _operation.Kind.ShouldBe(OperationKind.Export);
    }

    [Fact]
    public async Task TwoFiles_KeepTheirBookmarksUnderAFileBookmarkAndTheirLinks()
    {
        var output = await MergeAsync([Samples.BasicVi, Samples.LinksAnnots], new MergeParams([new(), new()]));

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        document.PageCount.ShouldBe(12);
        (await PdfInspector.TextsAsync(document, 6))[0].ShouldContain("Trang 1 ");
        (await PdfInspector.BookmarksAsync(document)).ShouldBe(
        [
            "basic-vi→0",
            ">Trang bìa→0",
            ">Chương I. Quy định chung→1",
            ">>Điều 1. Phạm vi điều chỉnh→1",
            ">>>Khoản 1. Đối tượng áp dụng→1",
            ">>Điều 2. Giải thích từ ngữ→2",
            ">Chương II. Tổ chức thực hiện→3",
            ">>Điều 3. Trách nhiệm thi hành→3",
            ">>Điều 4. Hiệu lực→4",
            ">Phụ lục→5",
            "links-annots→6",
            ">Mục lục→6",
            ">Ghi chú và đánh dấu→7",
            ">Phụ lục→11",
        ]);
        PdfInspector.Links(output).ShouldBe(
        [
            new LinkInfo(6, 9, null), new LinkInfo(6, 11, null), new LinkInfo(6, 8, null), new LinkInfo(6, null, "https://example.com/pdfpro"),
            new LinkInfo(8, 6, null),
        ]);
        var metadata = await document.GetMetadataAsync(CancellationToken);
        metadata.Title.ShouldBe("merged");
        metadata.Author.ShouldBeNull("the first file's author does not carry over");
        PdfInspector.Catalog(output, c => c.Elements.ContainsKey("/OpenAction")).ShouldBeFalse();
    }

    [Fact]
    public async Task ChosenPagesAndTitles_AreUsed()
    {
        var parameters = new MergeParams([new("1-2"), new("4-5", "Công văn góp ý")], Title: "Hồ sơ mẫu");

        var output = await MergeAsync([Samples.BasicVi, Samples.VanBanVi], parameters);

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        document.PageCount.ShouldBe(4);
        (await PdfInspector.TextsAsync(document, 2))[0].ShouldContain("Số: 45/STTM-VP");
        var bookmarks = await PdfInspector.BookmarksAsync(document);
        bookmarks.ShouldContain("Công văn góp ý→2");
        bookmarks.ShouldContain(">Công văn 45/STTM-VP→2");
        bookmarks.ShouldNotContain(b => b.Contains("Quyết định", StringComparison.Ordinal), "bookmarks of pages left out are dropped");
        (await document.GetMetadataAsync(CancellationToken)).Title.ShouldBe("Hồ sơ mẫu");
    }

    [Fact]
    public async Task WithoutBookmarkOptions_TheResultHasNoOutline()
    {
        var output = await MergeAsync([Samples.BasicVi, Samples.LinksAnnots], new MergeParams([new(), new()], KeepBookmarks: false, BookmarkPerFile: false));

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        (await PdfInspector.BookmarksAsync(document)).ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyFileBookmarks_WhenTheFilesOwnAreNotKept()
    {
        var output = await MergeAsync([Samples.BasicVi, Samples.VanBanVi], new MergeParams([new(), new()], KeepBookmarks: false));

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        (await PdfInspector.BookmarksAsync(document)).ShouldBe(["basic-vi→0", "vanban-vi→6"]);
    }

    [Fact]
    public async Task TableOfContents_ListsEveryFileWithLinksAndVietnameseText()
    {
        var parameters = new MergeParams(
            [new(Title: "Quy định chung"), new(Title: "Tập văn bản hành chính"), new(Title: "Liên kết")],
            TableOfContents: true,
            TableOfContentsHeading: "Mục lục");

        var output = await MergeAsync([Samples.BasicVi, Samples.VanBanVi, Samples.LinksAnnots], parameters);

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        document.PageCount.ShouldBe(1 + 6 + 14 + 6);
        var toc = (await PdfInspector.TextsAsync(document, 0))[0];
        toc.ShouldContain("Mục lục");
        toc.ShouldContain("Quy định chung");
        toc.ShouldContain("Tập văn bản hành chính");
        toc.ShouldContain("Liên kết");
        PdfInspector.Links(output).Where(l => l.Page == 0).Select(l => l.TargetPage).ShouldBe([1, 7, 21]);
        (await PdfInspector.BookmarksAsync(document)).Where(b => !b.StartsWith('>')).ShouldBe(["Quy định chung→1", "Tập văn bản hành chính→7", "Liên kết→21"]);
    }

    [Fact]
    public async Task StartFilesOnOddPages_AddsBlankBacks()
    {
        var parameters = new MergeParams([new(), new("1-3"), new("1")], StartFilesOnOddPages: true);
        var sources = await OpenAsync([Samples.RotatedCropBox, Samples.VanBanVi, Samples.BasicVi]);

        var layout = _operation.Layout(sources, parameters);
        var output = await _operation.WriteAsync(sources, parameters, _temp.Combine("out", "duplex.pdf"), _temp.Combine("work"), new RecordingProgress(), CancellationToken);

        layout.FirstPositions.Select(p => p + 1).ShouldBe([1, 5, 9]);
        layout.AddedBlankPages.ShouldBe(1);
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        document.PageCount.ShouldBe(9);
        (await PdfInspector.TextsAsync(document, 7))[0].ShouldBeEmpty();
        (await PdfInspector.TextsAsync(document, 8))[0].ShouldStartWith("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM");
    }

    [Fact]
    public async Task PageSizeA4_MakesEveryPageA4InItsOwnOrientation()
    {
        var output = await MergeAsync([Samples.RotatedCropBox, Samples.BasicVi], new MergeParams([new(), new()], PageSize: MergePageSize.A4));

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        foreach (var geometry in await PdfInspector.GeometriesAsync(document))
        {
            var size = geometry.DisplaySize;
            Math.Min(size.Width, size.Height).ShouldBe(595.28, tolerance: 0.5);
            Math.Max(size.Width, size.Height).ShouldBe(841.89, tolerance: 0.5);
        }
    }

    [Fact]
    public async Task ProtectedFile_NeedsThePermissionToTakeItsPages()
    {
        var error = await Should.ThrowAsync<MergeSourceException>(() => MergeSource.OpenAsync(pdfium.Loader, Samples.Encrypted, "test123", CancellationToken));
        error.Failure.ShouldBe(MergeSourceFailure.NotPermitted, "the author forbids copying");

        var wrong = await Should.ThrowAsync<PdfDocumentException>(() => MergeSource.OpenAsync(pdfium.Loader, Samples.Encrypted, "sai", CancellationToken));
        wrong.Failure.ShouldBe(PdfOpenFailure.WrongPassword);
        (await Should.ThrowAsync<PdfDocumentException>(() => MergeSource.OpenAsync(pdfium.Loader, Samples.Encrypted, null, CancellationToken)))
            .Failure.ShouldBe(PdfOpenFailure.PasswordRequired);
    }

    [Fact]
    public async Task ProtectedFile_WithTheOwnerPassword_IsMergedAndTheResultIsNotEncrypted()
    {
        var passwords = new InputPasswords();
        passwords.Set(Samples.Encrypted, OwnerPassword);
        var context = new OperationContext { InputPaths = [Samples.Encrypted, Samples.BasicVi], OutputDirectory = _temp.Combine("out"), WorkingDirectory = _temp.Combine("work"), Passwords = passwords };

        var result = await _operation.ExecuteAsync(context, new MergeParams([new(), new()]), new RecordingProgress(), CancellationToken);

        result.Succeeded.ShouldBeTrue();
        result.OutputPaths.ShouldBe([_temp.Combine("out", "encrypted-merged.pdf")]);
        await using var document = await pdfium.Loader.LoadAsync(result.OutputPaths[0], password: null, CancellationToken);
        document.Features.IsEncrypted.ShouldBeFalse();
        document.PageCount.ShouldBe(8);
        (await document.GetPageTextAsync(0, CancellationToken)).ShouldContain("AES-256");
        context.ToString().ShouldNotContain(OwnerPassword);
        passwords.ToString().ShouldBe("1 password(s)");
    }

    [Fact]
    public async Task TheOutputMayBeOneOfTheInputs()
    {
        var input = _temp.Combine("hoso.pdf");
        File.Copy(Samples.BasicVi, input);

        await _operation.WriteAsync(await OpenAsync([input, Samples.LinksAnnots]), new MergeParams([new(), new()]), input, _temp.Combine("work"), new RecordingProgress(), CancellationToken);

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, input);
        document.PageCount.ShouldBe(12);
        Directory.GetFiles(_temp.Path, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancelling_LeavesNoOutput()
    {
        var sources = await OpenAsync([Samples.Big1000, Samples.BasicVi]);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _operation.WriteAsync(sources, new MergeParams([new(), new()]), _temp.Combine("out", "x.pdf"), _temp.Combine("work"), new RecordingProgress(), cancel.Token));

        Directory.GetFiles(_temp.Combine("out")).ShouldBeEmpty();
    }

    [Fact]
    public async Task BadParameters_AreRefused()
    {
        var sources = await OpenAsync([Samples.BasicVi, Samples.LinksAnnots]);

        Should.Throw<OperationValidationException>(() => _operation.Layout(sources, new MergeParams([new()])));
        Should.Throw<OperationValidationException>(() => _operation.Layout(sources, new MergeParams([new(), new("7")])))
            .Errors.Single().MemberName.ShouldBe("Items[1]");
        Should.Throw<OperationValidationException>(() => _operation.Layout(sources, new MergeParams([new(), new()], TableOfContents: true)))
            .Errors.Single().MemberName.ShouldBe("TableOfContentsHeading");
    }

    private async Task<string> MergeAsync(string[] paths, MergeParams parameters)
        => await _operation.WriteAsync(await OpenAsync(paths), parameters, _temp.Combine("out", "merged.pdf"), _temp.Combine("work"), new RecordingProgress(), CancellationToken);

    private async Task<IReadOnlyList<MergeSource>> OpenAsync(string[] paths)
    {
        var sources = new List<MergeSource>();
        foreach (var path in paths)
        {
            sources.Add(await MergeSource.OpenAsync(pdfium.Loader, path, password: null, CancellationToken));
        }

        return sources;
    }
}
