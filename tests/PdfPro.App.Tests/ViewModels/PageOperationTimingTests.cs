using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.Core.Geometry;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>
/// SPEC P3 acceptance: turning, deleting, moving or duplicating 200 pages of a 1,000-page document takes under 50 ms,
/// from the command to every view model (viewer, sidebar thumbnails, organizer grid) showing the new plan.
/// </summary>
[Trait("Category", "Performance")]
public sealed class PageOperationTimingTests(ITestOutputHelper output) : IDisposable
{
    private static readonly ViewSize Viewport = new(1000, 700);

    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task TwoHundredPagesOfAThousand_TakeUnder50Milliseconds()
    {
        var factory = new DocumentTabFactory(
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
        await using var tab = factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(1000)));
        tab.Viewer.ViewportSize = Viewport;
        tab.ToggleOrganizeModeCommand.Execute(null);
        var organizer = tab.Organizer!;

        var timings = new Dictionary<string, List<double>>
        {
            ["rotate"] = [],
            ["move"] = [],
            ["duplicate"] = [],
            ["delete"] = [],
        };

        for (var round = 0; round < 7; round++)
        {
            organizer.Select(Enumerable.Range(300, 200));
            timings["rotate"].Add(Time(() => tab.RotateRightCommand.Execute(null)));
            timings["move"].Add(Time(() => tab.MovePages([.. Enumerable.Range(300, 200)], 0).ShouldBeTrue()));
            organizer.Select(Enumerable.Range(0, 200));
            timings["duplicate"].Add(Time(() => tab.DuplicatePagesCommand.Execute(null)));
            timings["delete"].Add(Time(() => tab.DeletePagesCommand.Execute(null)));
            tab.Viewer.PageCount.ShouldBe(1000);
        }

        foreach (var (name, values) in timings)
        {
            var median = values.Order().ElementAt(values.Count / 2);
            output.WriteLine($"{name} 200 of 1000 pages: median {median:0.0} ms (all: {string.Join(", ", values.Select(v => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))})");
            median.ShouldBeLessThan(50, name);
        }

        tab.Workspace.Session.History.UndoCount.ShouldBe(28);
    }

    private static double Time(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed.TotalMilliseconds;
    }
}
