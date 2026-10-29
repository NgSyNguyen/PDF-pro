using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Overlay;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Tests.Annotating;

/// <summary>annotate.flatten: comments drawn into the page content and taken off, the page looking the same.</summary>
public sealed class FlattenAnnotationsOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(7));
    private static readonly RgbColor Yellow = new(255, 230, 0);
    private static readonly RgbColor Red = new(220, 30, 30);

    private readonly TempDirectory _temp = new();
    private readonly FlattenAnnotationsOperation _operation = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operation_IsAContentChange()
    {
        _operation.Id.ShouldBe("annotate.flatten");
        _operation.Kind.ShouldBe(OperationKind.Content);
        var document = new Core.Documents.DocumentInfo(2);
        _operation.Validate(new FlattenAnnotationsParams(), document).IsValid.ShouldBeTrue();
        _operation.Validate(new FlattenAnnotationsParams("2"), document).IsValid.ShouldBeTrue();
        _operation.Validate(new FlattenAnnotationsParams("3"), document).IsValid.ShouldBeFalse();
        _operation.Validate(new FlattenAnnotationsParams(Groups: [AnnotationGroup.Markup]), document).IsValid.ShouldBeTrue();
        _operation.Validate(new FlattenAnnotationsParams(Groups: []), document).IsValid.ShouldBeFalse("nothing to flatten");
        _operation.Validate(new FlattenAnnotationsParams(Groups: [(AnnotationGroup)9]), document).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task OnlyTheChosenFamilies_AreFlattened()
    {
        var input = Annotated(page => { });

        var output = await ApplyAsync(input, new FlattenAnnotationsParams(Groups: [AnnotationGroup.Markup, AnnotationGroup.Stamps]));

        (await ReadAsync(output, 0)).Where(a => !a.IsReply && a.IsComment).Select(a => a.Name).ShouldBe(["n1", "box"], "the highlight was flattened");
        (await ReadAsync(output, 0)).ShouldContain(a => a.Name == "n1-r", "the note keeps its answer");
        new FlattenAnnotationsParams(Groups: [AnnotationGroup.Markup]).ShouldBe(new FlattenAnnotationsParams(Groups: [AnnotationGroup.Markup]));
    }

    [Fact]
    public async Task Comments_AreDrawnIntoThePage_AsTheyWereShown_AndTakenOff()
    {
        var input = Annotated(page => { });

        var output = await ApplyAsync(input, new FlattenAnnotationsParams());

        (await ReadAsync(output, 0)).ShouldBeEmpty("the comments, their answer and their pop-ups are gone");
        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, input);
        await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var shown = await RenderAsync(before, 0, PdfRenderFlags.Annotations);
        using var contentBefore = await RenderAsync(before, 0, PdfRenderFlags.None);
        using var flattened = await RenderAsync(after, 0, PdfRenderFlags.None);
        Changes(contentBefore, flattened).Count.ShouldBeGreaterThan(1000, "the comments are page content now");
        Changes(shown, flattened).Count.ShouldBeLessThan(shown.Width * shown.Height / 200, "the page looks as it did with its comments");
        var (b, g, r) = flattened.GetPixel(150, (int)(shown.Height - 706));
        (r > 200 && g > 180 && b < 120).ShouldBeTrue($"the highlight is yellow on the page ({r},{g},{b})");
    }

    [Fact]
    public async Task OnlyTheChosenPages_AreFlattened()
    {
        var input = Annotated(page => { }, onBothPages: true);

        var output = await ApplyAsync(input, new FlattenAnnotationsParams("2"));

        (await ReadAsync(output, 0)).Count(a => a.IsComment).ShouldBe((await ReadAsync(input, 0)).Count(a => a.IsComment));
        (await ReadAsync(output, 1)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Links_AndCommentsWithoutAnAppearance_Stay_HiddenOnes_GoUndrawn()
    {
        var input = Annotated(page =>
        {
            Find(page, "h1").Elements.Remove("/AP");
            Find(page, "box").Elements.SetInteger("/F", 2); // hidden
        });

        var output = await ApplyAsync(input, new FlattenAnnotationsParams());

        var after = await ReadAsync(output, 0);
        after.Select(a => a.Name).ShouldBe(["h1"], "nothing to draw for it: it would have vanished");
        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, input);
        await using var flattenedDocument = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var contentBefore = await RenderAsync(before, 0, PdfRenderFlags.None);
        using var flattened = await RenderAsync(flattenedDocument, 0, PdfRenderFlags.None);
        Changes(contentBefore, flattened).ShouldNotContain(p => p.X >= 100 && p.X <= 250 && p.Y >= contentBefore.Height - 500 && p.Y <= contentBefore.Height - 400, "the hidden box is not drawn");

        var links = (await ReadAsync(Samples.LinksAnnots, 0)).Count(a => a.Kind == FileAnnotationKind.Link);
        links.ShouldBeGreaterThan(0);
        var flattenedLinks = await ApplyAsync(Samples.LinksAnnots, new FlattenAnnotationsParams());
        (await ReadAsync(flattenedLinks, 0)).Count(a => a.Kind == FileAnnotationKind.Link).ShouldBe(links);
    }

    [Fact]
    public void Count_IsWhatWouldBeFlattened()
    {
        var input = Annotated(page => Find(page, "h1").Elements.Remove("/AP"));

        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        FlattenAnnotationsOperation.Count(document.Pages[0]).ShouldBe(2, "the note and the box; not the answer, nor the highlight without an appearance");
        FlattenAnnotationsOperation.Count(document.Pages[1]).ShouldBe(0);
        FlattenAnnotationsOperation.Count(document.Pages[0], [AnnotationGroup.Drawings]).ShouldBe(1, "the box");
    }

    /// <summary>basic-vi.pdf with a highlight (h1), a note (n1) answered once and a rectangle (box) on page 1.</summary>
    private string Annotated(Action<PdfPage> change, bool onBothPages = false)
    {
        var path = _temp.Combine($"annotated-{Guid.NewGuid():N}.pdf");
        using var document = PdfReader.Open(Samples.BasicVi, PdfDocumentOpenMode.Modify);
        foreach (var page in onBothPages ? document.Pages.Cast<PdfPage>().Take(2) : [document.Pages[0]])
        {
            AnnotationWriter.Write(document, page,
            [
                new MarkupAnnotation("h1", Yellow, 1, "Lan", Created, Created, "Số liệu sai", MarkupKind.Highlight, [TextQuad.Of(new PdfRect(72, 700, 300, 712))]),
                new NoteAnnotation("n1", Yellow, 1, "Lan", Created, Created, "Ghi chú", new PdfPoint(400, 700))
                {
                    Replies = [new AnnotationReply("n1-r", "Minh", Created, "Trả lời")],
                },
                new ShapeAnnotation("box", Red, 1, "Lan", Created, Created, string.Empty, NoteShape.Square, new PdfRect(100, 400, 250, 500), 3, null),
            ]);
        }

        change(document.Pages[0]);
        document.Save(path);
        return path;
    }

    private static PdfDictionary Find(PdfPage page, string name)
        => Annotations(page)!.Elements.Select(AsDictionary).OfType<PdfDictionary>().Single(a => a.Elements.GetString("/NM") == name);

    private async Task<IReadOnlyList<FileAnnotation>> ReadAsync(string path, int page)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        return await document.GetPageAnnotationsAsync(page, cancellationToken: Token);
    }

    private async Task<string> ApplyAsync(string input, FlattenAnnotationsParams parameters)
    {
        var output = _temp.Combine($"flattened-{Guid.NewGuid():N}.pdf");
        var stream = File.Create(output);
        await using (stream)
        {
            await _operation.ApplyToFileAsync(input, stream, parameters, new RecordingProgress(), Token);
        }

        return output;
    }

    private static async Task<PageBitmap> RenderAsync(PdfDocumentHandle document, int page, PdfRenderFlags flags)
        => await document.RenderPageAsync(page, new PageRenderOptions { Scale = 1, Flags = flags }, cancellationToken: Token);

    private static List<(int X, int Y)> Changes(PageBitmap before, PageBitmap after)
    {
        var changes = new List<(int, int)>();
        for (var y = 0; y < Math.Min(before.Height, after.Height); y++)
        {
            for (var x = 0; x < Math.Min(before.Width, after.Width); x++)
            {
                var (b1, g1, r1) = before.GetPixel(x, y);
                var (b2, g2, r2) = after.GetPixel(x, y);
                if (Math.Abs(r1 - r2) + Math.Abs(g1 - g2) + Math.Abs(b1 - b2) > 24)
                {
                    changes.Add((x, y));
                }
            }
        }

        return changes;
    }
}
