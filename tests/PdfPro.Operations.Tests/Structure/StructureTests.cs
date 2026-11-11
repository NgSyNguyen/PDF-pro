using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Core.Structure;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Structure;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Tests.Structure;

/// <summary>
/// The structure of a document: bookmarks written (read back by PDFium), made from headings (by size or pattern),
/// description and opening settings (Info and XMP agree, PDF/A identification kept), page labels, and links — made,
/// found in the text and removed.
/// </summary>
public sealed class StructureTests(PdfiumFixture pdfium)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bookmarks_AreWritten_WithTheirPagesLookAndChildren_AndReadBackByPdfium()
    {
        using var temp = new TempDirectory();
        var output = temp.Combine("bookmarks.pdf");
        BookmarkNode[] tree =
        [
            new("Chương I. Quy định chung", 0, 700, [new("Điều 1. Phạm vi", 1), new("Điều 2. Đối tượng", 2, 500)], Bold: true, Color: "#C00000", Open: true),
            new("Phụ lục", 5, Italic: true),
        ];

        await Apply(new SetBookmarksOperation(), Samples.BasicVi, output, new SetBookmarksParams(tree));

        await using (var document = await pdfium.Loader.LoadAsync(output, null, Token))
        {
            var bookmarks = await document.GetBookmarksAsync(Token);
            bookmarks.Select(b => b.Title).ShouldBe(["Chương I. Quy định chung", "Phụ lục"]);
            bookmarks[0].Destination!.PageIndex.ShouldBe(0);
            bookmarks[0].Children.Select(c => (c.Title, c.Destination!.PageIndex)).ShouldBe([("Điều 1. Phạm vi", 1), ("Điều 2. Đối tượng", 2)]);
            bookmarks[1].Destination!.PageIndex.ShouldBe(5);
        }

        using var read = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var catalog = read.Internals.Catalog;
        Name(catalog, "/PageMode").ShouldBe("/UseOutlines");
        var root = AsDictionary(catalog.Elements["/Outlines"])!;
        root.Elements.GetInteger("/Count").ShouldBe(4, "two items, and the two children of the open one");
        var first = AsDictionary(root.Elements["/First"])!;
        first.Elements.GetInteger("/F").ShouldBe(2);
        first.Elements.GetInteger("/Count").ShouldBe(2);
        AsArray(first.Elements["/C"])!.Elements.Count.ShouldBe(3);
        AsDictionary(first.Elements["/Next"])!.Elements.GetInteger("/F").ShouldBe(1);

        var none = temp.Combine("none.pdf");
        await Apply(new SetBookmarksOperation(), output, none, new SetBookmarksParams([]));
        using var empty = PdfReader.Open(none, PdfDocumentOpenMode.Import);
        empty.Internals.Catalog.Elements.ContainsKey("/Outlines").ShouldBeFalse();
        empty.Internals.Catalog.Elements.ContainsKey("/PageMode").ShouldBeFalse();
        new SetBookmarksOperation().Validate(new SetBookmarksParams([new("x", 6)]), new Core.Documents.DocumentInfo(6)).IsValid.ShouldBeFalse();
        new SetBookmarksOperation().Validate(new SetBookmarksParams([new(" ", 0)]), new Core.Documents.DocumentInfo(6)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task TheContractsHeadings_BecomeBookmarks_ByPattern_AndBySize()
    {
        var byPattern = await BookmarkGenerator.GenerateAsync(Samples.ContractV1, null, new BookmarkGenerationOptions(HeadingRule.Patterns, ["^HỢP ĐỒNG CUNG CẤP", @"^Điều \d+\."]), Token);
        var bySize = await BookmarkGenerator.GenerateAsync(Samples.ContractV1, null, new BookmarkGenerationOptions(MinRatio: 1.05), Token);

        var title = byPattern.ShouldHaveSingleItem();
        title.Title.ShouldStartWith("HỢP ĐỒNG CUNG CẤP DỊCH VỤ");
        title.Page.ShouldBe(0);
        var articles = title.Children.ShouldNotBeNull();
        articles.Select(a => a.Title[..a.Title.IndexOf('.')]).ShouldBe([.. Enumerable.Range(1, 9).Select(i => $"Điều {i}")]);
        articles[0].Title.ShouldBe("Điều 1. Nội dung dịch vụ");
        articles.Select(a => a.Page).ShouldBeInOrder();
        articles[^1].Page.ShouldBe(3);
        articles.ShouldAllBe(a => a.Top > 0);

        var sized = bySize.ShouldHaveSingleItem();
        sized.Title.ShouldBe(title.Title);
        sized.Children!.Select(a => a.Title).ShouldBe(articles.Select(a => a.Title));
        await Should.ThrowAsync<ArgumentException>(() => BookmarkGenerator.GenerateAsync(Samples.ContractV1, null, new BookmarkGenerationOptions(HeadingRule.Patterns, ["("]), Token));
    }

    [Fact]
    public async Task TheDescription_IsWrittenToInfoAndXmp_KeepingWhatElseTheXmpSays()
    {
        using var temp = new TempDirectory();
        var input = temp.Combine("pdfa.pdf");
        using (var document = PdfReader.Open(Samples.BasicVi, PdfDocumentOpenMode.Modify))
        {
            PdfPro.Operations.Converting.PdfA.PdfAMetadata.Write(document, PdfPro.Operations.Converting.PdfALevel.A2B, DateTimeOffset.Now);
            using var stream = File.Create(input);
            PdfPro.Operations.Materializing.PdfWriting.Save(document, stream);
        }

        var output = temp.Combine("described.pdf");
        var operation = new SetMetadataOperation { Now = () => new DateTimeOffset(2026, 11, 11, 9, 30, 0, TimeSpan.FromHours(7)) };
        var description = new DocumentDescription("Quyết định thành lập tổ kiểm thử", "Ủy ban nhân dân xã", "Kiểm thử", "kiểm thử, phần mềm", "");

        await Apply(operation, input, output, new SetMetadataParams(description, new OpenSettings(2, OpenZoom.FitWidth, Panel: OpenPanel.Bookmarks, ShowTitle: true)));

        var structure = DocumentStructureReader.Read(output);
        structure.Description.ShouldBe(description with { Creator = null });
        structure.Modified.ShouldBe(new DateTimeOffset(2026, 11, 11, 9, 30, 0, TimeSpan.FromHours(7)));
        structure.Open.ShouldBe(new OpenSettings(2, OpenZoom.FitWidth, null, OpenPanel.Bookmarks, true));
        var xmp = Xmp(output);
        xmp.ShouldContain("Quyết định thành lập tổ kiểm thử");
        xmp.ShouldContain("Ủy ban nhân dân xã");
        xmp.ShouldContain("<pdfaid:part>2</pdfaid:part>");
        xmp.ShouldContain("2026-11-11T09:30:00+07:00");

        var zoomed = temp.Combine("zoomed.pdf");
        await Apply(operation, output, zoomed, new SetMetadataParams(Open: new OpenSettings(Zoom: OpenZoom.Percent, Percent: 150, Panel: OpenPanel.Default)));
        var again = DocumentStructureReader.Read(zoomed);
        again.Open.ShouldBe(new OpenSettings(2, OpenZoom.Percent, 150, OpenPanel.Default, true));
        again.Description.ShouldBe(structure.Description, "the description is left as it was");
        operation.Validate(new SetMetadataParams(), new Core.Documents.DocumentInfo(6)).IsValid.ShouldBeFalse();
        operation.Validate(new SetMetadataParams(Open: new OpenSettings(Zoom: OpenZoom.Percent, Percent: 5)), new Core.Documents.DocumentInfo(6)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task PageLabels_AreWritten_AndReadBack_WithARangeAtTheFirstPage()
    {
        using var temp = new TempDirectory();
        var output = temp.Combine("labels.pdf");
        PageLabelRange[] ranges = [new(0, PageLabelStyle.LowerRoman), new(2), new(5, PageLabelStyle.None, "Phụ lục")];

        await Apply(new SetPageLabelsOperation(), Samples.BasicVi, output, new SetPageLabelsParams(ranges));

        var read = DocumentStructureReader.Read(output).PageLabels;
        read.ShouldBe(ranges);
        Enumerable.Range(0, 6).Select(p => PageLabels.Of(read, p)).ShouldBe(["i", "ii", "1", "2", "3", "Phụ lục"]);

        var late = temp.Combine("late.pdf");
        await Apply(new SetPageLabelsOperation(), Samples.BasicVi, late, new SetPageLabelsParams([new(3, PageLabelStyle.UpperLetters, "PL-", 2)]));
        var added = DocumentStructureReader.Read(late).PageLabels;
        added.ShouldBe([new PageLabelRange(0), new PageLabelRange(3, PageLabelStyle.UpperLetters, "PL-", 2)]);
        PageLabels.Of(added, 5).ShouldBe("PL-D");

        var cleared = temp.Combine("cleared.pdf");
        await Apply(new SetPageLabelsOperation(), output, cleared, new SetPageLabelsParams([]));
        DocumentStructureReader.Read(cleared).PageLabels.ShouldBeEmpty();
    }

    [Fact]
    public async Task Links_AreMade_ToTheWebAndToPages_ListedWithTheirText_AndRemoved()
    {
        using var temp = new TempDirectory();
        var input = temp.Combine("links.pdf");
        TextPdf(input, ["Xem chi tiết tại https://pdfpro.example.vn/huong-dan.", "Liên hệ: hotro@pdfpro.example.vn", "Trang chủ www.example.com"]);

        var found = await LinkDetector.FindAsync(input, null, Token);

        found.Select(f => f.Target.Url).ShouldBe(["https://pdfpro.example.vn/huong-dan", "mailto:hotro@pdfpro.example.vn", "https://www.example.com"]);
        var toPage = new LinkSpec(0, new PdfRect(72, 500, 200, 520), new LinkTarget(Page: 0, Top: 800));
        var linked = temp.Combine("linked.pdf");
        await Apply(new EditLinksOperation(), input, linked, new EditLinksParams([.. found, toPage]));

        var links = DocumentStructureReader.Read(linked).Links;
        links.Count.ShouldBe(4);
        links[0].Target.ShouldBe(new LinkTarget("https://pdfpro.example.vn/huong-dan"));
        links[0].Text.ShouldNotBeNull().ShouldContain("pdfpro.example.vn");
        links[3].Target.ShouldBe(new LinkTarget(Page: 0, Top: 800));
        (await LinkDetector.FindAsync(linked, null, Token)).ShouldBeEmpty("every address has its link");

        var unlinked = temp.Combine("unlinked.pdf");
        await Apply(new EditLinksOperation(), linked, unlinked, new EditLinksParams(Remove: [new LinkRef(links[1].Page, links[1].Index, links[1].Rect)]));
        DocumentStructureReader.Read(unlinked).Links.Select(l => l.Target!.Url ?? "page").ShouldBe(["https://pdfpro.example.vn/huong-dan", "https://www.example.com/", "page"], "addresses are written as URIs");

        var operation = new EditLinksOperation();
        var info = new Core.Documents.DocumentInfo(1);
        operation.Validate(new EditLinksParams([new LinkSpec(0, new PdfRect(0, 0, 10, 10), new LinkTarget("javascript:alert(1)"))]), info).IsValid.ShouldBeFalse();
        operation.Validate(new EditLinksParams([new LinkSpec(0, new PdfRect(0, 0, 10, 10), new LinkTarget("file:///C:/Windows/notepad.exe"))]), info).IsValid.ShouldBeFalse();
        operation.Validate(new EditLinksParams([new LinkSpec(0, new PdfRect(0, 0, 10, 10), new LinkTarget(Page: 1))]), info).IsValid.ShouldBeFalse();
        EditLinksOperation.IsAllowedUrl("https://a.vn").ShouldBeTrue();
        EditLinksOperation.IsAllowedUrl("mailto:").ShouldBeFalse();
        await Should.ThrowAsync<InvalidDataException>(() => Apply(operation, unlinked, temp.Combine("x.pdf"), new EditLinksParams(Remove: [new LinkRef(0, 0, new PdfRect(1, 1, 2, 2))])));
    }

    private static async Task Apply<TParams>(PdfPro.Operations.Stamping.DocumentContentOperation<TParams> operation, string input, string output, TParams parameters)
        where TParams : class
    {
        await using var stream = File.Create(output);
        await operation.ApplyToFileAsync(input, stream, parameters, new RecordingProgress(), Token);
    }

    private static string Xmp(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return Encoding.UTF8.GetString(AsDictionary(document.Internals.Catalog.Elements["/Metadata"])!.Stream.Value);
    }

    /// <summary>One page of lines in Noto Sans.</summary>
    private static void TextPdf(string path, IReadOnlyList<string> lines)
    {
        FontLibrary.Register();
        using var document = new PdfDocument();
        var page = document.AddPage();
        using (var graphics = XGraphics.FromPdfPage(page))
        {
            var font = new XFont(Core.Text.FontFamilies.NotoSans, 12);
            for (var i = 0; i < lines.Count; i++)
            {
                graphics.DrawString(lines[i], font, XBrushes.Black, new XPoint(72, 100 + (i * 30)));
            }
        }

        using var stream = File.Create(path);
        PdfPro.Operations.Materializing.PdfWriting.Save(document, stream);
    }
}
