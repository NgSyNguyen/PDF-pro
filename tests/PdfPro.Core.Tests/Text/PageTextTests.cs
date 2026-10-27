using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;

namespace PdfPro.Core.Tests.Text;

public sealed class PageTextTests
{
    private const double CharWidth = 5;
    private const double LineHeight = 10;

    [Fact]
    public void Lines_AreSplitAtTheLineEndsTheExtractorAdds()
    {
        var text = Page(("Xin chào", 50, 700), ("thế giới", 50, 688));

        text.Lines.Select(text.GetText).ShouldBe(["Xin chào", "thế giới"]);
        text.HasText.ShouldBeTrue();
        text.Text.ShouldBe("Xin chào\r\nthế giới");
    }

    [Fact]
    public void AnEmptyOrScannedPage_HasNoText()
    {
        PageText.Empty.HasText.ShouldBeFalse();
        PageText.Empty.Lines.ShouldBeEmpty();
        new PageText([new TextChar(" ", new PdfRect(0, 0, 5, 10))]).HasText.ShouldBeFalse();
    }

    [Fact]
    public void CharAt_FindsTheCharacterUnderThePoint_OrTheNearestWithinTheTolerance()
    {
        var text = Page(("abc", 100, 700));

        text.CharAt(new PdfPoint(107, 705)).ShouldBe(1);
        text.CharAt(new PdfPoint(120, 705)).ShouldBeNull();
        text.CharAt(new PdfPoint(118, 705), tolerance: 4).ShouldBe(2);
        text.CharAt(new PdfPoint(118, 705), tolerance: 2).ShouldBeNull();
    }

    [Fact]
    public void CaretAt_FollowsADragIntoTheMargins()
    {
        var text = Page(("dòng một", 100, 700), ("dòng hai", 100, 688));

        text.CaretAt(new PdfPoint(500, 704)).ShouldBe(7, "right of the first line: its last character");
        text.CaretAt(new PdfPoint(10, 690)).ShouldBe(10, "left of the second line: its first character");
        text.CaretAt(new PdfPoint(112, 900)).ShouldBe(2, "above the text: the first line");
        PageText.Empty.CaretAt(new PdfPoint(0, 0)).ShouldBeNull();
    }

    [Fact]
    public void WordAt_TakesLettersMarksAndDigits_ButStopsAtSpacesAndPunctuation()
    {
        // "hoà" with a combining grave accent, as some producers write it.
        var text = Page(("Cộng hoà, 2026!", 50, 700));

        text.GetText(text.WordAt(0)).ShouldBe("Cộng");
        text.GetText(text.WordAt(6)).ShouldBe("hoà");
        text.GetText(text.WordAt(8)).ShouldBe("hoà", "the mark belongs to the word");
        text.GetText(text.WordAt(11)).ShouldBe("2026");
        text.GetText(text.WordAt(9)).ShouldBe(",");
    }

    [Fact]
    public void LineAt_GivesTheWholeLine()
    {
        var text = Page(("một hai", 50, 700), ("ba", 50, 688));

        text.GetText(text.LineAt(4)).ShouldBe("một hai");
        text.GetText(text.LineAt(9)).ShouldBe("ba");
        text.LineIndexOf(7).ShouldBe(-1, "a line end belongs to no line");
    }

    [Fact]
    public void Rects_CoverEachLineOfTheSelection()
    {
        var text = Page(("abcd", 100, 700), ("efgh", 100, 688));

        var rects = text.Rects(new TextRange(2, 8)); // "cd", line end, "ef"

        rects.ShouldBe([new PdfRect(110, 700, 120, 710), new PdfRect(100, 688, 110, 698)]);
    }

    [Fact]
    public void CopyText_JoinsTheLinesOfAParagraph_AndIsNfc()
    {
        var text = Page(("Ủy ban nhân dân tỉnh", 50, 700), ("ban hành quyết định", 50, 688));

        text.CopyText(new TextRange(0, text.Count)).ShouldBe("Ủy ban nhân dân tỉnh ban hành quyết định");
    }

    [Fact]
    public void CopyText_MendsAWordBrokenByAHyphen()
    {
        var text = Page(("Phòng công nghệ thông tin (infor-", 50, 700), ("mation technology) của sở", 50, 688));

        text.CopyText(new TextRange(0, text.Count)).ShouldBe("Phòng công nghệ thông tin (information technology) của sở");
    }

    [Fact]
    public void CopyText_KeepsParagraphs_AfterAGap_AnIndent_OrAShortLineEndingASentence()
    {
        var text = Page(
            ("Điều 1. Phạm vi điều chỉnh của văn bản này", 50, 700),
            ("áp dụng cho mọi cơ quan.", 50, 688),
            ("Điều 2. Đối tượng áp dụng gồm tổ chức và", 50, 664),
            ("cá nhân có liên quan đến việc thi hành", 50, 652),
            ("quyết định này.", 50, 640),
            ("Điều 3. Hiệu lực thi hành.", 70, 628));

        text.CopyText(new TextRange(0, text.Count)).ShouldBe(
            "Điều 1. Phạm vi điều chỉnh của văn bản này áp dụng cho mọi cơ quan.\r\n"
            + "Điều 2. Đối tượng áp dụng gồm tổ chức và cá nhân có liên quan đến việc thi hành quyết định này.\r\n"
            + "Điều 3. Hiệu lực thi hành.");
    }

    [Fact]
    public void CopyText_OfPartOfALine_IsJustThatPart()
    {
        var text = Page(("Cộng hòa xã hội", 50, 700), ("chủ nghĩa Việt Nam", 50, 688));

        text.CopyText(new TextRange(5, 8)).ShouldBe("hòa");
        text.CopyText(new TextRange(9, text.Lines[1].Start + 9)).ShouldBe("xã hội chủ nghĩa");
        text.CopyText(default).ShouldBeEmpty();
    }

    [Fact]
    public void CopyText_ComposesDecomposedLetters()
    {
        var text = Page(("Hoà Bình", 50, 700));

        var copied = text.CopyText(new TextRange(0, text.Count));

        copied.ShouldBe("Hoà Bình");
        copied.IsNormalized(NormalizationForm.FormC).ShouldBeTrue();
    }

    /// <summary>
    /// Lines drawn left to right from (left, bottom), each character <see cref="CharWidth"/> wide; spaces are drawn, and a
    /// generated "\r\n" follows every line but the last, as PDFium's extractor gives it. Combining marks share the box of
    /// their letter.
    /// </summary>
    private static PageText Page(params (string Text, double Left, double Bottom)[] lines)
    {
        var chars = new List<TextChar>();
        for (var l = 0; l < lines.Length; l++)
        {
            var (text, left, bottom) = lines[l];
            var x = left;
            foreach (var c in text)
            {
                var isMark = char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark;
                var start = isMark ? x - CharWidth : x;
                chars.Add(new TextChar(c.ToString(), new PdfRect(start, bottom, start + CharWidth, bottom + LineHeight), IsHyphen: c == '-' && l < lines.Length - 1 && x == left + ((text.Length - 1) * CharWidth)));
                if (!isMark)
                {
                    x += CharWidth;
                }
            }

            if (l < lines.Length - 1)
            {
                chars.Add(new TextChar("\r", default, IsGenerated: true));
                chars.Add(new TextChar("\n", default, IsGenerated: true));
            }
        }

        return new PageText(chars);
    }
}
