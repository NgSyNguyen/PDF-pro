using PdfPro.Core.Organizing;

namespace PdfPro.Core.Tests.Organizing;

public sealed class PageRangesTests
{
    [Theory]
    [InlineData("1", 10, new[] { 0 })]
    [InlineData("1-3", 10, new[] { 0, 1, 2 })]
    [InlineData("1-3, 7", 10, new[] { 0, 1, 2, 6 })]
    [InlineData("1-5, 8, 10-", 12, new[] { 0, 1, 2, 3, 4, 7, 9, 10, 11 })]
    [InlineData("8-", 10, new[] { 7, 8, 9 })]
    [InlineData("-2", 10, new[] { 0, 1 })]
    [InlineData("5-3", 10, new[] { 2, 3, 4 })]
    [InlineData("2 - 4", 10, new[] { 1, 2, 3 })]
    [InlineData("3–5", 10, new[] { 2, 3, 4 })]
    [InlineData("1 3 5", 10, new[] { 0, 2, 4 })]
    [InlineData("1 3-4", 10, new[] { 0, 2, 3 })]
    [InlineData("1; 2", 10, new[] { 0, 1 })]
    [InlineData("1, 2,", 10, new[] { 0, 1 })]
    [InlineData("4, 1, 1, 2", 10, new[] { 0, 1, 3 })]
    [InlineData("odd", 5, new[] { 0, 2, 4 })]
    [InlineData("even", 5, new[] { 1, 3 })]
    [InlineData("lẻ", 5, new[] { 0, 2, 4 })]
    [InlineData("Chẵn", 5, new[] { 1, 3 })]
    [InlineData("ALL", 3, new[] { 0, 1, 2 })]
    [InlineData("tất cả", 3, new[] { 0, 1, 2 })]
    [InlineData("even, 1", 5, new[] { 0, 1, 3 })]
    public void TryParse_ReadsWhatPeopleType(string text, int pageCount, int[] expected)
    {
        PageRanges.TryParse(text, pageCount, out var indices, out var error).ShouldBeTrue();

        error.ShouldBeNull();
        indices.ShouldBe(expected);
    }

    [Fact]
    public void TryParse_NormalizesDecomposedVietnamese()
    {
        // "lẻ" typed as l + e + combining hook above (NFD).
        PageRanges.TryParse("lẻ", 4, out var indices, out _).ShouldBeTrue();

        indices.ShouldBe([0, 2]);
    }

    [Theory]
    [InlineData("", PageRangeErrorKind.Empty)]
    [InlineData("   ", PageRangeErrorKind.Empty)]
    [InlineData(",;", PageRangeErrorKind.Empty)]
    [InlineData("abc", PageRangeErrorKind.InvalidSyntax)]
    [InlineData("1-a", PageRangeErrorKind.InvalidSyntax)]
    [InlineData("-", PageRangeErrorKind.InvalidSyntax)]
    [InlineData("1.5", PageRangeErrorKind.InvalidSyntax)]
    [InlineData("0", PageRangeErrorKind.OutOfRange)]
    [InlineData("6", PageRangeErrorKind.OutOfRange)]
    [InlineData("4-6", PageRangeErrorKind.OutOfRange)]
    [InlineData("7-", PageRangeErrorKind.OutOfRange)]
    [InlineData("99999999999", PageRangeErrorKind.OutOfRange)]
    public void TryParse_ExplainsWhatIsWrong(string text, PageRangeErrorKind kind)
    {
        PageRanges.TryParse(text, 5, out var indices, out var error).ShouldBeFalse();

        indices.ShouldBeNull();
        error.ShouldNotBeNull().Kind.ShouldBe(kind);
    }

    [Fact]
    public void TryParse_ReportsTheOffendingPart()
    {
        PageRanges.TryParse("1-2, 9, 3", 5, out _, out var error).ShouldBeFalse();

        error.ShouldBe(new PageRangeError(PageRangeErrorKind.OutOfRange, "9"));
    }

    [Fact]
    public void TryParse_KeywordMatchingNoPage_IsEmpty()
    {
        PageRanges.TryParse("even", 1, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().Kind.ShouldBe(PageRangeErrorKind.Empty);
    }

    [Fact]
    public void TryParse_NeedsAPageCount()
        => Should.Throw<ArgumentOutOfRangeException>(() => PageRanges.TryParse("1", 0, out _, out _));

    [Fact]
    public void Parse_ThrowsOnInvalidText()
    {
        PageRanges.Parse("2-3", 5).ShouldBe([1, 2]);
        Should.Throw<FormatException>(() => PageRanges.Parse("x", 5));
    }

    [Theory]
    [InlineData(new int[0], "")]
    [InlineData(new[] { 0 }, "1")]
    [InlineData(new[] { 0, 1, 2, 6 }, "1-3, 7")]
    [InlineData(new[] { 6, 0, 2, 1, 1 }, "1-3, 7")]
    [InlineData(new[] { 3, 5 }, "4, 6")]
    [InlineData(new[] { 8, 9 }, "9-10")]
    public void Format_WritesCompactOneBasedRanges(int[] indices, string expected)
        => PageRanges.Format(indices).ShouldBe(expected);

    [Fact]
    public void Format_RejectsNegativeIndices()
        => Should.Throw<ArgumentOutOfRangeException>(() => PageRanges.Format([-1, 2]));

    [Fact]
    public void FormatThenParse_RoundTrips()
    {
        int[] indices = [0, 2, 3, 4, 9, 11, 12];

        PageRanges.Parse(PageRanges.Format(indices), 13).ShouldBe(indices);
    }
}
