using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Infrastructure.Settings;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>Selecting text in the viewer with the mouse and the keyboard, and what it copies.</summary>
public sealed class TextSelectionViewModelTests : IDisposable
{
    private const double Left = 72;
    private const double CharWidth = 6;
    private const double Top = 760;
    private const double LineHeight = 14;

    private static readonly string[][] Lines =
    [
        ["Ủy ban nhân dân tỉnh", "ban hành quyết định."],
        ["Điều 1. Phạm vi", "áp dụng."],
    ];

    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly DocumentTabFactory _factory;

    public TextSelectionViewModelTests()
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
    public async Task PressingAndDragging_SelectsFromCharacterToCharacter()
    {
        await using var tab = await OpenAsync();
        var selection = tab.TextSelection;
        var changes = 0;
        selection.Changed += (_, _) => changes++;

        (await selection.PressAsync(At(tab, 0, line: 0, column: 3), clickCount: 1)).ShouldBeTrue();
        selection.HasSelection.ShouldBeFalse("a click alone selects nothing");
        selection.DragTo(At(tab, 0, line: 0, column: 14));
        selection.Release();

        selection.HasSelection.ShouldBeTrue();
        (await selection.GetTextAsync(TestContext.Current.CancellationToken)).ShouldBe("ban nhân dân");
        selection.RectsOn(Key(tab, 0)).ShouldHaveSingleItem();
        selection.RectsOn(Key(tab, 1)).ShouldBeEmpty();
        changes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task DraggingBackwards_SelectsTheSameCharacters()
    {
        await using var tab = await OpenAsync();
        var selection = tab.TextSelection;

        await selection.PressAsync(At(tab, 0, line: 0, column: 14), clickCount: 1);
        selection.DragTo(At(tab, 0, line: 0, column: 3));

        (await selection.GetTextAsync(TestContext.Current.CancellationToken)).ShouldBe("ban nhân dân");
    }

    [Fact]
    public async Task DoubleAndTripleClicks_SelectAWordAndALine()
    {
        await using var tab = await OpenAsync();
        var selection = tab.TextSelection;

        await selection.PressAsync(At(tab, 0, line: 1, column: 10), clickCount: 2);
        (await selection.GetTextAsync(TestContext.Current.CancellationToken)).ShouldBe("quyết");

        await selection.PressAsync(At(tab, 0, line: 1, column: 10), clickCount: 3);
        (await selection.GetTextAsync(TestContext.Current.CancellationToken)).ShouldBe("ban hành quyết định.");

        // Dragging after a double click goes word by word.
        await selection.PressAsync(At(tab, 0, line: 0, column: 1), clickCount: 2);
        selection.DragTo(At(tab, 0, line: 0, column: 9));
        (await selection.GetTextAsync(TestContext.Current.CancellationToken)).ShouldBe("Ủy ban nhân");
    }

    [Fact]
    public async Task PressingOutsideTheText_ClearsTheSelection()
    {
        await using var tab = await OpenAsync();
        var selection = tab.TextSelection;
        await selection.PressAsync(At(tab, 0, line: 0, column: 1), clickCount: 2);

        (await selection.PressAsync(At(tab, 0, line: 6, column: 40), clickCount: 1)).ShouldBeFalse();

        selection.HasSelection.ShouldBeFalse();
        selection.IsDragging.ShouldBeFalse();
    }

    [Fact]
    public async Task ASelection_RunsAcrossPages_AndCopiesThemInOrder()
    {
        await using var tab = await OpenAsync();
        var selection = tab.TextSelection;
        await tab.TextLayers.GetAsync(tab.Workspace.Pages.Pages[1], cancellationToken: TestContext.Current.CancellationToken);

        await selection.PressAsync(At(tab, 0, line: 1, column: 9), clickCount: 1);
        selection.DragTo(At(tab, 1, line: 0, column: 6));

        (await selection.GetTextAsync(TestContext.Current.CancellationToken)).ShouldBe("quyết định.\r\nĐiều 1.");
        selection.RectsOn(Key(tab, 0)).ShouldHaveSingleItem();
        selection.RectsOn(Key(tab, 1)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SelectAll_CopiesTheWholeDocument_ParagraphsJoined()
    {
        await using var tab = await OpenAsync();

        tab.TextSelection.SelectAll();

        (await tab.TextSelection.GetTextAsync(TestContext.Current.CancellationToken))
            .ShouldBe("Ủy ban nhân dân tỉnh ban hành quyết định.\r\nĐiều 1. Phạm vi áp dụng.");
    }

    [Fact]
    public async Task ASearchResult_CanBeSelected()
    {
        await using var tab = await OpenAsync();

        tab.TextSelection.Select(1, new TextRange(8, 15));

        (await tab.TextSelection.GetTextAsync(TestContext.Current.CancellationToken)).ShouldBe("Phạm vi");
    }

    [Fact]
    public async Task ChangingThePages_ClearsTheSelection()
    {
        await using var tab = await OpenAsync();
        tab.TextSelection.SelectAll();

        tab.RotateRightCommand.Execute(null);

        tab.TextSelection.HasSelection.ShouldBeFalse();
    }

    [Fact]
    public async Task TheTextCursor_ShowsOverText_OnceItsPageIsRead()
    {
        await using var tab = await OpenAsync();
        await tab.TextLayers.GetAsync(tab.Workspace.Pages.Pages[0], cancellationToken: TestContext.Current.CancellationToken);

        tab.TextSelection.IsOverText(At(tab, 0, line: 0, column: 3)).ShouldBeTrue();
        tab.TextSelection.IsOverText(At(tab, 0, line: 5, column: 3)).ShouldBeFalse();
    }

    private async Task<DocumentTabViewModel> OpenAsync()
    {
        var document = FakePdfDocument.WithPages(Lines.Length);
        document.TextLayers = page => Layer(Lines[page]);
        var tab = _factory.Create(await _fixture.CreateAsync(document));
        tab.Viewer.ViewportSize = new ViewSize(1000, 700);
        foreach (var page in tab.Workspace.Pages.Pages)
        {
            await tab.Editor.GetGeometryAsync(page.Key);
        }

        return tab;
    }

    private static Guid Key(DocumentTabViewModel tab, int page) => tab.Workspace.Pages.Pages[page].Key;

    /// <summary>The viewport point over the middle of character <paramref name="column"/> of a line (lines past the text are blank space).</summary>
    private static ViewPoint At(DocumentTabViewModel tab, int page, int line, int column)
    {
        var layout = tab.Viewer.Layout;
        var bounds = layout.GetPageBounds(page);
        var mapper = new PageCoordinateMapper(tab.Editor.GeometryOf(Key(tab, page)).ShouldNotBeNull(), layout.Scale);
        var shown = mapper.PdfToScreen(new PdfPoint(Left + ((column + 0.5) * CharWidth), Top - (line * LineHeight) + (LineHeight / 2)));
        return new ViewPoint(bounds.X + shown.X - tab.Viewer.ScrollOffset.X, bounds.Y + shown.Y - tab.Viewer.ScrollOffset.Y);
    }

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
}
