using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.Documents;

public sealed class VirtualDocumentTests
{
    private static readonly PdfSize Page = new(200, 300);

    private readonly FakePdfDocument _source = new([Page, Page, new PdfSize(300, 200)]);

    [Fact]
    public void Create_ResolvesEveryPageOfThePlan()
    {
        var plan = Identity().Move([2], 0).InsertBlank(1, new PdfSize(50, 80), RandomPageKeys.Instance);

        var document = VirtualDocuments.Of(plan, _source);

        document.PageCount.ShouldBe(4);
        document.Pages.Select(p => p.IsBlank ? -1 : p.SourcePageIndex).ShouldBe([2, -1, 0, 1]);
        document.PageSizes.ShouldBe([new PdfSize(300, 200), new PdfSize(50, 80), Page, Page]);
        document.Pages[1].Source.ShouldBeNull();
        document.IndexOf(plan[3].PageKey).ShouldBe(3);
        document.FirstIndexOf(_source, 0).ShouldBe(2);
        document.FirstIndexOf(_source, 9).ShouldBe(-1);
        document.SourceDocument(SourceIds.Original).ShouldBeSameAs(_source);
    }

    [Fact]
    public void Create_RejectsPagesOfMissingSources()
    {
        var plan = Identity().InsertFromSource(0, "s1", [0], RandomPageKeys.Instance);

        Should.Throw<ArgumentException>(() => VirtualDocuments.Of(plan, _source));
        Should.Throw<ArgumentException>(() => VirtualDocuments.Of(PagePlan.Identity(SourceIds.Original, 4, RandomPageKeys.Instance), _source));
    }

    [Fact]
    public void RenderOptions_OfAPlainPage_TurnItAsThePlanSays()
    {
        var page = VirtualDocuments.Of(Identity().Rotate([0], 90), _source).Pages[0];

        var options = page.RenderOptions(2, new PixelRect(1, 2, 3, 4), PdfRenderFlags.Annotations);

        options.ExtraQuarterTurns.ShouldBe(1);
        options.Region.ShouldBe(new PixelRect(1, 2, 3, 4));
        options.Scale.ShouldBe(2);
        page.DisplaySize.ShouldBe(new PdfSize(300, 200));
        page.RenderOptions(1, null, PdfRenderFlags.Printing, extraQuarterTurns: 3).ExtraQuarterTurns.ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 20, 30, 100, 150)]
    [InlineData(1, 120, 20, 150, 100)]
    [InlineData(2, 80, 120, 100, 150)]
    [InlineData(3, 30, 80, 150, 100)]
    public void RenderOptions_OfACroppedPage_RenderOnlyTheKeptArea(int quarterTurns, int x, int y, int width, int height)
    {
        // Page 200 × 300 pt, crop (20, 30) 100 × 150 pt, at 1 pixel per point.
        var plan = Identity().Crop([0], new PageCrop(20, 30, 100, 150)).Rotate([0], quarterTurns * 90);
        var page = VirtualDocuments.Of(plan, _source).Pages[0];

        var whole = page.RenderOptions(1, null, PdfRenderFlags.Annotations);
        var part = page.RenderOptions(1, new PixelRect(5, 6, 10, 12), PdfRenderFlags.Annotations);

        whole.Region.ShouldBe(new PixelRect(x, y, width, height));
        whole.ExtraQuarterTurns.ShouldBe(quarterTurns);
        part.Region.ShouldBe(new PixelRect(x + 5, y + 6, 10, 12));
        page.DisplaySize.ShouldBe(quarterTurns % 2 == 0 ? new PdfSize(100, 150) : new PdfSize(150, 100));
    }

    [Theory]
    [InlineData(0, 30, 50)]
    [InlineData(1, 100, 30)]
    [InlineData(2, 70, 100)]
    [InlineData(3, 50, 70)]
    public void FromSourcePoint_FollowsCropAndRotation(int quarterTurns, double x, double y)
    {
        // A point 30 pt right and 50 pt down from the crop's corner (20, 30); crop 100 × 150 pt.
        var plan = Identity().Crop([0], new PageCrop(20, 30, 100, 150)).Rotate([0], quarterTurns * 90);
        var page = VirtualDocuments.Of(plan, _source).Pages[0];

        page.FromSourcePoint(new ViewPoint(50, 80)).ShouldBe(new ViewPoint(x, y));
    }

    [Fact]
    public void FromSourcePoint_OutsideTheCrop_StaysOnItsEdge()
    {
        var page = VirtualDocuments.Of(Identity().Crop([0], new PageCrop(20, 30, 100, 150)), _source).Pages[0];

        page.FromSourcePoint(new ViewPoint(0, 500)).ShouldBe(new ViewPoint(0, 150));
    }

    private static PagePlan Identity() => PagePlan.Identity(SourceIds.Original, 3, RandomPageKeys.Instance);
}
