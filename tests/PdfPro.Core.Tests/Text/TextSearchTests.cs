using PdfPro.Core.Geometry;
using PdfPro.Core.Text;

namespace PdfPro.Core.Tests.Text;

public sealed class TextSearchTests
{
    [Theory]
    [InlineData("hoa", new[] { "hoa", "Hòa", "hóa", "HOÀ" })]
    [InlineData("Hòa", new[] { "Hòa" })]
    [InlineData("hòa", new[] { "Hòa" })]
    [InlineData("HOA", new[] { "hoa", "Hòa", "hóa", "HOÀ" })]
    public void Automatically_ANoMarkQueryIgnoresMarks_AndAMarkedQueryMatchesThem(string query, string[] expected)
    {
        var page = Page("hoa Hòa hóa HOÀ ho");

        Find(page, query).ShouldBe(expected);
    }

    [Fact]
    public void DecomposedAndComposedLetters_MatchAlike()
    {
        // "Hòa" written as H, o, combining grave, a — as some producers do.
        var page = Page("Ủy ban Hòa Bình");

        Find(page, "Hòa").ShouldBe(["Hòa"]);
        Find(page, "hoa").ShouldBe(["Hòa"]);
        Find(page, "Hòa", new TextSearchOptions(Diacritics: DiacriticsMatching.Ignore)).ShouldBe(["Hòa"]);
    }

    [Fact]
    public void TheBarOfD_CountsAsAMark()
    {
        var page = Page("Đà Nẵng da đá");

        Find(page, "da").ShouldBe(["Đà", "da", "đá"]);
        Find(page, "đa").ShouldBe([], "a marked query matches marks: \"đa\" is none of them");
        Find(page, "đá").ShouldBe(["đá"]);
        Find(page, "đa", new TextSearchOptions(Diacritics: DiacriticsMatching.Ignore)).ShouldBe(["Đà", "da", "đá"]);
    }

    [Fact]
    public void WithMarksCounting_APlainLetterIsNotTheStartOfAMarkedOne()
    {
        var page = Page("hoà hoa");

        Find(page, "hoa", new TextSearchOptions(Diacritics: DiacriticsMatching.Match)).ShouldBe(["hoa"]);
    }

    [Fact]
    public void MatchCase_AndWholeWord()
    {
        var page = Page("Quyết định quyết toán QUYẾT");

        Find(page, "quyết", new TextSearchOptions(MatchCase: true)).ShouldBe(["quyết"]);
        Find(page, "quyết").ShouldBe(["Quyết", "quyết", "QUYẾT"]);
        Find(page, "định", new TextSearchOptions(WholeWord: true)).ShouldBe(["định"]);
        Find(Page("toán học toánhọc"), "toán", new TextSearchOptions(WholeWord: true)).ShouldBe(["toán"]);
    }

    [Fact]
    public void Phrases_MatchAcrossLines_AndRuns_OfSpaces()
    {
        var page = Page("Ủy ban nhân", "dân   tỉnh");

        var matches = Search(page, "nhân dân").FindAll(page);

        matches.ShouldHaveSingleItem();
        page.GetText(matches[0]).ShouldBe("nhân\r\ndân");
        Find(page, "nhân  dân").ShouldBe(["nhân\r\ndân"], "spaces in the query count as one");
    }

    [Fact]
    public void Matches_DoNotOverlap()
    {
        Find(Page("aaaa"), "aa").ShouldBe(["aa", "aa"]);
    }

    [Fact]
    public void RegularExpressions_FindDecisionNumbers()
    {
        var page = Page("Số: 12/QĐ-UBND ngày 5 tháng 3", "Số:128/2026/HĐ-MT");

        Find(page, @"Số:\s*\d+", new TextSearchOptions(UseRegex: true)).ShouldBe(["Số: 12", "Số:128"]);
        Find(page, @"so:\s*\d+", new TextSearchOptions(UseRegex: true)).ShouldBe(["Số: 12", "Số:128"], "no marks in the query: marks ignored, case too");
        Find(page, @"\d+/QĐ", new TextSearchOptions(UseRegex: true, MatchCase: true)).ShouldBe(["12/QĐ"]);
    }

    [Fact]
    public void InvalidQueries_AreRefused()
    {
        TextSearch.TryCreate("  ", new TextSearchOptions(), out _, out var empty).ShouldBeFalse();
        empty.ShouldNotBeNull();
        TextSearch.TryCreate("Số:(", new TextSearchOptions(UseRegex: true), out _, out var invalid).ShouldBeFalse();
        invalid.ShouldNotBeNull();
    }

    [Fact]
    public void ASnippet_ShowsTheMatchInItsContext_OnOneLine()
    {
        var page = Page("Căn cứ Luật Tổ chức chính quyền", "địa phương ngày 19 tháng 6");
        var match = Search(page, "chính quyền địa").FindAll(page).Single();

        var snippet = TextSearch.Snippet(page, match, context: 12);

        snippet.ShouldBe(new SearchSnippet("…Tổ chức ", "chính quyền địa", " phương ngày…"));
        TextSearch.Snippet(page, match, context: 200).ShouldBe(new SearchSnippet("Căn cứ Luật Tổ chức ", "chính quyền địa", " phương ngày 19 tháng 6"));
    }

    [Fact]
    public void APageWithoutText_HasNoMatches()
        => Search(PageText.Empty, "hoa").FindAll(PageText.Empty).ShouldBeEmpty();

    private static TextSearch Search(PageText page, string query, TextSearchOptions? options = null)
    {
        TextSearch.TryCreate(query, options ?? new TextSearchOptions(), out var search, out var error).ShouldBeTrue(error);
        return search;
    }

    private static string[] Find(PageText page, string query, TextSearchOptions? options = null)
        => [.. Search(page, query, options).FindAll(page).Select(page.GetText)];

    /// <summary>Lines of drawn characters (spaces drawn too), separated by the generated "\r\n" PDFium adds.</summary>
    private static PageText Page(params string[] lines)
    {
        var chars = new List<TextChar>();
        for (var l = 0; l < lines.Length; l++)
        {
            var x = 0.0;
            foreach (var c in lines[l])
            {
                chars.Add(new TextChar(c.ToString(), new PdfRect(x, 700 - (l * 12), x + 5, 710 - (l * 12))));
                x += 5;
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
