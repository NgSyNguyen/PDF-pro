using System.Globalization;
using System.IO;
using System.Text;
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
using PdfPro.Core.Overlay;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Annotating;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Bình luận": the comments of the session and of the file, filtered, answered, reviewed, exported.</summary>
public sealed class CommentsViewModelTests : IDisposable
{
    private static readonly DateTimeOffset When = new(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(7));

    private readonly WorkspaceFixture _fixture = new();
    private readonly TempDirectory _temp = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly DocumentTabFactory _factory;

    public CommentsViewModelTests()
        => _factory = new DocumentTabFactory(
            new FakePageImageRenderer { AutoComplete = true },
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            new ManualDebouncerFactory(),
            TestOperations.Catalog(),
            Substitute.For<IMessageService>(),
            Substitute.For<IPageDialogService>(),
            _files,
            Substitute.For<IPdfDocumentLoader>(),
            NullLoggerFactory.Instance,
            settings: new FakeSettingsService(AppSettings.Default with { AnnotationAuthor = "Lan" }));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _fixture.Dispose();
        _temp.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task TheFilesAndTheSessionsComments_AreListedPageByPage_TopToBottom_WithTheirConversation()
    {
        await using var tab = await OpenAsync();
        AddNote(tab, page: 1, "n1", new PdfPoint(100, 500), "Kiểm tra ngày ký");
        var comments = await ShowAsync(tab);

        comments.Items.Select(i => (i.PageIndex, i.KindLabel)).ShouldBe(
        [
            (0, Strings.Cmd_Highlight),
            (0, Strings.Cmd_StickyNote),
            (2, Strings.Cmd_Ink),
        ]);
        var fromFile = comments.Items[0];
        fromFile.IsFromFile.ShouldBeTrue();
        fromFile.CanEdit.ShouldBeTrue("changed as the next revision");
        fromFile.Author.ShouldBe("Minh");
        fromFile.Contents.ShouldBe("Số liệu sai");
        fromFile.Replies.ShouldHaveSingleItem().ShouldBe(new CommentReplyRecord("Lan", When.AddHours(1), "Đã sửa theo báo cáo"));
        fromFile.State.ShouldBe(ReviewState.Accepted);
        fromFile.StateText.ShouldBe(Strings.Comments_State_Accepted);
        var mine = comments.Items[1];
        mine.IsFromFile.ShouldBeFalse();
        mine.CanEdit.ShouldBeTrue();
        mine.Author.ShouldBe("Lan");
        mine.Contents.ShouldBe("Kiểm tra ngày ký");
        comments.Items[2].Author.ShouldBe(Strings.Annotation_NoAuthor, "the ink in the file has no author");
        comments.StatusText.ShouldBe(Format(Strings.Comments_Count, 3));
        comments.AuthorOptions.Select(o => o.Value).ShouldBe([string.Empty, Strings.Annotation_NoAuthor, "Lan", "Minh"], ignoreOrder: true);
    }

