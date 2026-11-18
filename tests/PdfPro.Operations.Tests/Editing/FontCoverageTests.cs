using System.Text;
using PdfPro.Operations.Editing;

namespace PdfPro.Operations.Tests.Editing;

public sealed class FontCoverageTests
{
    [Fact]
    public void ToUnicode_GivesTheCharactersOfSingleCodesAndRanges_NotLigatures()
    {
        const string cmap = """
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            /CMapName /Adobe-Identity-UCS def
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            3 beginbfchar
            <0003> <0020>
            <0010> <1EA0>
            <0011> <00660069>
            endbfchar
            2 beginbfrange
            <0020> <0022> <0041>
            <0030> <0031> [<0111> <D835DC00>]
            endbfrange
            endcmap
            """;

        var covered = FontCoverage.ParseToUnicode(Encoding.ASCII.GetBytes(cmap));

        covered.ShouldBe([' ', 'Ạ', 'A', 'B', 'C', 'đ', 0x1D400], ignoreOrder: true);
    }

    [Fact]
    public void ABrokenCMap_GivesWhatCanBeRead()
    {
        var covered = FontCoverage.ParseToUnicode(Encoding.ASCII.GetBytes("1 beginbfchar <0003> <00"));

        covered.ShouldBeEmpty();
    }
}
