using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Tests.Annotating;

/// <summary>annotate.editFile: answering, reviewing, rewriting and removing comments already in the file.</summary>
public sealed class EditFileAnnotationsOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(7));
    private static readonly RgbColor Yellow = new(255, 230, 0);

    private readonly TempDirectory _temp = new();
    private readonly EditFileAnnotationsOperation _operation = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operation_IsAContentChange()
    {
        _operation.Id.ShouldBe("annotate.editFile");
        _operation.Kind.ShouldBe(OperationKind.Content);
    }

    [Fact]
    public async Task Answers_ReviewStates_AndNewComments_AreWrittenIntoTheFile()
    {
        var input = Annotated();
        var read = await ReadAsync(input);
        var highlight = read.Single(a => a.Name == "h1");
        var note = read.Single(a => a.Name == "n1");

        var output = await ApplyAsync(
            input,
            Edit(highlight, FileAnnotationAction.Reply, text: "Đồng ý với nhận xét", id: "r-new"),
            Edit(highlight, FileAnnotationAction.SetState, state: ReviewState.Accepted, id: "s-new"),
            Edit(note, FileAnnotationAction.SetContents, text: "Nội dung mới"));

        var after = await ReadAsync(output);
        var thread = after.Where(a => a.InReplyTo == after.Single(x => x.Name == "h1").Index).ToArray();
        thread.Select(a => a.Name).ShouldBe(["r-new", "s-new"]);
        TextNormalizer.ToNfc(thread[0].Contents!).ShouldBe("Đồng ý với nhận xét");
        thread[0].Author.ShouldBe("Minh");
        thread[0].Modified.ShouldBe(Created.AddDays(1));
        thread[1].State.ShouldBe("Accepted");
        thread[1].StateModel.ShouldBe("Review");
        TextNormalizer.ToNfc(after.Single(a => a.Name == "n1").Contents!).ShouldBe("Nội dung mới");
        after.Single(a => a.Name == "n1").Modified.ShouldBe(Created.AddDays(1));
    }

    [Fact]
    public async Task Removing_TakesTheConversationAlong_AndLeavesTheRest()
    {
        var input = Annotated();
        var read = await ReadAsync(input);

        var output = await ApplyAsync(input, Edit(read.Single(a => a.Name == "n1"), FileAnnotationAction.Delete));

        var after = await ReadAsync(output);
        after.Select(a => a.Name).ShouldBe(["h1"], "the note's answer went with it");
    }

    [Fact]
    public async Task AnAnnotationThatMoved_IsFoundByItsMarks_AndOneThatIsGone_IsNotReplacedByAnother()
    {
        var input = Annotated();
        var note = (await ReadAsync(input)).Single(a => a.Name == "n1");

        var moved = await ApplyAsync(input, Edit(note, FileAnnotationAction.SetContents, text: "Đúng ghi chú") with { Index = 0 });
        TextNormalizer.ToNfc((await ReadAsync(moved)).Single(a => a.Name == "n1").Contents!).ShouldBe("Đúng ghi chú", "index 0 is the highlight: the name decides");

        var gone = Edit(note, FileAnnotationAction.Delete) with { Name = "không còn" };
        await Should.ThrowAsync<InvalidDataException>(() => ApplyAsync(input, gone));
    }

    [Fact]
    public async Task AnAnnotationWithoutAName_IsRecognisedByItsPlace()
    {
        var links = (await ReadAsync(Samples.LinksAnnots)).Where(a => a.Kind == FileAnnotationKind.Link).ToArray();
        links.ShouldNotBeEmpty();
        var first = links[0];

        var output = await ApplyAsync(Samples.LinksAnnots, Edit(first, FileAnnotationAction.Delete) with { Name = null });

        (await ReadAsync(output)).Count(a => a.Kind == FileAnnotationKind.Link).ShouldBe(links.Length - 1);
    }

    [Fact]
    public async Task Moving_ShiftsThePlace_TheStrokes_AndThePopUp_AndTheAppearanceFollows()
    {
        var input = Annotated(withInk: true);
        var read = await ReadAsync(input);
        var ink = read.Single(a => a.Name == "i1");
        var note = read.Single(a => a.Name == "n1");

        var output = await ApplyAsync(
            input,
            Edit(ink, FileAnnotationAction.Move) with { Dx = 50, Dy = -100 },
            Edit(note, FileAnnotationAction.Move) with { Dx = -20, Dy = 10 });

        var after = await ReadAsync(output);
        var movedInk = after.Single(a => a.Name == "i1");
        movedInk.Rect.Left.ShouldBe(ink.Rect.Left + 50, 0.01);
        movedInk.Rect.Bottom.ShouldBe(ink.Rect.Bottom - 100, 0.01);
        movedInk.Modified.ShouldBe(Created.AddDays(1));
        after.Single(a => a.Name == "n1").Rect.Top.ShouldBe(note.Rect.Top + 10, 0.01);
        after.Single(a => a.Name == "h1").Rect.ShouldBe(read.Single(a => a.Name == "h1").Rect, "only the chosen ones move");

        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var annotations = PdfPro.Operations.Materializing.PdfGraph.Annotations(document.Pages[0])!.Elements
            .Select(PdfPro.Operations.Materializing.PdfGraph.AsDictionary).OfType<PdfSharp.Pdf.PdfDictionary>().ToList();
        var stroke = PdfPro.Operations.Materializing.PdfGraph.AsArray(PdfPro.Operations.Materializing.PdfGraph.AsArray(
            annotations.Single(a => a.Elements.GetString("/NM") == "i1").Elements["/InkList"])!.Elements[0])!;
        Value(stroke.Elements[0]).ShouldBe(150, 0.01, "the first point (100, 300) moved by (50, -100)");
        Value(stroke.Elements[1]).ShouldBe(200, 0.01);

        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, input);
        await using var moved = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var original = await before.RenderPageAsync(0, new PageRenderOptions { Scale = 1 }, cancellationToken: Token);
        using var shifted = await moved.RenderPageAsync(0, new PageRenderOptions { Scale = 1 }, cancellationToken: Token);
        var height = original.Height;
        IsRed(original.GetPixel(150, height - 325)).ShouldBeTrue("the stroke before");
        IsRed(shifted.GetPixel(150, height - 325)).ShouldBeFalse("gone from there");
        IsRed(shifted.GetPixel(200, height - 225)).ShouldBeTrue("drawn where it went");
    }

    [Fact]
    public void AMove_MustBeAFiniteDistance()
    {
        var edit = new FileAnnotationEdit(1, 0, FileAnnotationKind.Square, "s", new PdfRect(0, 0, 20, 20), FileAnnotationAction.Move, Author: "Minh", When: Created, Dx: 10);
        var document = new Core.Documents.DocumentInfo(1);

        _operation.Validate(new EditFileAnnotationsParams([edit]), document).IsValid.ShouldBeTrue();
        _operation.Validate(new EditFileAnnotationsParams([edit with { Dx = 0 }]), document).IsValid.ShouldBeFalse("not a move");
        _operation.Validate(new EditFileAnnotationsParams([edit with { Dy = double.NaN }]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new EditFileAnnotationsParams([edit with { Dx = 20000 }]), document).IsValid.ShouldBeFalse();
    }

    private static double Value(PdfSharp.Pdf.PdfItem item) => item switch
    {
        PdfSharp.Pdf.PdfReal real => real.Value,
        PdfSharp.Pdf.PdfInteger integer => integer.Value,
        _ => double.NaN,
    };

    private static bool IsRed((byte B, byte G, byte R) pixel) => pixel.R > 150 && pixel.G < 120 && pixel.B < 120;

    [Fact]
    public void BadChanges_AreRefused()
    {
        var edit = new FileAnnotationEdit(1, 0, FileAnnotationKind.Text, "n", new PdfRect(0, 0, 20, 20), FileAnnotationAction.Reply, "ok", Author: "Minh", When: Created, Id: "r");
        var document = new Core.Documents.DocumentInfo(2);

        _operation.Validate(new EditFileAnnotationsParams([edit]), document).IsValid.ShouldBeTrue();
        _operation.Validate(new EditFileAnnotationsParams([]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new EditFileAnnotationsParams([edit with { Page = 3 }]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new EditFileAnnotationsParams([edit with { Text = "  " }]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new EditFileAnnotationsParams([edit with { Id = null }]), document).IsValid.ShouldBeFalse();
        _operation.Validate(new EditFileAnnotationsParams([edit with { Action = FileAnnotationAction.SetContents, Text = string.Empty }]), document).IsValid.ShouldBeTrue("a comment can be emptied");
    }

    private static FileAnnotationEdit Edit(FileAnnotation annotation, FileAnnotationAction action, string? text = null, ReviewState state = ReviewState.None, string? id = null)
        => new(1, annotation.Index, annotation.Kind, annotation.Name, annotation.Rect, action, text, state, "Minh", Created.AddDays(1), id);

    /// <summary>basic-vi.pdf with a highlight (h1), a note (n1) answered once, and a red stroke (i1) when asked.</summary>
    private string Annotated(bool withInk = false)
    {
        var path = _temp.Combine($"annotated-{Guid.NewGuid():N}.pdf");
        using var document = PdfReader.Open(Samples.BasicVi, PdfDocumentOpenMode.Modify);
        AnnotationWriter.Write(document, document.Pages[0],
        [
            new MarkupAnnotation("h1", Yellow, 1, "Lan", Created, Created, "Số liệu sai", MarkupKind.Highlight, [TextQuad.Of(new PdfRect(72, 700, 300, 712))]),
            new NoteAnnotation("n1", Yellow, 1, "Lan", Created, Created, "Ghi chú cũ", new PdfPoint(400, 700))
            {
                Replies = [new AnnotationReply("n1-r", "Minh", Created, "Trả lời cũ")],
            },
            .. withInk
                ? [new InkAnnotation("i1", new RgbColor(220, 30, 30), 1, "Lan", Created, Created, string.Empty, 6, [[new PdfPoint(100, 300), new PdfPoint(200, 350)]])]
                : Array.Empty<AnnotationItem>(),
        ]);
        document.Save(path);
        return path;
    }

    private async Task<IReadOnlyList<FileAnnotation>> ReadAsync(string path)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        return await document.GetPageAnnotationsAsync(0, cancellationToken: Token);
    }

    private async Task<string> ApplyAsync(string input, params FileAnnotationEdit[] edits)
    {
        var output = _temp.Combine($"edited-{Guid.NewGuid():N}.pdf");
        var stream = File.Create(output);
        await using (stream)
        {
            await _operation.ApplyToFileAsync(input, stream, new EditFileAnnotationsParams(edits), new RecordingProgress(), Token);
        }

        return output;
    }
}