    [Fact]
    public async Task Filters_NarrowTheList()
    {
        await using var tab = await OpenAsync();
        AddNote(tab, page: 1, "n1", new PdfPoint(100, 500), "Kiểm tra ngày ký");
        var comments = await ShowAsync(tab);

        comments.KindFilter = CommentKindFilter.Markup;
        comments.Items.ShouldHaveSingleItem().KindLabel.ShouldBe(Strings.Cmd_Highlight);
        comments.StatusText.ShouldBe(Format(Strings.Comments_CountFiltered, 1, 3));

        comments.KindFilter = CommentKindFilter.All;
        comments.AuthorFilter = "Lan";
        comments.Items.ShouldHaveSingleItem().Contents.ShouldBe("Kiểm tra ngày ký");

        comments.AuthorFilter = string.Empty;
        comments.StateFilter = nameof(ReviewState.Accepted);
        comments.Items.ShouldHaveSingleItem().Contents.ShouldBe("Số liệu sai");

        comments.StateFilter = string.Empty;
        comments.SearchText = "da sua"; // no marks: found in the answer "Đã sửa"
        comments.Items.ShouldHaveSingleItem().Contents.ShouldBe("Số liệu sai");

        comments.SearchText = "không có";
        comments.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACommentOfTheSession_IsAnswered_Reviewed_AndDeleted_EachOneStep()
    {
        await using var tab = await OpenAsync();
        AddNote(tab, page: 1, "n1", new PdfPoint(100, 500), "Kiểm tra ngày ký");
        var comments = await ShowAsync(tab);
        var mine = comments.Items.Single(i => i.Target != null);

        mine.ReplyCommand.CanExecute(null).ShouldBeFalse("nothing typed yet");
        mine.ReplyDraft = "  Đã kiểm tra  ";
        mine.ReplyCommand.Execute(null);
        await comments.RefreshAsync();

        var note = tab.Workspace.Session.Plan[0].FindAnnotation("n1").ShouldNotBeNull();
        var reply = note.Replies.ShouldHaveSingleItem();
        reply.Contents.ShouldBe("Đã kiểm tra");
        reply.Author.ShouldBe("Lan");
        tab.Workspace.Session.History.NextUndoDescription.ShouldBe(Format(Strings.History_UpdateAnnotations, 1));
        comments.Items.Single(i => i.Target != null).Replies.ShouldHaveSingleItem().Contents.ShouldBe("Đã kiểm tra");

        comments.Items.Single(i => i.Target != null).SetStateCommand.Execute(nameof(ReviewState.Completed));
        await comments.RefreshAsync();
        tab.Workspace.Session.Plan[0].FindAnnotation("n1")!.State.ShouldBe(ReviewState.Completed);
        comments.Items.Single(i => i.Target != null).StateText.ShouldBe(Strings.Comments_State_Completed);

        comments.Items.Single(i => i.Target != null).DeleteCommand.Execute(null);
        await comments.RefreshAsync();
        tab.Workspace.Session.Plan[0].Annotations.ShouldBeEmpty();
        comments.Items.ShouldAllBe(i => i.IsFromFile);
    }

    [Fact]
    public async Task ACommentOfTheFile_IsAnswered_Reviewed_AndDeleted_AsTheNextRevision()
    {
        await using var tab = await OpenAsync();
        var requests = new List<ContentChangeRequest>();
        tab.ContentChangeRequested += (_, request) => requests.Add(request);
        var comments = await ShowAsync(tab);
        var fromFile = comments.Items[0];
        var steps = tab.Workspace.Session.History.UndoCount;

        fromFile.ReplyDraft = "  Đồng ý  ";
        fromFile.ReplyCommand.Execute(null);
        fromFile.SetStateCommand.Execute(nameof(ReviewState.Rejected));
        fromFile.DeleteCommand.Execute(null);

        fromFile.ReplyDraft.ShouldBeEmpty("the answer was sent");
        tab.Workspace.Session.History.UndoCount.ShouldBe(steps, "the window runs the change in the background");
        requests.Select(r => r.Operation.Id).ShouldAllBe(id => id == EditFileAnnotationsOperation.OperationId);
        requests.Select(r => r.Description).ShouldBe([Strings.History_ReplyFileComment, Strings.History_ReviewFileComment, Strings.History_DeleteFileComment]);
        var edits = requests.Select(r => ((EditFileAnnotationsParams)r.Parameters).Edits.ShouldHaveSingleItem()).ToArray();
        edits.ShouldAllBe(e => e.Page == 1 && e.Index == 0 && e.Kind == FileAnnotationKind.Highlight && e.Name == "h" && e.Rect == new PdfRect(72, 700, 300, 712));
        edits[0].Action.ShouldBe(FileAnnotationAction.Reply);
        edits[0].Text.ShouldBe("Đồng ý");
        edits[0].Author.ShouldBe("Lan");
        edits[0].Id.ShouldNotBeNullOrEmpty();
        edits[1].Action.ShouldBe(FileAnnotationAction.SetState);
        edits[1].State.ShouldBe(ReviewState.Rejected);
        edits[1].Id.ShouldNotBe(edits[0].Id);
        edits[2].Action.ShouldBe(FileAnnotationAction.Delete);
        new EditFileAnnotationsOperation().Validate(new EditFileAnnotationsParams(edits), new Core.Documents.DocumentInfo(3)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task TheCommentOfTheSession_IsRewritten_AsOneStep()
    {
        await using var tab = await OpenAsync();
        AddNote(tab, page: 1, "n1", new PdfPoint(100, 500), "Kiểm tra ngày ký");
        var comments = await ShowAsync(tab);
        var mine = comments.Items.Single(i => i.Target != null);

        mine.CanEditContents.ShouldBeTrue();
        mine.EditContentsCommand.Execute(null);
        mine.IsEditingContents.ShouldBeTrue();
        mine.ShowsContents.ShouldBeFalse();
        mine.ContentsDraft.ShouldBe("Kiểm tra ngày ký");
        mine.ContentsDraft = "Kiểm tra lại\r\nngày ký  ";
        mine.SaveContentsCommand.Execute(null);

        mine.IsEditingContents.ShouldBeFalse();
        tab.Workspace.Session.Plan[0].FindAnnotation("n1")!.Contents.ShouldBe("Kiểm tra lại\nngày ký");
        tab.Workspace.Session.History.NextUndoDescription.ShouldBe(Format(Strings.History_UpdateAnnotations, 1));
    }

    [Fact]
    public async Task TheCommentOfAFileAnnotation_IsRewrittenAsTheNextRevision_ButNotTheWordsOfATextBox()
    {
        await using var tab = await OpenAsync(page => page == 0
            ?
            [
                new FileAnnotation(0, FileAnnotationKind.Highlight, new PdfRect(72, 700, 300, 712), "h", "Minh", "Số liệu sai", When, null, null, null, 4, []),
                new FileAnnotation(1, FileAnnotationKind.FreeText, new PdfRect(72, 600, 300, 640), "t", "Minh", "Hộp chữ", When, null, null, null, 4, []),
            ]
            : []);
        var requests = new List<ContentChangeRequest>();
        tab.ContentChangeRequested += (_, request) => requests.Add(request);
        var comments = await ShowAsync(tab);
        var highlight = comments.Items.Single(i => i.FromFile?.Name == "h");
        var box = comments.Items.Single(i => i.FromFile?.Name == "t");

        highlight.EditContentsCommand.Execute(null);
        highlight.ContentsDraft = "Số liệu đúng";
        highlight.SaveContentsCommand.Execute(null);
        box.CanEditContents.ShouldBeFalse("its words are drawn in its appearance");
        box.EditContentsCommand.Execute(null);

        box.IsEditingContents.ShouldBeFalse();
        highlight.IsEditingContents.ShouldBeFalse();
        var request = requests.ShouldHaveSingleItem();
        request.Description.ShouldBe(Strings.History_EditFileComment);
        var edit = ((EditFileAnnotationsParams)request.Parameters).Edits.ShouldHaveSingleItem();
        edit.Action.ShouldBe(FileAnnotationAction.SetContents);
        edit.Text.ShouldBe("Số liệu đúng");
        edit.Name.ShouldBe("h");
    }

    [Fact]
    public async Task ACommentOfTheFile_IsNotChanged_WhileObjectsWaitInTheEditor()
    {
        await using var tab = await OpenAsync();
        var requests = new List<ContentChangeRequest>();
        tab.ContentChangeRequested += (_, request) => requests.Add(request);
        var comments = await ShowAsync(tab);
        tab.Editor.Add(tab.Workspace.Pages.Pages[0].Key, new ShapeOverlay(0, new OverlayBox(new PdfPoint(100, 100), new PdfSize(20, 20)), ShapeKind.Whiteout));

        var fromFile = comments.Items[0];
        fromFile.ReplyDraft = "Đồng ý";
        fromFile.ReplyCommand.Execute(null);

        requests.ShouldBeEmpty();
        fromFile.ReplyDraft.ShouldBe("Đồng ý", "kept to be sent later");
    }

    [Fact]
    public async Task ChoosingAComment_ShowsIt_AndSelectsItWhenItIsTheSessions()
    {
        await using var tab = await OpenAsync();
        AddNote(tab, page: 3, "n3", new PdfPoint(100, 500), "Trang cuối");
        var comments = await ShowAsync(tab);

        comments.SelectedItem = comments.Items.Single(i => i.Target != null);

        await WaitUntilAsync(() => tab.Viewer.CurrentPageIndex == 2);
        tab.Annotations.Selected.ShouldBe(new AnnotationTarget(tab.Workspace.Pages.Pages[2].Key, "n3"));
    }

    [Fact]
    public async Task TheList_IsReadOnlyWhileShown_AndAgainWhenThePagesChange()
    {
        await using var tab = await OpenAsync();
        var comments = tab.Comments;
        comments.Items.ShouldBeEmpty("not read before the panel is shown");

        comments.IsActive = true;
        await WaitUntilAsync(() => !comments.IsLoading && comments.Items.Count == 2);
        AddNote(tab, page: 2, "n2", new PdfPoint(100, 500), "Mới");
        await WaitUntilAsync(() => !comments.IsLoading && comments.Items.Count == 3);

        comments.IsActive = false;
        tab.UndoCommand.Execute(null);
        comments.Items.Count.ShouldBe(3, "hidden: not read again");
        comments.IsActive = true;
        await WaitUntilAsync(() => !comments.IsLoading && comments.Items.Count == 2);
    }

    [Fact]
    public async Task Exporting_WritesMarkdownOrCsv_AsChosen()
    {
        await using var tab = await OpenAsync();
        AddNote(tab, page: 1, "n1", new PdfPoint(100, 500), "Kiểm tra ngày ký");
        var comments = await ShowAsync(tab);
        var markdown = _temp.Combine("comments.md");
        var csv = _temp.Combine("comments.csv");

        _files.PickCommentExportPath(Arg.Any<string>()).Returns(markdown, csv);
        await comments.ExportCommand.ExecuteAsync(null);
        await comments.ExportCommand.ExecuteAsync(null);

        _files.Received().PickCommentExportPath($"a - {Strings.Comments_Title}");
        var md = await File.ReadAllTextAsync(markdown, Token);
        md.ShouldStartWith($"# {Strings.Comments_Title} — a.pdf");
        md.ShouldContain("Kiểm tra ngày ký");
        md.ShouldContain("Lan");
        var bytes = await File.ReadAllBytesAsync(csv, Token);
        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF }, "Excel needs the byte order mark for Vietnamese");
        var rows = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        rows.Length.ShouldBe(1 + 3 + 1, "the header, three comments and one answer");
    }

    private async Task<DocumentTabViewModel> OpenAsync(Func<int, IReadOnlyList<FileAnnotation>>? annotations = null)
    {
        var document = FakePdfDocument.WithPages(3);
        document.FileAnnotations = annotations ?? (page => page switch
        {
            0 =>
            [
                new FileAnnotation(0, FileAnnotationKind.Highlight, new PdfRect(72, 700, 300, 712), "h", "Minh", "Số liệu sai", When, null, null, null, 4, [TextQuad.Of(new PdfRect(72, 700, 300, 712))]),
                new FileAnnotation(1, FileAnnotationKind.Text, new PdfRect(72, 700, 92, 712), "h-r", "Lan", "Đã sửa theo báo cáo", When.AddHours(1), 0, null, null, 28, []),
                new FileAnnotation(2, FileAnnotationKind.Text, new PdfRect(72, 700, 92, 712), "h-s", "Lan", "Accepted set by Lan", When.AddHours(2), 0, "Accepted", "Review", 28, []),
                new FileAnnotation(3, FileAnnotationKind.Link, new PdfRect(72, 600, 200, 612), null, null, null, null, null, null, null, 4, []),
                new FileAnnotation(4, FileAnnotationKind.Popup, new PdfRect(300, 600, 500, 700), null, null, null, null, 0, null, null, 0, []),
            ],
            2 => [new FileAnnotation(0, FileAnnotationKind.Ink, new PdfRect(100, 100, 200, 200), null, null, null, null, null, null, null, 4, [])],
            _ => [],
        });
        var tab = _factory.Create(await _fixture.CreateAsync(document));
        tab.Viewer.ViewportSize = new ViewSize(1000, 700);
        foreach (var page in tab.Workspace.Pages.Pages)
        {
            await tab.Editor.GetGeometryAsync(page.Key);
        }

        return tab;
    }

    private static async Task<CommentsViewModel> ShowAsync(DocumentTabViewModel tab)
    {
        tab.Comments.IsActive = true;
        await tab.Comments.RefreshAsync();
        return tab.Comments;
    }

    private static void AddNote(DocumentTabViewModel tab, int page, string id, PdfPoint at, string contents)
    {
        var note = new NoteAnnotation(id, AnnotationToolsViewModel.NoteColor, 1, "Lan", When, When, contents, at);
        tab.Apply(TestOperations.Catalog().Get<AddAnnotationsOperation>(), AddAnnotationsParams.On(page, [note])).ShouldBeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the comments to be read");
            await Task.Delay(10, Token);
        }
    }

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
