using PdfPro.Core.Structure;

namespace PdfPro.Core.Tests.Structure;

/// <summary>Page labels as a reader shows them: roman front matter, numbered body, letters and prefixes.</summary>
public sealed class PageLabelsTests
{
    [Fact]
    public void EachPage_TakesTheLabelOfItsRange()
    {
        PageLabelRange[] ranges = [new(0, PageLabelStyle.UpperRoman), new(3, Start: 10), new(5, PageLabelStyle.LowerLetters, "PL-"), new(6, PageLabelStyle.None, "Bìa sau")];

        Enumerable.Range(0, 7).Select(p => PageLabels.Of(ranges, p)).ShouldBe(["I", "II", "III", "10", "11", "PL-a", "Bìa sau"]);
        PageLabels.Of([], 0).ShouldBeNull();
        PageLabels.Of([new PageLabelRange(2)], 1).ShouldBeNull("no range covers it");
    }

    [Theory]
    [InlineData(1, "A")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(53, "AAA")]
    public void Letters_RepeatAfterZ(int number, string label) => PageLabels.Number(number, PageLabelStyle.UpperLetters).ShouldBe(label);

    [Fact]
    public void Ranges_AreSortedKeptInsideTheDocument_AndOnePerPage()
        => PageLabels.Normalize([new(4), new(0, PageLabelStyle.LowerRoman, ""), new(4, PageLabelStyle.UpperLetters, Start: 0), new(9)], 5)
            .ShouldBe([new PageLabelRange(0, PageLabelStyle.LowerRoman), new PageLabelRange(4, PageLabelStyle.UpperLetters, Start: 1)]);
}
