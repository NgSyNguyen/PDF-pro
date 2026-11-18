using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.Documents;

/// <summary>Page text layers read once, kept for the most recent pages, and read again after a failure.</summary>
public sealed class TextLayerCacheTests : IDisposable
{
    private readonly WorkspaceFixture _fixture = new();
    private readonly List<int> _reads = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task APage_IsReadOnce()
    {
        var pages = await PagesAsync(3);
        var cache = new TextLayerCache();

        cache.TryGet(pages[1]).ShouldBeNull("not read yet");
        var first = await cache.GetAsync(pages[1], cancellationToken: Token);
        var second = await cache.GetAsync(pages[1], cancellationToken: Token);

        second.ShouldBeSameAs(first);
        cache.TryGet(pages[1]).ShouldBeSameAs(first);
        _reads.ShouldBe([1]);
        first.Text.ShouldBe("trang 2");
    }

    [Fact]
    public async Task OnlyTheMostRecentPages_AreKept()
    {
        var pages = await PagesAsync(3);
        var cache = new TextLayerCache(capacity: 2);

        await cache.GetAsync(pages[0], cancellationToken: Token);
        await cache.GetAsync(pages[1], cancellationToken: Token);
        cache.TryGet(pages[0]).ShouldNotBeNull(); // page 1 is now the oldest
        await cache.GetAsync(pages[2], cancellationToken: Token);

        cache.TryGet(pages[1]).ShouldBeNull();
        cache.TryGet(pages[0]).ShouldNotBeNull();
        cache.TryGet(pages[2]).ShouldNotBeNull();
    }

    [Fact]
    public async Task AFailedRead_IsTriedAgain()
    {
        var failOnce = true;
        var pages = await PagesAsync(1, page =>
        {
            if (failOnce)
            {
                failOnce = false;
                throw new PdfDocumentException(PdfOpenFailure.ReadFailed, "unreadable");
            }

            return Layer("trang 1");
        });
        var cache = new TextLayerCache();

        await Should.ThrowAsync<PdfDocumentException>(() => cache.GetAsync(pages[0], cancellationToken: Token));
        cache.TryGet(pages[0]).ShouldBeNull();

        (await cache.GetAsync(pages[0], cancellationToken: Token)).Text.ShouldBe("trang 1");
    }

    private async Task<IReadOnlyList<VirtualPage>> PagesAsync(int count, Func<int, PageText>? layers = null)
    {
        var document = FakePdfDocument.WithPages(count);
        document.TextLayers = page =>
        {
            _reads.Add(page);
            return layers?.Invoke(page) ?? Layer($"trang {page + 1}");
        };
        var workspace = await _fixture.CreateAsync(document);
        return workspace.Pages.Pages;
    }

    private static PageText Layer(string text)
        => new([.. text.Select((c, i) => new TextChar(c.ToString(), new PdfRect(i * 5, 700, (i * 5) + 5, 710)))]);
}
