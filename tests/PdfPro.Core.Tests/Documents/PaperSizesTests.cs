using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;

namespace PdfPro.Core.Tests.Documents;

public sealed class PaperSizesTests
{
    [Theory]
    [InlineData(595.276, 841.89, "A4", false)]
    [InlineData(841.89, 595.276, "A4", true)]
    [InlineData(595, 842, "A4", false)]
    [InlineData(612, 792, "Letter", false)]
    [InlineData(792, 612, "Letter", true)]
    [InlineData(612, 1008, "Legal", false)]
    [InlineData(841.89, 1190.55, "A3", false)]
    [InlineData(419.53, 595.28, "A5", false)]
    [InlineData(2383.94, 3370.39, "A0", false)]
    [InlineData(708.66, 1000.63, "B4", false)]
    [InlineData(792, 1224, "Tabloid", false)]
    public void Identify_RecognisesStandardFormatsInBothOrientations(double width, double height, string name, bool landscape)
    {
        var paper = PaperSizes.Identify(new PdfSize(width, height));

        paper.ShouldNotBeNull();
        paper.Name.ShouldBe(name);
        paper.IsLandscape.ShouldBe(landscape);
    }

    [Theory]
    [InlineData(600, 800)]
    [InlineData(595, 846)] // 4 pt off on one side: a different (custom) size
    [InlineData(100, 100)]
    [InlineData(0, 0)]
    public void Identify_ReturnsNullForCustomSizes(double width, double height)
        => PaperSizes.Identify(new PdfSize(width, height)).ShouldBeNull();

    [Fact]
    public void Identify_ToleratesProducerRounding()
    {
        PaperSizes.Identify(new PdfSize(597.5, 839.5))!.Name.ShouldBe("A4");
        PaperSizes.Identify(new PdfSize(598.5, 842)).ShouldBeNull();
    }

    [Theory]
    [InlineData("A4", 595, 842)]
    [InlineData("a3", 842, 1191)]
    [InlineData("Letter", 612, 792)]
    [InlineData("LEGAL", 612, 1008)]
    public void SizeOf_GivesThePortraitSize(string name, double width, double height)
        => PaperSizes.SizeOf(name).ShouldBe(new PdfSize(width, height));

    [Theory]
    [InlineData("A10")]
    [InlineData("")]
    public void SizeOf_UnknownFormat_IsNull(string name) => PaperSizes.SizeOf(name).ShouldBeNull();

    [Fact]
    public void SizeOf_IsRecognisedAgain()
    {
        foreach (var name in new[] { "A3", "A4", "A5", "Letter", "Legal" })
        {
            PaperSizes.Identify(PaperSizes.SizeOf(name)!.Value)!.Name.ShouldBe(name);
        }
    }

    [Fact]
    public void ToMillimetres_ConvertsPoints()
    {
        PaperSizes.ToMillimetres(72).ShouldBe(25.4, 1e-9);
        Math.Round(PaperSizes.ToMillimetres(595.276)).ShouldBe(210);
        Math.Round(PaperSizes.ToMillimetres(841.89)).ShouldBe(297);
    }
}
