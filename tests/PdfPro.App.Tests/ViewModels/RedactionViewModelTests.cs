using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;
using PdfPro.Core.Redaction;
using PdfPro.Core.Text;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Security;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Xóa thông tin": marking areas, text and matches on the viewer, then applying them as one content change.</summary>
public sealed class RedactionViewModelTests : IDisposable
{
    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly IPageDialogService _dialogs = Substitute.For<IPageDialogService>();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly DocumentTabFactory _factory;

    public RedactionViewModelTests()
        => _factory = new DocumentTabFactory(
            new FakePageImageRenderer { AutoComplete = true },
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            new ManualDebouncerFactory(),
            TestOperations.Catalog(),
            _messages,
            _dialogs,
            Substitute.For<IFileDialogService>(),
            Substitute.For<IPdfDocumentLoader>(),
            NullLoggerFactory.Instance);

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task TheMode_TakesTheViewer_FromTheOtherModes()
    {
        var (tab, _) = await OpenAsync();
        tab.ToggleContentEditingCommand.Execute(null);

        tab.ToggleRedactionCommand.Execute(null);

        tab.Redaction.IsActive.ShouldBeTrue();
        tab.IsRedacting.ShouldBeTrue();
        tab.ContentEditor.IsActive.ShouldBeFalse();
        tab.ToggleFormDesignCommand.Execute(null);
        tab.Redaction.IsActive.ShouldBeFalse("form design takes the viewer back");
    }

