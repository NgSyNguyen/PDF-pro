using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Sessions;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Trình chiếu": a page on the whole screen, the next ahead of time, the keys of a slide show.</summary>
public sealed class PresentationViewModelTests
{
    private readonly FakePageImageRenderer _renderer = new() { AutoComplete = true };

    [Fact]
    public async Task ItStartsAtThePageBeingRead_RenderedToFillTheScreen_TheNextOneAhead()
    {
        var presentation = Create(FakePdfDocument.WithPages(4), start: 1);

        await presentation.SetScreenAsync(1920, 1080);

        presentation.PageIndex.ShouldBe(1);
        presentation.CounterText.ShouldBe("2 / 4");
        presentation.Image.ShouldNotBeNull();
        presentation.ShowsCounter.ShouldBeTrue("the page number shows at the start");
        _renderer.Calls.Select(c => c.PageIndex).ShouldBe([1, 2], "the page, then the next one ahead");
        var page = FakePdfDocument.A4;
        _renderer.Calls[0].Options.Scale.ShouldBe(Math.Min(1920 / page.Width, 1080 / page.Height), 1e-9);
    }

    [Fact]
    public async Task Moving_UsesThePageRenderedAhead_AndRendersTheNext()
    {
        var presentation = Create(FakePdfDocument.WithPages(4), start: 0);
        await presentation.SetScreenAsync(1280, 800);
        _renderer.Calls.Clear();

        await presentation.NextCommand.ExecuteAsync(null);

        presentation.PageIndex.ShouldBe(1);
        _renderer.Calls.Select(c => c.PageIndex).ShouldBe([2], "page 2 was ready; page 3 is rendered ahead");

        await presentation.PreviousCommand.ExecuteAsync(null);
        await presentation.LastCommand.ExecuteAsync(null);
        presentation.PageIndex.ShouldBe(3);
        await presentation.FirstCommand.ExecuteAsync(null);
        presentation.PageIndex.ShouldBe(0);
        await presentation.PreviousCommand.ExecuteAsync(null);
        presentation.PageIndex.ShouldBe(0, "nothing before the first page");
    }

    [Fact]
    public async Task PastTheLastPage_OrWithEscape_ThePresentationEnds()
    {
        var presentation = Create(FakePdfDocument.WithPages(2), start: 1);
        var ended = 0;
        presentation.CloseRequested += (_, _) => ended++;
        await presentation.SetScreenAsync(1280, 800);

        await presentation.NextCommand.ExecuteAsync(null);
        presentation.CloseCommand.Execute(null);

        ended.ShouldBe(2);
        presentation.PageIndex.ShouldBe(1);
    }

    [Fact]
    public async Task ABlankPage_IsWhite_WithoutRendering()
    {
        var plan = PagePlan.Identity(SourceIds.Original, 1, RandomPageKeys.Instance).InsertBlank(1, new PdfSize(595, 842), RandomPageKeys.Instance);
        var presentation = new PresentationViewModel(VirtualDocuments.Of(plan, FakePdfDocument.WithPages(1)), _renderer, 1, NullLogger.Instance);

        await presentation.SetScreenAsync(1000, 1000);

        presentation.Image.ShouldNotBeNull().Height.ShouldBe(1000, 1);
        _renderer.Calls.ShouldBeEmpty();
    }

    private PresentationViewModel Create(FakePdfDocument document, int start)
        => new(VirtualDocuments.Of(document), _renderer, start, NullLogger.Instance);
}
