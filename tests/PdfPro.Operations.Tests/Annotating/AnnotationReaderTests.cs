using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Annotating;

/// <summary>The annotations already in a file, read through PDFium for the comments list.</summary>
public sealed class AnnotationReaderTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(7));
    private static readonly RgbColor Yellow = new(255, 230, 0);

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task WrittenAnnotations_AreReadBack_WithTheirConversation()
    {
        var highlight = new MarkupAnnotation("h1", Yellow, 1, "Nguyễn Lan", Created, Created.AddMinutes(5), "Số liệu sai", MarkupKind.Highlight,
            [TextQuad.Of(new PdfRect(72, 700, 300, 712)), TextQuad.Of(new PdfRect(72, 686, 150, 698))])
        {
            Replies = [new AnnotationReply("h1-r1", "Minh", Created.AddHours(1), "Đã sửa theo báo cáo mới")],
            Reviews = [new AnnotationReview("h1-s1", "Minh", Created.AddHours(2), ReviewState.Accepted)],
        };
        var square = new ShapeAnnotation("s1", RgbColor.Red, 1, "Lan", Created, Created, string.Empty, NoteShape.Square, new PdfRect(100, 300, 200, 360), 2, null);
        var path = Write(highlight, square);

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        var annotations = await document.GetPageAnnotationsAsync(0, cancellationToken: Token);

        annotations.Select(a => a.Kind).ShouldBe([FileAnnotationKind.Highlight, FileAnnotationKind.Text, FileAnnotationKind.Text, FileAnnotationKind.Square]);
        annotations.Select(a => a.Index).ShouldBe([0, 1, 2, 3]);
        var read = annotations[0];
        read.Name.ShouldBe("h1");
        read.Author.ShouldBe("Nguyễn Lan");
        TextNormalizer.ToNfc(read.Contents!).ShouldBe("Số liệu sai");
        read.Modified.ShouldBe(Created.AddMinutes(5));
        read.IsComment.ShouldBeTrue();
        read.IsReply.ShouldBeFalse();
        read.Quads.Count.ShouldBe(2);
        read.Quads[0].UpperLeft.X.ShouldBe(72, 0.01);
        read.Quads[0].UpperLeft.Y.ShouldBe(712, 0.01);
        read.Quads[0].LowerRight.X.ShouldBe(300, 0.01);
        read.Quads[0].LowerRight.Y.ShouldBe(700, 0.01);
        read.Rect.Left.ShouldBeLessThanOrEqualTo(72);

        var reply = annotations[1];
        reply.InReplyTo.ShouldBe(0);
        reply.IsReply.ShouldBeTrue();
        reply.Author.ShouldBe("Minh");
        TextNormalizer.ToNfc(reply.Contents!).ShouldBe("Đã sửa theo báo cáo mới");
        var review = annotations[2];
        review.InReplyTo.ShouldBe(0);
        review.State.ShouldBe("Accepted");
        review.StateModel.ShouldBe("Review");

        annotations[3].Rect.ShouldSatisfyAllConditions(
            r => r.Left.ShouldBe(100, 0.01),
            r => r.Bottom.ShouldBe(300, 0.01),
            r => r.Right.ShouldBe(200, 0.01),
            r => r.Top.ShouldBe(360, 0.01));
        annotations[3].Contents.ShouldBeNull("an empty comment reads as none");
        annotations[3].Quads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Links_AreNotComments()
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.LinksAnnots);

        var annotations = await document.GetPageAnnotationsAsync(0, cancellationToken: Token);

        annotations.ShouldNotBeEmpty();
        annotations.Where(a => a.Kind == FileAnnotationKind.Link).ShouldNotBeEmpty();
        annotations.Where(a => a.Kind == FileAnnotationKind.Link).ShouldAllBe(a => !a.IsComment);
    }

    [Fact]
    public async Task APageWithoutAnnotations_HasNone()
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);

        (await document.GetPageAnnotationsAsync(0, cancellationToken: Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadingAgain_GivesTheSameAnswer()
    {
        var path = Write(new NoteAnnotation("n", Yellow, 1, "Lan", Created, Created, "Ghi chú", new PdfPoint(100, 700))
        {
            Replies = [new AnnotationReply("r", "Minh", Created, "Trả lời")],
        });

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        var first = await document.GetPageAnnotationsAsync(0, cancellationToken: Token);
        var second = await document.GetPageAnnotationsAsync(0, cancellationToken: Token);

        second.Select(a => (a.Kind, a.Name, a.InReplyTo)).ShouldBe(first.Select(a => (a.Kind, a.Name, a.InReplyTo)));
        second.Select(a => a.InReplyTo).ShouldBe([null, 0]);
    }

    private string Write(params AnnotationItem[] items)
    {
        var output = _temp.Combine($"annotated-{Guid.NewGuid():N}.pdf");
        using var document = PdfReader.Open(Samples.BasicVi, PdfDocumentOpenMode.Modify);
        AnnotationWriter.Write(document, document.Pages[0], items);
        document.Save(output);
        return output;
    }
}