    [Fact]
    public async Task ADraggedBox_BecomesAMark_KeptInsideThePage()
    {
        var (tab, redaction) = await RedactingAsync();

        redaction.Press(Viewport(tab, 0, new PdfPoint(100, 700))).ShouldBeTrue();
        redaction.IsDragging.ShouldBeTrue();
        redaction.DragTo(Viewport(tab, 0, new PdfPoint(700, 650)));
        redaction.Preview(PageKey(tab, 0)).ShouldNotBeNull();
        redaction.Release();

        var mark = redaction.Marks.ShouldHaveSingleItem();
        mark.PageKey.ShouldBe(PageKey(tab, 0));
        Round(mark.Rects.ShouldHaveSingleItem()).ShouldBe(new PdfRect(100, 650, 595, 700), "the box stops at the page's edge");
        redaction.Selected.ShouldBeSameAs(mark);
        redaction.Status.ShouldBe(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Redaction_Status, 1, 1));
        tab.IsDirty.ShouldBeFalse("nothing changes until the marks are applied");
    }

    [Fact]
    public async Task AClick_MarksNothing_ButPicksAMark_WhichDeleteRemoves()
    {
        var (tab, redaction) = await RedactingAsync();
        Drag(tab, redaction, 0, new PdfPoint(100, 700), new PdfPoint(101, 701));
        redaction.HasMarks.ShouldBeFalse("a click is not a box");
        Drag(tab, redaction, 0, new PdfPoint(100, 700), new PdfPoint(200, 650));
        redaction.Escape().ShouldBeTrue("Esc drops the selection first");

        redaction.Press(Viewport(tab, 0, new PdfPoint(150, 680))).ShouldBeTrue();
        redaction.Selected.ShouldNotBeNull();
        redaction.DeleteCommand.Execute(null);

        redaction.HasMarks.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectedText_IsMarkedLineByLine()
    {
        var document = FakePdfDocument.WithPages(2);
        document.TextLayers = _ => Line("Số CCCD 001203004567", 100, 700);
        var (tab, redaction) = await RedactingAsync(document);
        await tab.TextLayers.GetAsync(tab.Workspace.Pages.Pages[1], cancellationToken: TestContext.Current.CancellationToken);
        tab.TextSelection.Select(1, new TextRange(8, 20));

        redaction.MarkSelectionCommand.Execute(null);

        var mark = redaction.Marks.ShouldHaveSingleItem();
        mark.PageKey.ShouldBe(PageKey(tab, 1));
        mark.Rects.ShouldHaveSingleItem().Left.ShouldBe(100 + (8 * 6));
        tab.TextSelection.HasSelection.ShouldBeFalse();
    }

    [Fact]
    public async Task FoundMatches_AreReviewed_ThenMarked()
    {
        var document = FakePdfDocument.WithPages(3);
        document.TextLayers = page => page == 1 ? Line("CCCD 001203004567, email an@example.vn", 72, 700) : PageText.Empty;
        var (tab, redaction) = await OpenAsync(document);
        _dialogs.ShowRedactionSearch(Arg.Any<RedactionSearchViewModel>()).Returns(call =>
        {
            var search = call.Arg<RedactionSearchViewModel>();
            search.SearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            search.Hits.Select(h => (h.PageNumber, h.Kind, h.Text)).ShouldBe(
            [
                (2, Strings.Redaction_Kind_NationalId, "001203004567"),
                (2, Strings.Redaction_Kind_Email, "an@example.vn"),
            ]);
            search.Hits[1].IsChecked = false;
            search.MarkCommand.Execute(null);
            return true;
        });

        tab.FindRedactionsCommand.Execute(null);

        redaction.IsActive.ShouldBeTrue("finding turns the mode on to review the marks");
        redaction.Marks.ShouldHaveSingleItem().Pattern.ShouldBe(RedactionPattern.NationalId);
    }

    [Fact]
    public async Task Applying_AsksTheWindowForOneContentChange_ThenTheMarksGo()
    {
        var (tab, redaction) = await RedactingAsync();
        Drag(tab, redaction, 1, new PdfPoint(100, 700), new PdfPoint(300, 650));
        ContentChangeRequest? request = null;
        tab.ContentChangeRequested += (_, r) => request = r;
        _dialogs.ShowRedactionApply(Arg.Any<RedactionApplyViewModel>()).Returns(call =>
        {
            var apply = call.Arg<RedactionApplyViewModel>();
            apply.Marks.ShouldBe(1);
            apply.UseLabel = true;
            apply.Fill = RgbColor.Gray;
            apply.ApplyCommand.Execute(null);
            return true;
        });

        tab.ApplyRedactionsCommand.Execute(null);

        request.ShouldNotBeNull().Operation.ShouldBeOfType<RedactOperation>();
        var parameters = request.Parameters.ShouldBeOfType<RedactParams>();
        parameters.Areas.ShouldHaveSingleItem().Page.ShouldBe(2);
        parameters.Fill.ShouldBe(RgbColor.Gray);
        parameters.Label.ShouldBe(Strings.Redaction_DefaultLabel);
        parameters.Mode.ShouldBe(RedactionMode.Raster, "pages become pictures unless the vector mode is chosen");
        request.Description.ShouldBe(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.History_Redact, 1));
        redaction.HasMarks.ShouldBeTrue("until the window has applied them");
        request.Completed!(true);
        redaction.HasMarks.ShouldBeFalse();
    }

    [Fact]
    public async Task ChoosingToKeepTheText_RedactsInTheVectorMode()
    {
        var (tab, redaction) = await RedactingAsync();
        Drag(tab, redaction, 0, new PdfPoint(100, 700), new PdfPoint(300, 650));
        ContentChangeRequest? request = null;
        tab.ContentChangeRequested += (_, r) => request = r;
        _dialogs.ShowRedactionApply(Arg.Any<RedactionApplyViewModel>()).Returns(call =>
        {
            var apply = call.Arg<RedactionApplyViewModel>();
            apply.AsPictures.ShouldBeTrue();
            apply.KeepText = true;
            apply.AsPictures.ShouldBeFalse();
            apply.ApplyCommand.Execute(null);
            return true;
        });

        tab.ApplyRedactionsCommand.Execute(null);

        request.ShouldNotBeNull().Parameters.ShouldBeOfType<RedactParams>().Mode.ShouldBe(RedactionMode.Vector);
    }

    [Fact]
    public void TheReport_SaysWhichPagesBecamePictures()
    {
        var report = new RedactionReport(3, [1, 2, 4], [4], 1, 0, 0, [], Xmp: false, Structure: 2);

        var text = DocumentTabViewModel.Describe(report, RedactionMode.Vector);

        text.ShouldStartWith(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Redaction_Done, 3, 3));
        text.ShouldContain(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Redaction_Done_PicturesSome, "4"));
        text.ShouldContain(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Redaction_Done_Annotations, 1));
        text.ShouldContain(Strings.Redaction_Done_Metadata, Case.Sensitive, "tag texts naming the data went");
        text.ShouldNotContain(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Redaction_Done_Fields, 0));
        DocumentTabViewModel.Describe(report with { PicturePages = [1, 2, 4] }, RedactionMode.Raster).ShouldContain(Strings.Redaction_Done_PicturesAll);
        DocumentTabViewModel.Describe(report with { PicturePages = [] }, RedactionMode.Vector).ShouldNotContain(Strings.Redaction_Done_PicturesAll);
    }

    [Fact]
    public async Task CancellingTheConfirmation_AppliesNothing()
    {
        var (tab, redaction) = await RedactingAsync();
        Drag(tab, redaction, 0, new PdfPoint(100, 700), new PdfPoint(300, 650));
        var asked = false;
        tab.ContentChangeRequested += (_, _) => asked = true;
        _dialogs.ShowRedactionApply(Arg.Any<RedactionApplyViewModel>()).Returns(false);

        tab.ApplyRedactionsCommand.Execute(null);

        asked.ShouldBeFalse();
        redaction.HasMarks.ShouldBeTrue();
    }

    [Fact]
    public async Task Marks_FollowTheirPage_WhenPagesMove()
    {
        var (tab, redaction) = await RedactingAsync(FakePdfDocument.WithPages(3));
        Drag(tab, redaction, 0, new PdfPoint(100, 700), new PdfPoint(300, 650));

        tab.Apply(TestOperations.Catalog().Get<MovePagesOperation>(), new MovePagesParams("1", 4)).ShouldBeTrue();

        redaction.Parameters(RgbColor.Black, null)!.Areas.ShouldHaveSingleItem().Page.ShouldBe(3);
        tab.Apply(TestOperations.Catalog().Get<DeletePagesOperation>(), new DeletePagesParams("3")).ShouldBeTrue();
        redaction.HasMarks.ShouldBeFalse("the marked page is gone");
    }

    private static PageText Line(string text, double left, double bottom)
        => new([.. text.Select((c, i) => new TextChar(c.ToString(), new PdfRect(left + (i * 6), bottom, left + ((i + 1) * 6), bottom + 12)))]);

    private static PdfRect Round(PdfRect rect) => new(Math.Round(rect.Left), Math.Round(rect.Bottom), Math.Round(rect.Right), Math.Round(rect.Top));

    private static Guid PageKey(DocumentTabViewModel tab, int index) => tab.Workspace.Pages.Pages[index].Key;

    private static ViewPoint Viewport(DocumentTabViewModel tab, int page, PdfPoint point)
    {
        var layout = tab.Viewer.Layout;
        var bounds = layout.GetPageBounds(page);
        var mapper = new PageCoordinateMapper(tab.Editor.GeometryOf(PageKey(tab, page)).ShouldNotBeNull(), layout.Scale);
        var shown = mapper.PdfToScreen(point);
        return new ViewPoint(bounds.X + shown.X - tab.Viewer.ScrollOffset.X, bounds.Y + shown.Y - tab.Viewer.ScrollOffset.Y);
    }

    private static void Drag(DocumentTabViewModel tab, RedactionViewModel redaction, int page, PdfPoint from, PdfPoint to)
    {
        redaction.Press(Viewport(tab, page, from));
        redaction.DragTo(Viewport(tab, page, to));
        redaction.Release();
    }

    private async Task<(DocumentTabViewModel Tab, RedactionViewModel Redaction)> OpenAsync(FakePdfDocument? document = null)
    {
        var tab = _factory.Create(await _fixture.CreateAsync(document ?? FakePdfDocument.WithPages(2)));
        tab.Viewer.ViewportSize = new ViewSize(1000, 3000);
        foreach (var page in tab.Workspace.Pages.Pages)
        {
            await tab.Editor.GetGeometryAsync(page.Key);
        }

        return (tab, tab.Redaction);
    }

    private async Task<(DocumentTabViewModel Tab, RedactionViewModel Redaction)> RedactingAsync(FakePdfDocument? document = null)
    {
        var (tab, redaction) = await OpenAsync(document);
        tab.ToggleRedactionCommand.Execute(null);
        redaction.IsActive.ShouldBeTrue();
        return (tab, redaction);
    }
}
