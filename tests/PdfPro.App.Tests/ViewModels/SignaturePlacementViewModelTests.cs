using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Geometry;
using PdfPro.Infrastructure.Settings;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Ký số" → "Kéo khung trên trang": the box of a visible signature, placed once with the viewer's mouse.</summary>
public sealed class SignaturePlacementViewModelTests : IDisposable
{
    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly DocumentTabFactory _factory;

    public SignaturePlacementViewModelTests()
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
            NullLoggerFactory.Instance);

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task ADraggedBox_IsTheAnswer_KeptInsideThePage()
    {
        var tab = await OpenAsync();
        var placing = tab.PlaceSignatureAsync();
        var placement = tab.SignaturePlacement;
        placement.IsActive.ShouldBeTrue();

        placement.Press(Viewport(tab, 1, new PdfPoint(400, 120))).ShouldBeTrue();
        placement.DragTo(Viewport(tab, 1, new PdfPoint(700, 40)));
        placement.Preview(PageKey(tab, 1)).ShouldNotBeNull();
        placement.Preview(PageKey(tab, 0)).ShouldBeNull();
        placement.Release();

        var box = (await placing).ShouldNotBeNull();
        box.PageIndex.ShouldBe(1);
        box.PageKey.ShouldBe(PageKey(tab, 1));
        Round(box.Rect).ShouldBe(new PdfRect(400, 40, 595, 120), "the box stops at the page's edge");
        placement.IsActive.ShouldBeFalse();
        tab.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task AClick_GivesTheUsualBox_AroundThePoint_InsideThePage()
    {
        var tab = await OpenAsync();
        var placing = tab.PlaceSignatureAsync();

        tab.SignaturePlacement.Press(Viewport(tab, 0, new PdfPoint(580, 830)));
        tab.SignaturePlacement.Release();

        var box = (await placing).ShouldNotBeNull();
        Round(box.Rect).ShouldBe(new PdfRect(595 - SignaturePlacementViewModel.DefaultWidth, 842 - SignaturePlacementViewModel.DefaultHeight, 595, 842));
    }

    [Theory]
    [InlineData(0, 180, 60)]
    [InlineData(90, 60, 180)]
    [InlineData(270, 60, 180)]
    public void TheUsualBox_IsUprightAsThePageShows(int rotation, double width, double height)
    {
        var box = SignaturePlacementViewModel.Around(new PdfPoint(300, 400), new PageGeometry(new PdfRect(0, 0, 595, 842), rotation));

        box.Width.ShouldBe(width);
        box.Height.ShouldBe(height);
        box.Left.ShouldBe(300 - (width / 2));
        box.Bottom.ShouldBe(400 - (height / 2));
    }

    [Fact]
    public async Task Esc_GivesNothing()
    {
        var tab = await OpenAsync();
        var placing = tab.PlaceSignatureAsync();
        tab.SignaturePlacement.Press(Viewport(tab, 0, new PdfPoint(100, 100)));

        tab.SignaturePlacement.CancelCommand.Execute(null);

        (await placing).ShouldBeNull();
        tab.SignaturePlacement.IsActive.ShouldBeFalse();
        tab.SignaturePlacement.IsDragging.ShouldBeFalse();
    }

    [Fact]
    public async Task AnotherMode_TakingTheViewer_GivesNothing()
    {
        var tab = await OpenAsync();
        var placing = tab.PlaceSignatureAsync();
        tab.Forms.IsActive.ShouldBeFalse("the form's inputs give the mouse to the placing");

        tab.ToggleRedactionCommand.Execute(null);

        (await placing).ShouldBeNull();
        tab.SignaturePlacement.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task Placing_TurnsTheOtherModesOff()
    {
        var tab = await OpenAsync();
        tab.ToggleContentEditingCommand.Execute(null);

        var placing = tab.PlaceSignatureAsync();

        tab.ContentEditor.IsActive.ShouldBeFalse();
        tab.SignaturePlacement.IsActive.ShouldBeTrue();
        tab.SignaturePlacement.Cancel();
        (await placing).ShouldBeNull();
        tab.Forms.IsActive.ShouldBeTrue("the form's inputs take the mouse back");
    }

    [Fact]
    public async Task ClosingTheDocument_GivesNothing()
    {
        var tab = await OpenAsync();
        var placing = tab.PlaceSignatureAsync();

        await tab.DisposeAsync();

        (await placing).ShouldBeNull();
    }

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

    private async Task<DocumentTabViewModel> OpenAsync()
    {
        var tab = _factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(2)));
        tab.Viewer.ViewportSize = new ViewSize(1000, 3000);
        foreach (var page in tab.Workspace.Pages.Pages)
        {
            await tab.Editor.GetGeometryAsync(page.Key);
        }

        return tab;
    }
}
