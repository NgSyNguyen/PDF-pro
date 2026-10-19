using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;

namespace PdfPro.Core.Tests.Organizing;

public sealed class PageRefTests
{
    private static readonly Guid Key = SequentialKeys.Key(1);

    [Theory]
    [InlineData("")]
    [InlineData("S0")]
    [InlineData("0s")]
    [InlineData("s 1")]
    [InlineData("blank")]
    public void Constructor_RejectsInvalidSourceIds(string sourceId)
        => Should.Throw<ArgumentException>(() => new PageRef(sourceId, 0, 0, null, Key));

    [Fact]
    public void Constructor_RejectsNegativeIndexOddRotationAndEmptyKey()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new PageRef("s0", -1, 0, null, Key));
        Should.Throw<ArgumentOutOfRangeException>(() => new PageRef("s0", 0, 45, null, Key));
        Should.Throw<ArgumentException>(() => new PageRef("s0", 0, 0, null, Guid.Empty));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(-360, 0)]
    public void Constructor_NormalizesTheRotation(int degrees, int expected)
    {
        var page = new PageRef("s0", 3, degrees, null, Key);

        page.RotationDelta.ShouldBe(expected);
        page.QuarterTurns.ShouldBe(expected / 90);
    }

    [Fact]
    public void Blank_HasTheReservedSourceAndASize()
    {
        var page = PageRef.Blank(new PdfSize(595, 842), Key);

        page.IsBlank.ShouldBeTrue();
        page.SourceId.ShouldBe(SourceIds.Blank);
        page.SourcePageIndex.ShouldBe(0);
        page.BlankSize.ShouldBe(new PdfSize(595, 842));
        new PageRef("s0", 0, 0, null, Key).IsBlank.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, 842)]
    [InlineData(595, -1)]
    [InlineData(double.NaN, 842)]
    public void Blank_RejectsInvalidSizes(double width, double height)
        => Should.Throw<ArgumentOutOfRangeException>(() => PageRef.Blank(new PdfSize(width, height), Key));

    [Fact]
    public void Edits_ReturnTheSameInstanceWhenNothingChanges()
    {
        var page = new PageRef("s0", 1, 90, new PageCrop(1, 2, 30, 40), Key);

        page.WithRotation(450).ShouldBeSameAs(page);
        page.WithCrop(new PageCrop(1, 2, 30, 40)).ShouldBeSameAs(page);
        page.WithKey(Key).ShouldBeSameAs(page);
    }

    [Fact]
    public void Edits_ChangeOnlyTheirOwnValue()
    {
        var page = new PageRef("s1", 4, 90, null, Key);
        var otherKey = SequentialKeys.Key(2);

        page.WithRotation(-90).ShouldBe(new PageRef("s1", 4, 270, null, Key));
        page.WithCrop(new PageCrop(0, 0, 50, 50)).ShouldBe(new PageRef("s1", 4, 90, new PageCrop(0, 0, 50, 50), Key));
        page.WithKey(otherKey).ShouldBe(new PageRef("s1", 4, 90, null, otherKey));
        Should.Throw<ArgumentOutOfRangeException>(() => page.WithRotation(10));
    }

    [Fact]
    public void Edits_KeepBlankPagesBlank()
    {
        var blank = PageRef.Blank(new PdfSize(100, 200), Key);

        var edited = blank.WithRotation(90).WithKey(SequentialKeys.Key(9));

        edited.IsBlank.ShouldBeTrue();
        edited.BlankSize.ShouldBe(new PdfSize(100, 200));
    }

    [Theory]
    [InlineData(0, false, 200, 300)]
    [InlineData(90, false, 300, 200)]
    [InlineData(180, false, 200, 300)]
    [InlineData(270, true, 150, 100)]
    [InlineData(0, true, 100, 150)]
    public void GetDisplaySize_AppliesCropThenRotation(int rotation, bool cropped, double width, double height)
    {
        var page = new PageRef("s0", 0, rotation, cropped ? new PageCrop(10, 10, 100, 150) : null, Key);

        page.GetDisplaySize(new PdfSize(200, 300)).ShouldBe(new PdfSize(width, height));
    }

    [Fact]
    public void GetDisplaySize_OfABlankPage_UsesItsOwnSize()
    {
        var page = PageRef.Blank(new PdfSize(100, 200), Key).WithRotation(90);

        page.GetBaseSize(new PdfSize(1, 1)).ShouldBe(new PdfSize(100, 200));
        page.GetDisplaySize(new PdfSize(1, 1)).ShouldBe(new PdfSize(200, 100));
    }

    [Fact]
    public void Equality_ComparesAllValues()
    {
        new PageRef("s0", 2, 90, null, Key).ShouldBe(new PageRef("s0", 2, -270, null, Key));
        new PageRef("s0", 2, 90, null, Key).ShouldNotBe(new PageRef("s0", 2, 90, null, SequentialKeys.Key(2)));
    }
}
