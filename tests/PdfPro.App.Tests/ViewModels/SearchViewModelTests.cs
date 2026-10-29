using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Infrastructure.Settings;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Tìm kiếm": matches listed with context, highlighted, stepped through, and searched again when needed.</summary>
public sealed class SearchViewModelTests : IDisposable
{
    private static readonly string[] Pages =
    [
        "Ủy ban nhân dân tỉnh Hòa Bình",
        string.Empty, // a scanned page: no text layer
        "hoa hồng và hóa đơn",
    ];

    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly DocumentTabFactory _factory;
    private FakePdfDocument? _document;

    public SearchViewModelTests()
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

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task AQueryWithoutMarks_FindsEveryVariant_ListsThemWithContext_AndShowsTheFirst()
    {
        await using var tab = await OpenAsync();
        var search = tab.Search;
        search.Query = "hoa";

        await search.SearchCommand.ExecuteAsync(null);

        search.Results.Select(r => (r.PageIndex, r.Snippet.Match)).ShouldBe([(0, "Hòa"), (2, "hoa"), (2, "hóa")]);
        search.Results[0].Snippet.ShouldBe(new SearchSnippet("Ủy ban nhân dân tỉnh ", "Hòa", " Bình"));
        search.Results[0].PageLabel.ShouldBe(Format(Strings.Search_ResultPage, 1));
        search.StatusText.ShouldBe(Format(Strings.Search_Done, 3, 2));
        search.ScannedPagesText.ShouldBe(Format(Strings.Search_ScannedPages, 1));
        search.IsSearching.ShouldBeFalse();

        search.SelectedResult.ShouldBeSameAs(search.Results[0]);
        (await tab.TextSelection.GetTextAsync(Token)).ShouldBe("Hòa", "the current match is selected, so Ctrl+C copies it");
        tab.Viewer.CurrentPageIndex.ShouldBe(0);
        search.MatchRectsOn(Key(tab, 0)).ShouldHaveSingleItem();
        search.MatchRectsOn(Key(tab, 2)).Count.ShouldBe(2);
        search.CurrentRectsOn(Key(tab, 0)).ShouldHaveSingleItem();
        search.CurrentRectsOn(Key(tab, 2)).ShouldBeEmpty();
    }

    [Fact]
    public async Task NextAndPrevious_StepThroughTheMatches_AndGoRound()
    {
        await using var tab = await OpenAsync();
        var search = tab.Search;
        search.Query = "hoa";

        await search.NextCommand.ExecuteAsync(null); // a new query: searches and shows the first match
        search.SelectedResult.ShouldBeSameAs(search.Results[0]);

        await search.NextCommand.ExecuteAsync(null);
        search.SelectedResult.ShouldBeSameAs(search.Results[1]);
        await WaitUntilAsync(() => tab.Viewer.CurrentPageIndex == 2);
        (await tab.TextSelection.GetTextAsync(Token)).ShouldBe("hoa");

        await search.NextCommand.ExecuteAsync(null);
        await search.NextCommand.ExecuteAsync(null);
        search.SelectedResult.ShouldBeSameAs(search.Results[0], "after the last match comes the first");

        await search.PreviousCommand.ExecuteAsync(null);
        search.SelectedResult.ShouldBeSameAs(search.Results[2], "before the first comes the last");
    }

    [Fact]
    public async Task ChangingAnOption_SearchesAgain()
    {
        await using var tab = await OpenAsync();
        var search = tab.Search;
        search.Query = "Hòa";
        search.Diacritics = DiacriticsMatching.Ignore;
        await search.SearchCommand.ExecuteAsync(null);
        search.Results.Count.ShouldBe(3);

        search.Diacritics = DiacriticsMatching.Match;
        await WaitUntilAsync(() => !search.IsSearching && search.StatusText == Format(Strings.Search_Done, 1, 1));

        search.Results.ShouldHaveSingleItem().Snippet.Match.ShouldBe("Hòa");
    }

    [Fact]
    public async Task NothingFound_SaysSo()
    {
        await using var tab = await OpenAsync();
        tab.Search.Query = "quyết định";

        await tab.Search.SearchCommand.ExecuteAsync(null);

        tab.Search.Results.ShouldBeEmpty();
        tab.Search.SelectedResult.ShouldBeNull();
        tab.Search.StatusText.ShouldBe(Strings.Search_NoMatches);
    }

    [Fact]
    public async Task AnInvalidExpression_IsExplained()
    {
        await using var tab = await OpenAsync();
        tab.Search.Query = "Hòa(";
        tab.Search.UseRegex = true;

        await tab.Search.SearchCommand.ExecuteAsync(null);

        tab.Search.ErrorText.ShouldBe(Strings.Search_Error_Invalid);
        tab.Search.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stop_EndsTheSearch()
    {
        await using var tab = await OpenAsync();
        _document!.TextGate = new TaskCompletionSource(); // reading text waits
        tab.Search.Query = "hoa";

        var running = tab.Search.SearchCommand.ExecuteAsync(null);
        tab.Search.IsSearching.ShouldBeTrue();
        tab.Search.StopCommand.Execute(null);
        await running;

        tab.Search.IsSearching.ShouldBeFalse();
        tab.Search.StatusText.ShouldBe(Strings.Search_Stopped);
        tab.Search.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task ChangingThePages_SearchesAgain_AndClosingRemovesTheHighlights()
    {
        await using var tab = await OpenAsync();
        tab.Search.Query = "hoa";
        await tab.Search.SearchCommand.ExecuteAsync(null);

        tab.Viewer.NavigateTo(2);
        tab.DeletePagesCommand.Execute(null); // the last page and its two matches go

        await WaitUntilAsync(() => !tab.Search.IsSearching && tab.Search.StatusText == Format(Strings.Search_Done, 1, 1));
        tab.Search.Results.ShouldHaveSingleItem().PageIndex.ShouldBe(0);

        tab.Search.ClearCommand.Execute(null);
        tab.Search.Results.ShouldBeEmpty();
        tab.Search.MatchRectsOn(Key(tab, 0)).ShouldBeEmpty();
        tab.Search.StatusText.ShouldBeNull();
    }

    private async Task<DocumentTabViewModel> OpenAsync()
    {
        _document = FakePdfDocument.WithPages(Pages.Length);
        _document.TextLayers = page => Layer(Pages[page]);
        var tab = _factory.Create(await _fixture.CreateAsync(_document));
        tab.Viewer.ViewportSize = new ViewSize(1000, 700);
        return tab;
    }

    private static Guid Key(DocumentTabViewModel tab, int page) => tab.Workspace.Pages.Pages[page].Key;

    private static PageText Layer(string text)
        => new([.. text.Select((c, i) => new TextChar(c.ToString(), new PdfRect(72 + (i * 6), 700, 78 + (i * 6), 712)))]);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the search to finish");
            await Task.Delay(10, Token);
        }
    }

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
