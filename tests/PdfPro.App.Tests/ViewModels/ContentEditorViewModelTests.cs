using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Annotating;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Editing;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Overlay;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Editing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Sửa nội dung": framing, selecting, moving, rewriting, removing and turning the drawings of a page.</summary>
public sealed class ContentEditorViewModelTests : IDisposable
{
    private static readonly PageObjectInfo Background = Info(0, PageObjectKind.Path, new PdfRect(0, 0, 612, 792));
    private static readonly PageObjectInfo Title = Info(1, PageObjectKind.Text, new PdfRect(100, 700, 300, 720)) with
    {
        Text = new PageTextObject("Ủy ban nhân dân", "ABCDEF+TimesNewRomanPSMT", 12, true, false, false, true, false),
        Matrix = new PdfMatrix(1, 0, 0, 1, 100, 704),
    };

    private static readonly PageObjectInfo Photo = Info(2, PageObjectKind.Image, new PdfRect(100, 400, 300, 550)) with { Image = new PageImageObject(480, 320) };

    private readonly WorkspaceFixture _fixture = new();
    private readonly TempDirectory _temp = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly DocumentTabFactory _factory;

    public ContentEditorViewModelTests()
        => _factory = new DocumentTabFactory(
            new FakePageImageRenderer { AutoComplete = true },
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            new ManualDebouncerFactory(),
            TestOperations.Catalog(),
            _messages,
            Substitute.For<IPageDialogService>(),
            _files,
            Substitute.For<IPdfDocumentLoader>(),
            NullLoggerFactory.Instance);

    public void Dispose()
    {
        _fixture.Dispose();
        _temp.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task TheDrawingUnderTheMouse_IsFramed_Selected_AndMovedByADrag()
    {
        var (tab, requests) = await OpenAsync();
        var editor = tab.ContentEditor;

        editor.HoverAt(Viewport(tab, new PdfPoint(150, 710)));
        editor.Hovered.ShouldNotBeNull().Object.ShouldBe(Title);
        editor.HoverAt(Viewport(tab, new PdfPoint(500, 100)));
        editor.Hovered.ShouldNotBeNull().Object.ShouldBe(Background, "a background is picked only where nothing else is");

        var grab = Viewport(tab, new PdfPoint(200, 475));
        editor.Press(grab, clickCount: 1).ShouldBeTrue();
        editor.Selected.ShouldNotBeNull().Object.ShouldBe(Photo);
        editor.IsImageSelected.ShouldBeTrue();
        editor.SelectionText.ShouldBe(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.ContentEditor_Image, 480, 320));
        editor.IsMoving.ShouldBeTrue();
        editor.DragTo(new ViewPoint(grab.X + 20, grab.Y + 10));
        var scale = tab.Viewer.Layout.Scale;
        editor.Shown(editor.Selected!.PageKey, editor.Selected).Left.ShouldBe(100 + (20 / scale), 0.01, "the frame follows the mouse");
        editor.Release();

        var request = requests.ShouldHaveSingleItem();
        request.Operation.ShouldBeOfType<EditContentOperation>();
        request.Description.ShouldBe(Strings.History_MoveObject);
        var move = ((EditContentParams)request.Parameters).Edits.ShouldHaveSingleItem().ShouldBeOfType<MoveObjectEdit>();
        move.Target.ShouldBe(PageObjectRef.Of(1, Photo));
        move.Dx.ShouldBe(20 / scale, 0.01);
        move.Dy.ShouldBe(-10 / scale, 0.01, "down on the screen is down on the page");
    }

    [Fact]
    public async Task ADoubleClickOnText_OpensItsWords_AndTheNewWordsAreWritten()
    {
        var (tab, requests) = await OpenAsync();
        var editor = tab.ContentEditor;

        editor.Press(Viewport(tab, new PdfPoint(150, 710)), clickCount: 2).ShouldBeTrue();

        editor.IsEditingText.ShouldBeTrue();
        editor.TextDraft.ShouldBe("Ủy ban nhân dân");
        editor.TextBox.Width.ShouldBeGreaterThan(0);
        editor.IsMoving.ShouldBeFalse();
        editor.TextDraft = "Hội đồng nhân dân  ";
        editor.WrapText = true;
        editor.CommitTextCommand.Execute(null);

        editor.IsEditingText.ShouldBeFalse();
        var request = requests.ShouldHaveSingleItem();
        request.Description.ShouldBe(Strings.History_ReplaceText);
        var edit = ((EditContentParams)request.Parameters).Edits.ShouldHaveSingleItem().ShouldBeOfType<ReplaceTextEdit>();
        edit.Text.ShouldBe("Hội đồng nhân dân");
        edit.Overflow.ShouldBe(TextOverflow.Wrap);
        edit.Target.ShouldBe(PageObjectRef.Of(1, Title));
    }

    [Fact]
    public async Task UnchangedOrCancelledWords_WriteNothing()
    {
        var (tab, requests) = await OpenAsync();
        var editor = tab.ContentEditor;
        editor.Press(Viewport(tab, new PdfPoint(150, 710)), clickCount: 1);

        editor.EditTextCommand.Execute(null);
        editor.CommitTextCommand.Execute(null);
        editor.EditTextCommand.Execute(null);
        editor.TextDraft = "khác";
        editor.Escape().ShouldBeTrue();

        requests.ShouldBeEmpty();
        editor.Selected.ShouldNotBeNull("Esc first drops the typing…");
        editor.Escape().ShouldBeTrue();
        editor.Selected.ShouldBeNull("…then the selection");
        editor.Escape().ShouldBeFalse();
    }

