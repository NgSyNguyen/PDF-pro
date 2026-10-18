using PdfPro.Core.Printing;

namespace PdfPro.Core.Tests.Printing;

public sealed class PrintPageRangeTests
{
    [Fact]
    public void All_ListsEveryPage()
    {
        PrintPageRange.All(4).ShouldBe([0, 1, 2, 3]);
        PrintPageRange.All(0).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(2, 4, new[] { 1, 2, 3 })]
    [InlineData(4, 2, new[] { 1, 2, 3 })]
    [InlineData(5, 5, new[] { 4 })]
    [InlineData(0, 2, new[] { 0, 1 })]
    [InlineData(9, 50, new[] { 8, 9 })]
    [InlineData(40, 50, new[] { 9 })]
    public void Between_IsOneBasedInclusiveAndClamped(int first, int last, int[] expected)
    {
        PrintPageRange.Between(first, last, pageCount: 10).ShouldBe(expected);
    }

    [Fact]
    public void Between_OnAnEmptyDocument_IsEmpty()
    {
        PrintPageRange.Between(1, 3, 0).ShouldBeEmpty();
    }
}
