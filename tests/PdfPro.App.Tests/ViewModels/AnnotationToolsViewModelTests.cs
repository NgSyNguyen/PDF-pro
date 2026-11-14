using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Annotating;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Annotating;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Chú thích" in the viewer: marking text, sticky notes and their comments, selecting, moving and deleting.</summary>
public sealed class AnnotationToolsViewModelTests : IDisposable
{
    private const double Left = 72;
    private const double CharWidth = 6;
    private const double Top = 760;
    private const double LineHeight = 14;

    private static readonly string[][] Lines =
    [
        ["Ủy ban nhân dân tỉnh", "ban hành quyết định."],
        [], // a scanned page: no text
        ["Điều 1. Phạm vi"],
    ];

    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly DocumentTabFactory _factory;

    public AnnotationToolsViewModelTests()
        => _factory = new DocumentTabFactory(
            new FakePageImageRenderer { AutoComplete = true },
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            new ManualDebouncerFactory(),
            TestOperations.Catalog(),
            Substitute.For<IMessageService>(),
            Substitute.For<IPageDialogService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IPdfDocumentLoader>(),
            NullLoggerFactory.Instance,
            settings: new FakeSettingsService(AppSettings.Default with { AnnotationAuthor = "Nguyễn Lan" }));

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task MarkingSelectedText_AddsAHighlightOverItsLines_InOneStep()
    {
        await using var tab = await OpenAsync();
        tab.TextSelection.Select(0, new TextRange(3, 6));

        await tab.HighlightCommand.ExecuteAsync(null);

        var highlight = tab.Workspace.Session.Plan[0].Annotations.ShouldHaveSingleItem().ShouldBeOfType<MarkupAnnotation>();
        highlight.Kind.ShouldBe(MarkupKind.Highlight);
        highlight.Quads.ShouldBe([TextQuad.Of(new PdfRect(Left + (3 * CharWidth), Top, Left + (6 * CharWidth), Top + LineHeight - 2))]);
        highlight.MarkedText.ShouldBe("ban");
        highlight.Author.ShouldBe("Nguyễn Lan");
        highlight.Color.ShouldBe(AnnotationToolsViewModel.HighlightColor);
        tab.TextSelection.HasSelection.ShouldBeFalse("the marked text is let go");
        tab.Annotations.Tool.ShouldBe(AnnotationTool.None, "with text selected the tool does not stay on");
        tab.Workspace.Session.History.NextUndoDescription.ShouldBe(Format(Strings.History_AddAnnotations, 1));
        tab.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task TextSelectedAcrossPages_IsMarkedOnEachPageWithText_InOneStep()
    {
        await using var tab = await OpenAsync();
        tab.TextSelection.SelectAll();

        await tab.UnderlineCommand.ExecuteAsync(null);

        var plan = tab.Workspace.Session.Plan;
        plan[0].Annotations.ShouldHaveSingleItem().ShouldBeOfType<MarkupAnnotation>().Quads.Count.ShouldBe(2, "one per line");
        plan[1].Annotations.ShouldBeEmpty("a page without text has nothing to mark");
        plan[2].Annotations.ShouldHaveSingleItem().ShouldBeOfType<MarkupAnnotation>().Kind.ShouldBe(MarkupKind.Underline);
        tab.Workspace.Session.History.NextUndoDescription.ShouldBe(Format(Strings.History_AddAnnotations, 2));

        tab.UndoCommand.Execute(null);
        tab.Workspace.Session.Plan.HasAnnotations.ShouldBeFalse();
    }

    [Fact]
    public async Task WithNothingSelected_TheToolStaysOn_AndMarksEachSelection()
    {
        await using var tab = await OpenAsync();
        var annotations = tab.Annotations;

        await tab.StrikeOutCommand.ExecuteAsync(null);
        annotations.Tool.ShouldBe(AnnotationTool.StrikeOut);
        annotations.IsToolActive.ShouldBeTrue();
        annotations.ToolText.ShouldBe(Format(Strings.Annotation_ToolActive, Strings.Cmd_Strikeout));

        await SelectAsync(tab, line: 0, from: 0, to: 1);
        await annotations.SelectionReleasedAsync();
        await SelectAsync(tab, line: 1, from: 4, to: 7);
        await annotations.SelectionReleasedAsync();

        tab.Workspace.Session.Plan[0].Annotations.Select(a => ((MarkupAnnotation)a).MarkedText).ShouldBe(["Ủy", "hành"]);
        annotations.Tool.ShouldBe(AnnotationTool.StrikeOut);

        await tab.StrikeOutCommand.ExecuteAsync(null);
        annotations.Tool.ShouldBe(AnnotationTool.None, "choosing the tool again turns it off");
    }

    [Fact]
    public async Task ANote_IsPlacedWhereClicked_AndItsCommentIsWritten()
    {
        await using var tab = await OpenAsync();
        var annotations = tab.Annotations;
        await tab.StickyNoteCommand.ExecuteAsync(null);

        annotations.Press(Viewport(tab, 0, new PdfPoint(300, 500)), clickCount: 1).ShouldBeTrue();

        var note = tab.Workspace.Session.Plan[0].Annotations.ShouldHaveSingleItem().ShouldBeOfType<NoteAnnotation>();
        note.At.X.ShouldBe(300, tolerance: 0.01);
        note.At.Y.ShouldBe(500, tolerance: 0.01);
        note.Author.ShouldBe("Nguyễn Lan");
        annotations.Tool.ShouldBe(AnnotationTool.None, "one note at a time");
        annotations.Selected.ShouldBe(new AnnotationTarget(Key(tab, 0), note.Id));
        annotations.IsEditingContents.ShouldBeFalse("the comment opens once the mouse is released");

        annotations.Release();
        annotations.IsEditingContents.ShouldBeTrue();
        annotations.ContentsHeader.ShouldNotBeNull().ShouldStartWith("Nguyễn Lan · ");
        annotations.ContentsDraft = "Kiểm tra lại số liệu\r\n";
        annotations.CommitContentsCommand.Execute(null);

        annotations.IsEditingContents.ShouldBeFalse();
        tab.Workspace.Session.Plan[0].Annotations.Single().Contents.ShouldBe("Kiểm tra lại số liệu");
        tab.Workspace.Session.History.NextUndoDescription.ShouldBe(Format(Strings.History_UpdateAnnotations, 1));

        annotations.BeginEditContents(annotations.Selected!);
        annotations.ContentsDraft = "bỏ đi";
        annotations.CancelContentsCommand.Execute(null);
        tab.Workspace.Session.Plan[0].Annotations.Single().Contents.ShouldBe("Kiểm tra lại số liệu", "cancelling keeps the comment as it was");
    }

    [Fact]
    public async Task ClickingAnAnnotation_SelectsIt_DraggingMovesIt_DeleteRemovesIt()
    {
        await using var tab = await OpenAsync();
        var annotations = tab.Annotations;
        AddNote(tab, "n", new PdfPoint(300, 500));
        var scale = tab.Viewer.Layout.Scale;
        var grab = Viewport(tab, 0, new PdfPoint(310, 490)); // inside the note's icon

        annotations.Press(grab, clickCount: 1).ShouldBeTrue();
        annotations.Selected.ShouldBe(new AnnotationTarget(Key(tab, 0), "n"));
        annotations.IsMoving.ShouldBeTrue();
        annotations.DragTo(new ViewPoint(grab.X + 30, grab.Y + 15));
        annotations.Shown(Key(tab, 0), tab.Workspace.Session.Plan[0].Annotations[0]).ShouldBeOfType<NoteAnnotation>().At.X.ShouldBe(300 + (30 / scale), tolerance: 0.01);
        tab.Workspace.Session.Plan[0].Annotations[0].ShouldBeOfType<NoteAnnotation>().At.X.ShouldBe(300, "nothing changes before the mouse is released");
        annotations.Release();

        var moved = tab.Workspace.Session.Plan[0].Annotations.Single().ShouldBeOfType<NoteAnnotation>();
        moved.At.X.ShouldBe(300 + (30 / scale), tolerance: 0.01);
        moved.At.Y.ShouldBe(500 - (15 / scale), tolerance: 0.01, "screen y goes down, PDF y up");
        tab.Workspace.Session.History.NextUndoDescription.ShouldBe(Format(Strings.History_UpdateAnnotations, 1));

        annotations.DeleteCommand.Execute(null);
        tab.Workspace.Session.Plan[0].Annotations.ShouldBeEmpty();
        annotations.Selected.ShouldBeNull();
        annotations.CanDelete.ShouldBeFalse();
    }

    [Fact]
    public async Task AClickWithoutMoving_ChangesNothing_AndAClickElsewhere_LetsGo()
    {
        await using var tab = await OpenAsync();
        var annotations = tab.Annotations;
        AddNote(tab, "n", new PdfPoint(300, 500));
        var steps = tab.Workspace.Session.History.UndoCount;

        annotations.Press(Viewport(tab, 0, new PdfPoint(310, 490)), clickCount: 1).ShouldBeTrue();
        annotations.Release();
        tab.Workspace.Session.History.UndoCount.ShouldBe(steps);

        annotations.Press(Viewport(tab, 0, new PdfPoint(100, 300)), clickCount: 1).ShouldBeFalse("text selection takes presses elsewhere");
        annotations.Selected.ShouldBeNull();
    }

    [Fact]
    public async Task MarkedText_IsSelectedByAClick_ButStaysOnItsText()
    {
        await using var tab = await OpenAsync();
        tab.TextSelection.Select(0, new TextRange(3, 6));
        await tab.HighlightCommand.ExecuteAsync(null);

        tab.Annotations.Press(At(tab, 0, line: 0, column: 4), clickCount: 1).ShouldBeTrue();

        tab.Annotations.Selected.ShouldNotBeNull();
        tab.Annotations.IsMoving.ShouldBeFalse();
    }

    [Fact]
    public async Task DoubleClickingANote_OpensItsComment()
    {
        await using var tab = await OpenAsync();
        AddNote(tab, "n", new PdfPoint(300, 500), "Ghi chú cũ");

        tab.Annotations.Press(Viewport(tab, 0, new PdfPoint(310, 490)), clickCount: 2).ShouldBeTrue();
        tab.Annotations.Release();

        tab.Annotations.IsEditingContents.ShouldBeTrue();
        tab.Annotations.ContentsDraft.ShouldBe("Ghi chú cũ");
    }

    [Fact]
    public async Task Escape_ClosesTheComment_ThenTheTool_ThenTheSelection()
    {
        await using var tab = await OpenAsync();
        var annotations = tab.Annotations;
        AddNote(tab, "n", new PdfPoint(300, 500));
        annotations.Press(Viewport(tab, 0, new PdfPoint(310, 490)), clickCount: 2);
        annotations.Release();
        await annotations.UseToolAsync(AnnotationTool.Note);

        annotations.Escape().ShouldBeTrue();
        annotations.IsEditingContents.ShouldBeFalse();
        annotations.Escape().ShouldBeTrue();
        annotations.Tool.ShouldBe(AnnotationTool.None);
        annotations.Escape().ShouldBeTrue();
        annotations.Selected.ShouldBeNull();
        annotations.Escape().ShouldBeFalse("nothing left: Esc goes to the text selection");
    }

    [Fact]
    public async Task UndoingAnAnnotation_LetsGoOfIt()
    {
        await using var tab = await OpenAsync();
        AddNote(tab, "n", new PdfPoint(300, 500));
        tab.Annotations.Press(Viewport(tab, 0, new PdfPoint(310, 490)), clickCount: 1);
        tab.Annotations.Release();

        tab.UndoCommand.Execute(null);

        tab.Annotations.Selected.ShouldBeNull();
        tab.Annotations.AnnotationsOn(Key(tab, 0)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Annotating_KeepsTheSearchAndTheTextSelection()
    {
        await using var tab = await OpenAsync();
        tab.Search.Query = "ban";
        await tab.Search.SearchCommand.ExecuteAsync(null);
        var first = tab.Search.Results[0];
        tab.TextSelection.Select(2, new TextRange(0, 4));

        AddNote(tab, "n", new PdfPoint(300, 500));

        tab.Search.Results[0].ShouldBeSameAs(first, "annotations do not change the text: no new search");
        tab.Search.SelectedResult.ShouldBeSameAs(first);
        tab.TextSelection.HasSelection.ShouldBeTrue();
        tab.Annotations.AnnotationsOn(Key(tab, 0)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TheCommands_NeedTheViewer()
    {
        await using var tab = await OpenAsync();

        tab.HighlightCommand.CanExecute(null).ShouldBeTrue();
        tab.IsOrganizing = true;
        tab.HighlightCommand.CanExecute(null).ShouldBeFalse();
        tab.StickyNoteCommand.CanExecute(null).ShouldBeFalse();
        tab.IsOrganizing = false;
        tab.Editor.IsActive = true;
        tab.UnderlineCommand.CanExecute(null).ShouldBeFalse("the page editor takes the mouse");
    }

    [Fact]
    public async Task AnnotationsOfTheFile_AreSelected_Moved_AndDeleted_AsTheNextRevision()
    {
        await using var tab = await OpenAsync(page => page == 0
            ?
            [
                new FileAnnotation(0, FileAnnotationKind.Stamp, new PdfRect(300, 400, 400, 450), "s", "Minh", null, null, null, null, null, 4, []),
                new FileAnnotation(1, FileAnnotationKind.Highlight, new PdfRect(Left, Top, Left + 120, Top + 12), "h", "Minh", null, null, null, null, null, 4, [TextQuad.Of(new PdfRect(Left, Top, Left + 120, Top + 12))]),
            ]
            : []);
        var requests = new List<ContentChangeRequest>();
        tab.ContentChangeRequested += (_, r) => requests.Add(r);
        var annotations = tab.Annotations;
        var page = Key(tab, 0);
        annotations.FileAnnotationsOn(page).ShouldBeEmpty("read in the background");
        await WaitUntilAsync(() => annotations.FileAnnotationsOn(page).Count == 2);
        var scale = tab.Viewer.Layout.Scale;

        var grab = Viewport(tab, 0, new PdfPoint(350, 425));
        annotations.Press(grab, clickCount: 1).ShouldBeTrue();
        annotations.SelectedFile.ShouldNotBeNull().Annotation.Name.ShouldBe("s");
        annotations.Selected.ShouldBeNull();
        annotations.IsMoving.ShouldBeTrue();
        annotations.IsBarVisible.ShouldBeTrue();
        annotations.ToolText.ShouldBe(Strings.Annotation_FileSelected);
        annotations.DragTo(new ViewPoint(grab.X + 30, grab.Y + 15));
        annotations.FileSelectionOn(page).ShouldNotBeNull().Left.ShouldBe(300 + (30 / scale), 0.01, "the frame follows the mouse");
        annotations.Release();

        var move = ((EditFileAnnotationsParams)requests.ShouldHaveSingleItem().Parameters).Edits.ShouldHaveSingleItem();
        move.Action.ShouldBe(FileAnnotationAction.Move);
        (move.Page, move.Index, move.Name, move.Kind).ShouldBe((1, 0, "s", FileAnnotationKind.Stamp));
        move.Dx.ShouldBe(30 / scale, 0.01);
        move.Dy.ShouldBe(-15 / scale, 0.01, "down on the screen is down on the page");
        requests[0].Description.ShouldBe(Strings.History_MoveFileComment);
        tab.Workspace.Session.Plan[0].Annotations.ShouldBeEmpty("the file changes, not the plan");

        var onText = Viewport(tab, 0, new PdfPoint(Left + 30, Top + 6));
        annotations.Press(onText, clickCount: 1).ShouldBeTrue();
        annotations.SelectedFile!.Annotation.Name.ShouldBe("h");
        annotations.IsMoving.ShouldBeFalse("marked text stays on its text");
        annotations.ToolText.ShouldBe(Strings.Annotation_FileSelectedFixed);
        annotations.Release();
        annotations.DeleteCommand.CanExecute(null).ShouldBeTrue();
        annotations.DeleteCommand.Execute(null);

        requests.Count.ShouldBe(2);
        ((EditFileAnnotationsParams)requests[1].Parameters).Edits.ShouldHaveSingleItem().Action.ShouldBe(FileAnnotationAction.Delete);
        annotations.SelectedFile.ShouldBeNull();

        annotations.Press(grab, clickCount: 1).ShouldBeTrue();
        annotations.Release();
        annotations.Escape().ShouldBeTrue();
        annotations.SelectedFile.ShouldBeNull();
        annotations.Press(Viewport(tab, 0, new PdfPoint(500, 200)), clickCount: 1).ShouldBeFalse("nothing there: text selection takes the press");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private async Task<DocumentTabViewModel> OpenAsync(Func<int, IReadOnlyList<FileAnnotation>>? fileAnnotations = null)
    {
        var document = FakePdfDocument.WithPages(Lines.Length);
        document.TextLayers = page => Layer(Lines[page]);
        if (fileAnnotations is not null)
        {
            document.FileAnnotations = fileAnnotations;
        }

        var tab = _factory.Create(await _fixture.CreateAsync(document));
        tab.Viewer.ViewportSize = new ViewSize(1000, 700);
        foreach (var page in tab.Workspace.Pages.Pages)
        {
            await tab.Editor.GetGeometryAsync(page.Key);
        }

        return tab;
    }

    private static void AddNote(DocumentTabViewModel tab, string id, PdfPoint at, string contents = "")
    {
        var note = new NoteAnnotation(id, AnnotationToolsViewModel.NoteColor, 1, "Minh", DateTimeOffset.Now, DateTimeOffset.Now, contents, at);
        tab.Apply(TestOperations.Catalog().Get<AddAnnotationsOperation>(), AddAnnotationsParams.On(1, [note])).ShouldBeTrue();
    }

    /// <summary>Drags a text selection over characters of page 1.</summary>
    private static async Task SelectAsync(DocumentTabViewModel tab, int line, int from, int to)
    {
        (await tab.TextSelection.PressAsync(At(tab, 0, line, from), clickCount: 1)).ShouldBeTrue();
        tab.TextSelection.DragTo(At(tab, 0, line, to));
        tab.TextSelection.Release();
    }

    private static Guid Key(DocumentTabViewModel tab, int page) => tab.Workspace.Pages.Pages[page].Key;

    private static ViewPoint Viewport(DocumentTabViewModel tab, int page, PdfPoint point)
    {
        var layout = tab.Viewer.Layout;
        var bounds = layout.GetPageBounds(page);
        var mapper = new PageCoordinateMapper(tab.Editor.GeometryOf(Key(tab, page)).ShouldNotBeNull(), layout.Scale);
        var shown = mapper.PdfToScreen(point);
        return new ViewPoint(bounds.X + shown.X - tab.Viewer.ScrollOffset.X, bounds.Y + shown.Y - tab.Viewer.ScrollOffset.Y);
    }

    /// <summary>The viewport point over the middle of character <paramref name="column"/> of a line.</summary>
    private static ViewPoint At(DocumentTabViewModel tab, int page, int line, int column)
        => Viewport(tab, page, new PdfPoint(Left + ((column + 0.5) * CharWidth), Top - (line * LineHeight) + (LineHeight / 2)));

    /// <summary>Lines drawn from the top, each character <see cref="CharWidth"/> wide, with PDFium's generated line ends.</summary>
    private static PageText Layer(string[] lines)
    {
        var chars = new List<TextChar>();
        for (var l = 0; l < lines.Length; l++)
        {
            var bottom = Top - (l * LineHeight);
            for (var c = 0; c < lines[l].Length; c++)
            {
                var x = Left + (c * CharWidth);
                chars.Add(new TextChar(lines[l][c].ToString(), new PdfRect(x, bottom, x + CharWidth, bottom + LineHeight - 2)));
            }

            if (l < lines.Length - 1)
            {
                chars.Add(new TextChar("\r", default, IsGenerated: true));
                chars.Add(new TextChar("\n", default, IsGenerated: true));
            }
        }

        return new PageText(chars);
    }

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