    [Fact]
    public async Task TheSelectedDrawing_IsRemovedOrTurned()
    {
        var (tab, requests) = await OpenAsync();
        var editor = tab.ContentEditor;
        editor.Press(Viewport(tab, new PdfPoint(200, 475)), clickCount: 1);
        editor.Release();

        editor.RotateRightCommand.Execute(null);
        editor.RotateLeftCommand.Execute(null);
        editor.DeleteCommand.Execute(null);

        requests.Select(r => ((EditContentParams)r.Parameters).Edits.ShouldHaveSingleItem()).ShouldBe(
        [
            new RotateObjectEdit(PageObjectRef.Of(1, Photo), -90),
            new RotateObjectEdit(PageObjectRef.Of(1, Photo), 90),
            new DeleteObjectEdit(PageObjectRef.Of(1, Photo)),
        ]);
        requests.Select(r => r.Description).ShouldBe([Strings.History_RotateObject, Strings.History_RotateObject, Strings.History_DeleteObject]);
        editor.Selected.ShouldBeNull();
    }

    [Fact]
    public async Task AnImage_IsReplacedByAPicture_OrSavedAsAFile()
    {
        var (tab, requests) = await OpenAsync();
        var editor = tab.ContentEditor;
        editor.Press(Viewport(tab, new PdfPoint(200, 475)), clickCount: 1);
        editor.Release();
        var picture = _temp.Combine("new.png");
        await File.WriteAllBytesAsync(picture, PngCodec.Encode(new BgraImage(2, 1, [0, 0, 255, 255, 0, 255, 0, 255])), TestContext.Current.CancellationToken);
        _files.PickImageFile(Arg.Any<string>()).Returns(picture);
        var saved = _temp.Combine("saved.png");
        _files.PickImageSavePath(Arg.Any<string>(), ".png").Returns(saved);

        editor.ReplaceImageCommand.Execute(null);
        await editor.SaveImageCommand.ExecuteAsync(null);

        var replace = ((EditContentParams)requests.ShouldHaveSingleItem().Parameters).Edits.ShouldHaveSingleItem().ShouldBeOfType<ReplaceImageEdit>();
        replace.KeepAspect.ShouldBeTrue();
        File.Exists(replace.ImagePath).ShouldBeTrue("copied into the session");
        replace.ImagePath.ShouldNotBe(picture);
        _files.Received(1).PickImageSavePath(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.ContentEditor_ImageFileName, 1, 3) + ".png", ".png");
        PngCodec.IsPng(await File.ReadAllBytesAsync(saved, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task TheMode_GivesWayToTheOrganizer_ThePageEditor_AndAnnotationTools()
    {
        var (tab, _) = await OpenAsync();

        tab.ContentEditor.IsActive = false;
        tab.ToggleContentEditingCommand.Execute(null);
        tab.ContentEditor.IsActive.ShouldBeTrue();
        tab.IsEditingContent.ShouldBeTrue();

        tab.ToggleOrganizeModeCommand.Execute(null);
        tab.ContentEditor.IsActive.ShouldBeFalse();

        tab.ToggleOrganizeModeCommand.Execute(null);
        tab.ToggleContentEditingCommand.Execute(null);
        tab.StartEditing(OverlayTool.Text);
        tab.ContentEditor.IsActive.ShouldBeFalse();

        tab.Editor.IsActive = false;
        tab.ToggleContentEditingCommand.Execute(null);
        await tab.InkCommand.ExecuteAsync(null);
        tab.ContentEditor.IsActive.ShouldBeFalse();
        tab.Annotations.Tool.ShouldBe(AnnotationTool.Ink);

        tab.ContentEditor.Press(Viewport(tab, new PdfPoint(150, 710)), clickCount: 1).ShouldBeFalse("the mode is off");
    }

    private static PageObjectInfo Info(int index, PageObjectKind kind, PdfRect bounds)
        => new(index, kind, bounds, [new PdfPoint(bounds.Left, bounds.Bottom), new PdfPoint(bounds.Right, bounds.Bottom), new PdfPoint(bounds.Right, bounds.Top), new PdfPoint(bounds.Left, bounds.Top)], PdfMatrix.Identity, null);

    private async Task<(DocumentTabViewModel Tab, List<ContentChangeRequest> Requests)> OpenAsync()
    {
        var document = FakePdfDocument.WithPages(2);
        document.PageObjects = page => page == 0 ? [Background, Title, Photo] : [];
        var tab = _factory.Create(await _fixture.CreateAsync(document));
        tab.Viewer.ViewportSize = new ViewSize(1000, 700);
        foreach (var page in tab.Workspace.Pages.Pages)
        {
            await tab.Editor.GetGeometryAsync(page.Key);
        }

        var requests = new List<ContentChangeRequest>();
        tab.ContentChangeRequested += (_, request) => requests.Add(request);
        tab.ToggleContentEditingCommand.Execute(null);
        var key = tab.Workspace.Pages.Pages[0].Key;
        tab.ContentEditor.ObjectsOn(key).ShouldBeEmpty("read in the background");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (tab.ContentEditor.ObjectsOn(key).Count == 0)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return (tab, requests);
    }

    private static ViewPoint Viewport(DocumentTabViewModel tab, PdfPoint point)
    {
        var layout = tab.Viewer.Layout;
        var bounds = layout.GetPageBounds(0);
        var mapper = new PageCoordinateMapper(tab.Editor.GeometryOf(tab.Workspace.Pages.Pages[0].Key).ShouldNotBeNull(), layout.Scale);
        var shown = mapper.PdfToScreen(point);
        return new ViewPoint(bounds.X + shown.X - tab.Viewer.ScrollOffset.X, bounds.Y + shown.Y - tab.Viewer.ScrollOffset.Y);
    }
}
