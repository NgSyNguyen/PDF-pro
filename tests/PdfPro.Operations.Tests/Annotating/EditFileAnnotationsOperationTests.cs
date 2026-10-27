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

    /// <summary>basic-vi.pdf with a highlight (h1) and a note (n1) answered once.</summary>
    private string Annotated()
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
