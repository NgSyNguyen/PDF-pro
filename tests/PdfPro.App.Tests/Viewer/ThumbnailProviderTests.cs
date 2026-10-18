using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Geometry;
using PdfPro.Infrastructure;
using PdfPro.Infrastructure.Caching;
using PdfPro.Infrastructure.Settings;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.Viewer;

public sealed class ThumbnailProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakePdfDocument _document = FakePdfDocument.WithPages(5);
    private readonly SizedPageImageRenderer _renderer = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly ThumbnailDiskCache _diskCache;
    private readonly string _file;

    public ThumbnailProviderTests()
    {
        _diskCache = new ThumbnailDiskCache(new AppPaths(_temp.Combine("data"), _temp.Combine("tools")), NullLogger<ThumbnailDiskCache>.Instance);
        _file = _temp.Combine("document.pdf");
        File.WriteAllBytes(_file, Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
    }

    public void Dispose()
    {
        _cache.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task FirstLoad_RendersAtLowPriorityAndKeepsTheThumbnailInMemory()
    {
        var provider = CreateProvider(_cache);
        var request = new ThumbnailRequest(_document, _file, 2, DpiScale: 1.5);

        var image = await provider.GetAsync(request, CancellationToken.None);

        var call = _renderer.Calls.ShouldHaveSingleItem();
        call.PageIndex.ShouldBe(2);
        call.Priority.ShouldBe(PdfiumPriority.Low);
        call.Scale.ShouldBe(ThumbnailGeometry.ScaleFor(FakePdfDocument.A4) * 1.5, 1e-9);
        ((BitmapSource)image).PixelWidth.ShouldBe(request.Pixels.Width);
        ((BitmapSource)image).PixelHeight.ShouldBe(request.Pixels.Height);

        provider.TryGetCached(request, out var cached).ShouldBeTrue();
        cached.ShouldBeSameAs(image);
        (await provider.GetAsync(request, CancellationToken.None)).ShouldBeSameAs(image);
        _renderer.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RenderedThumbnail_IsSavedToDiskAndReusedAfterReopening()
    {
        var first = CreateProvider(_cache);
        var request = new ThumbnailRequest(_document, _file, 1, 1.0);
        await first.GetAsync(request, CancellationToken.None);
        await first.WhenWritesCompleteAsync();
        _diskCache.GetSize().ShouldBeGreaterThan(0);

        // The same file opened again (new document instance, empty memory cache).
        var reopened = FakePdfDocument.WithPages(5);
        using var freshCache = new RenderCache(new FakeSettingsService(AppSettings.Default));
        var second = CreateProvider(freshCache);
        var image = await second.GetAsync(new ThumbnailRequest(reopened, _file, 1, 1.0), CancellationToken.None);

        _renderer.Calls.Count.ShouldBe(1, "the second load came from the disk cache");
        ((BitmapSource)image).PixelWidth.ShouldBe(request.Pixels.Width);
        ((BitmapSource)image).PixelHeight.ShouldBe(request.Pixels.Height);
        second.TryGetCached(new ThumbnailRequest(reopened, _file, 1, 1.0), out _).ShouldBeTrue();
    }

    [Fact]
    public async Task AnotherDpi_IsAMissAndRendersAgain()
    {
        var provider = CreateProvider(_cache);
        await provider.GetAsync(new ThumbnailRequest(_document, _file, 0, 1.0), CancellationToken.None);
        var sharper = new ThumbnailRequest(_document, _file, 0, 2.0);

        provider.TryGetCached(sharper, out _).ShouldBeFalse();
        var image = await provider.GetAsync(sharper, CancellationToken.None);

        _renderer.Calls.Count.ShouldBe(2);
        ((BitmapSource)image).PixelWidth.ShouldBe(sharper.Pixels.Width);
        provider.TryGetCached(sharper, out _).ShouldBeTrue("the memory cache keeps the latest size of each page");
    }

    [Fact]
    public async Task EncryptedDocument_NeverWritesToDisk()
    {
        _document.Features = FakePdfDocument.PlainFeatures with { SecurityHandlerRevision = 4 };
        var provider = CreateProvider(_cache);

        await provider.GetAsync(new ThumbnailRequest(_document, _file, 0, 1.0), CancellationToken.None);
        await provider.WhenWritesCompleteAsync();

        _diskCache.GetSize().ShouldBe(0);
        Directory.Exists(_diskCache.RootDirectory).ShouldBeFalse();
    }

    [Fact]
    public async Task EncryptedDocument_DoesNotReadEarlierEntriesEither()
    {
        var plain = CreateProvider(_cache);
        await plain.GetAsync(new ThumbnailRequest(_document, _file, 0, 1.0), CancellationToken.None);
        await plain.WhenWritesCompleteAsync();

        var encrypted = FakePdfDocument.WithPages(5);
        encrypted.Features = FakePdfDocument.PlainFeatures with { SecurityHandlerRevision = 3 };
        using var freshCache = new RenderCache(new FakeSettingsService(AppSettings.Default));
        await CreateProvider(freshCache).GetAsync(new ThumbnailRequest(encrypted, _file, 0, 1.0), CancellationToken.None);

        _renderer.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task UnreadableFile_StillRenders()
    {
        var provider = CreateProvider(_cache);

        var image = await provider.GetAsync(new ThumbnailRequest(_document, _temp.Combine("missing.pdf"), 0, 1.0), CancellationToken.None);
        await provider.WhenWritesCompleteAsync();

        image.ShouldNotBeNull();
        _diskCache.GetSize().ShouldBe(0);
    }

    [Fact]
    public async Task DamagedDiskEntry_IsRenderedAgain()
    {
        var request = new ThumbnailRequest(_document, _file, 3, 1.0);
        var fingerprint = await DocumentFingerprint.ComputeAsync(_file, TestContext.Current.CancellationToken);
        await _diskCache.WriteAsync(fingerprint, 3, request.Pixels.Width, [1, 2, 3, 4, 5], TestContext.Current.CancellationToken);

        var image = await CreateProvider(_cache).GetAsync(request, CancellationToken.None);

        _renderer.Calls.Count.ShouldBe(1);
        ((BitmapSource)image).PixelWidth.ShouldBe(request.Pixels.Width);
    }

    [Fact]
    public async Task Cancellation_ReachesTheRenderer()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateProvider(_cache).GetAsync(new ThumbnailRequest(_document, _file, 0, 1.0), cancellation.Token));
    }

    [Fact]
    public void Geometry_FitsPagesIntoTheThumbnailBox()
    {
        ThumbnailGeometry.SizeFor(new PdfSize(595, 842)).Height.ShouldBe(ThumbnailGeometry.MaxHeight, 1e-9);
        ThumbnailGeometry.SizeFor(new PdfSize(842, 595)).Width.ShouldBe(ThumbnailGeometry.MaxWidth, 1e-9);
        ThumbnailGeometry.SizeFor(new PdfSize(100, 4000)).Height.ShouldBe(ThumbnailGeometry.MaxHeight, 1e-9);
        ThumbnailGeometry.ScaleFor(new PdfSize(0, 100)).ShouldBe(0);
        ThumbnailGeometry.PixelSizeFor(new PdfSize(842, 595), 1.5).Width.ShouldBe(240);
    }

    private ThumbnailProvider CreateProvider(RenderCache cache)
        => new(_renderer, cache, _diskCache, NullLogger<ThumbnailProvider>.Instance);
}
