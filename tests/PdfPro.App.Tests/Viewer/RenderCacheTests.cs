using System.Windows.Media.Imaging;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.Tests.Viewer;

public sealed class RenderCacheTests
{
    private const long MB = 1024 * 1024;

    [Fact]
    public async Task Budget_FollowsTheSettings()
    {
        var settings = new FakeSettingsService(AppSettings.Default with { RenderCacheMegabytes = 256 });
        using var cache = new RenderCache(settings);
        cache.BudgetBytes.ShouldBe(256 * MB);

        await settings.UpdateAsync(s => s with { RenderCacheMegabytes = 1024 }, TestContext.Current.CancellationToken);

        cache.BudgetBytes.ShouldBe(1024 * MB);
    }

    [Fact]
    public void RemoveDocument_DropsOnlyThatDocument()
    {
        using var cache = new RenderCache(new FakeSettingsService(AppSettings.Default));
        var closed = Guid.NewGuid();
        var open = Guid.NewGuid();
        var image = FakePageImageRenderer.CreateImage();
        cache.Add(new RenderKey(closed, 0, 1000), image, MB);
        cache.Add(new RenderKey(closed, 1, 1000), image, MB);
        cache.Add(new RenderKey(open, 0, 1000), image, MB);

        cache.RemoveDocument(closed).ShouldBe(2);

        cache.Count.ShouldBe(1);
        cache.TryGet(new RenderKey(open, 0, 1000), out _).ShouldBeTrue();
    }

    [Fact]
    public void DisplayImage_IsAViewOfTheCachedBitmapWithoutCopyingIt()
    {
        var cached = FakePageImageRenderer.CreateImage();

        var shown = DisplayImage.Of(cached);

        shown.ShouldNotBeSameAs(cached, "the cached bitmap itself is never drawn");
        shown.IsFrozen.ShouldBeTrue();
        DisplayImage.SourceOf(shown).ShouldBeSameAs(cached);
        ((BitmapSource)shown).PixelWidth.ShouldBe(((BitmapSource)cached).PixelWidth);
        DisplayImage.Of(cached).ShouldNotBeSameAs(shown, "each page gets its own view");
    }

    [Theory]
    [InlineData(1.0, 1000)]
    [InlineData(1.6667, 1667)]
    [InlineData(2.00049, 2000)]
    public void ScaleKey_RoundsToThousandths(double scale, int expected) => RenderKey.ToScaleKey(scale).ShouldBe(expected);
}
