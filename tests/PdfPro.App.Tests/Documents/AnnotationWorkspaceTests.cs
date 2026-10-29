using System.Globalization;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Overlay;
using PdfPro.Core.Sessions;
using PdfPro.Operations.Annotating;

namespace PdfPro.App.Tests.Documents;

/// <summary>Annotations in a document session: instant page edits, each one undoable step that makes the document unsaved.</summary>
public sealed class AnnotationWorkspaceTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(7));

    private readonly WorkspaceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task AddingEditingAndDeleting_AreUndoableSteps_ThatChangeTheDocument()
    {
        await using var workspace = await _fixture.CreateAsync(FakePdfDocument.WithPages(3));
        var operations = TestOperations.Catalog();

        workspace.Apply(operations.Get<AddAnnotationsOperation>(), AddAnnotationsParams.On(2, [Note("a"), Note("b")]));
        workspace.IsDirty.ShouldBeTrue();
        workspace.Session.History.NextUndoDescription.ShouldBe(Format(Strings.History_AddAnnotations, 2));
        workspace.Apply(operations.Get<UpdateAnnotationsOperation>(), new UpdateAnnotationsParams(2, [Note("a") with { Contents = "Đã sửa" }]));
        workspace.Session.History.NextUndoDescription.ShouldBe(Format(Strings.History_UpdateAnnotations, 1));
        workspace.Apply(operations.Get<DeleteAnnotationsOperation>(), new DeleteAnnotationsParams(2, ["b"]));
        workspace.Session.History.NextUndoDescription.ShouldBe(Format(Strings.History_DeleteAnnotations, 1));

        var page = workspace.Session.Plan[1];
        page.Annotations.ShouldHaveSingleItem().Contents.ShouldBe("Đã sửa");

        workspace.Undo(3);
        workspace.Session.Plan[1].Annotations.ShouldBeEmpty();
        workspace.IsDirty.ShouldBeFalse();
        workspace.Redo(3);
        workspace.Session.Plan[1].ShouldBeSameAs(page);
    }

    [Fact]
    public void HistoryDescriptions_CountTheAnnotations()
    {
        var plan = PagePlan.Identity("s0", 2, RandomPageKeys.Instance);

        Describe(AddAnnotationsParams.On(1, [Note("a"), Note("b"), Note("c")])).ShouldBe(Format(Strings.History_AddAnnotations, 3));
        Describe(new UpdateAnnotationsParams(1, [Note("a")])).ShouldBe(Format(Strings.History_UpdateAnnotations, 1));
        Describe(new DeleteAnnotationsParams(1, ["a", "b"])).ShouldBe(Format(Strings.History_DeleteAnnotations, 2));

        string Describe(object parameters) => HistoryDescriptions.Describe(new HistoryRecord("annotate.x", parameters, []), plan, _ => null);
    }

    private static NoteAnnotation Note(string id) => new(id, new RgbColor(255, 230, 0), 1, "Lan", Created, Created, "Ghi chú", new PdfPoint(100, 700));

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
