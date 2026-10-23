using PdfPro.Core.Overlay;
using PdfPro.Core.Text;

namespace PdfPro.Core.Tests.Text;

public sealed class TextLayoutTests
{
    // 1000 units per em; every character is 500 units wide (spaces 250): at 10 pt a letter is 5 pt, a space 2.5 pt.
    private static readonly FakeMetrics Metrics = new();

    [Fact]
    public void ShortText_IsOneLine_WithTheBaselineAtTheAscent()
    {
        var layout = TextLayout.Layout("abc", Metrics, 10, 100);

        var line = layout.Lines.ShouldHaveSingleItem();
        line.Text.ShouldBe("abc");
        line.Width.ShouldBe(15);
        line.Baseline.ShouldBe(8, "ascender 800/1000 of 10 pt");
        layout.LineHeight.ShouldBe(12, "(800 + 200 + 200) / 1000 × 10");
        layout.Height.ShouldBe(10, "ascent 8 + descent 2");
    }

    [Fact]
    public void LongText_BreaksAtSpaces()
    {
        // "aaaa bbbb cccc": each word 20 pt, space 2.5 pt; 45 pt fits two words.
        var layout = TextLayout.Layout("aaaa bbbb cccc", Metrics, 10, 45);

        layout.Lines.Select(l => l.Text).ShouldBe(["aaaa bbbb", "cccc"]);
        layout.Lines[1].Baseline.ShouldBe(20);
        layout.Height.ShouldBe(8 + 2 + 12);
    }

    [Fact]
    public void NewLines_StartParagraphs_AndEmptyLinesKeepTheirHeight()
    {
        var layout = TextLayout.Layout("a\r\n\nb", Metrics, 10, 100);

        layout.Lines.Select(l => l.Text).ShouldBe(["a", string.Empty, "b"]);
        layout.Lines[2].Baseline.ShouldBe(8 + 24);
    }

    [Fact]
    public void AWordWiderThanTheBox_BreaksBetweenCharacters_NeverInsideACombiningSequence()
    {
        // "e" followed by a combining mark that has no precomposed form keeps both together.
        var layout = TextLayout.Layout("abcdefgh", Metrics, 10, 22);

        layout.Lines.Select(l => l.Text).ShouldBe(["abcd", "efgh"]);

        var combining = TextLayout.Layout("aaae͠bb", Metrics, 10, 20);
        combining.Lines.SelectMany(l => l.Text).Count(c => c == '͠').ShouldBe(1);
        combining.Lines.ShouldAllBe(l => !l.Text.StartsWith('͠'));
    }

    [Theory]
    [InlineData(TextAlignment.Left, 0)]
    [InlineData(TextAlignment.Center, 42.5)]
    [InlineData(TextAlignment.Right, 85)]
    public void Alignment_PlacesTheLine(TextAlignment alignment, double expectedX)
    {
        var layout = TextLayout.Layout("abc", Metrics, 10, 100, alignment);

        layout.Lines[0].Segments.ShouldHaveSingleItem().X.ShouldBe(expectedX);
    }

    [Fact]
    public void Justify_SpreadsWords_ExceptOnTheLastLine()
    {
        var layout = TextLayout.Layout("aa bb cc dd", Metrics, 10, 40, TextAlignment.Justify);

        layout.Lines.Select(l => l.Text).ShouldBe(["aa bb cc", "dd"]);
        var first = layout.Lines[0].Segments;
        first.Select(s => s.X).ShouldBe([0, 15, 30]);
        first[^1].X.ShouldBe(40 - first[^1].Width, 1e-9, "the last word ends at the right edge");
        layout.Lines[1].Segments.ShouldHaveSingleItem().X.ShouldBe(0);
    }

    [Fact]
    public void Text_IsNormalizedToNfc_AndMeasuredByCodePoint()
    {
        var decomposed = "Việt"; // "Việt" written with combining marks
        var layout = TextLayout.Layout(decomposed, Metrics, 10, 0);

        layout.Lines[0].Text.ShouldBe("Việt");
        TextLayout.Measure(decomposed, Metrics, 10).ShouldBe(20, "4 letters once composed");
    }

    [Fact]
    public void LineSpacing_MultipliesTheLineHeight()
    {
        var layout = TextLayout.Layout("a\nb", Metrics, 10, 0, lineSpacing: 1.5);

        layout.LineHeight.ShouldBe(18);
        layout.Lines[1].Baseline.ShouldBe(26);
    }

    [Fact]
    public void WithoutAWidth_TextDoesNotWrap()
    {
        TextLayout.Layout("aaaa bbbb cccc dddd", Metrics, 10, 0).Lines.ShouldHaveSingleItem();
        TextLayout.Layout("aaaa bbbb", Metrics, 10, double.PositiveInfinity).Lines.ShouldHaveSingleItem();
    }

    private sealed class FakeMetrics : IFontMetrics
    {
        public int UnitsPerEm => 1000;

        public int Ascender => 800;

        public int Descender => -200;

        public int LineGap => 200;

        public int Advance(int codePoint) => codePoint switch
        {
            ' ' => 250,
            >= 0x0300 and <= 0x036F => 0,
            _ => 500,
        };
    }
}
