using PdfPro.Core.Geometry;
using PdfPro.Core.Stamping;

namespace PdfPro.Core.Tests.Stamping;

public sealed class StampingTests
{
    private static readonly StampValues Values = new("3", "10", new DateTime(2026, 10, 5, 14, 30, 0), "Hồ sơ", "Tờ trình");

    [Fact]
    public void Template_FillsEveryField()
    {
        StampTemplate.Parse("Trang {page}/{pages} – {filename} – {title} – {date}").Render(Values)
            .ShouldBe("Trang 3/10 – Hồ sơ – Tờ trình – 05/10/2026");
        StampTemplate.Parse("{DATE:yyyy-MM-dd HH:mm}").Render(Values).ShouldBe("2026-10-05 14:30");
        StampTemplate.Parse("{{page}} = {page}").Render(Values).ShouldBe("{page} = 3");
        StampTemplate.Parse("{title}").Render(Values with { Title = null }).ShouldBe(string.Empty);
        StampTemplate.Parse(string.Empty).Render(Values).ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("Trang 3/10")]
    [InlineData("{page} chưa thay")]
    [InlineData("}{ {{ }}")]
    [InlineData("")]
    public void Escape_WritesTheTextAsItIs(string literal)
    {
        var template = StampTemplate.Parse(StampTemplate.Escape(literal));

        template.Render(Values).ShouldBe(literal);
        template.DependsOnPage.ShouldBeFalse();
    }

    [Fact]
    public void Template_KnowsWhetherItChangesPerPage()
    {
        StampTemplate.Parse("{page}").DependsOnPage.ShouldBeTrue();
        StampTemplate.Parse("{pages} {date}").DependsOnPage.ShouldBeFalse();
    }

    [Theory]
    [InlineData("{page", StampTemplateError.UnbalancedBrace)]
    [InlineData("page}", StampTemplateError.UnbalancedBrace)]
    [InlineData("{author}", StampTemplateError.UnknownField)]
    [InlineData("{page:000}", StampTemplateError.UnknownField)]
    [InlineData("{date:}", StampTemplateError.InvalidDateFormat)]
    public void Template_RefusesBadPatterns(string pattern, StampTemplateError expected)
    {
        StampTemplate.TryParse(pattern, out _, out var error, out var token).ShouldBeFalse();

        error.ShouldBe(expected);
        token.ShouldNotBeNullOrEmpty();
        Should.Throw<FormatException>(() => StampTemplate.Parse(pattern));
    }

    [Theory]
    [InlineData(1, "I")]
    [InlineData(4, "IV")]
    [InlineData(9, "IX")]
    [InlineData(14, "XIV")]
    [InlineData(1994, "MCMXCIV")]
    [InlineData(3999, "MMMCMXCIX")]
    [InlineData(4000, "4000")]
    [InlineData(0, "0")]
    public void RomanNumerals(int number, string expected) => PageNumbering.ToRoman(number).ShouldBe(expected);

    [Fact]
    public void PageNumbers_StartOnAChosenPageWithAChosenNumber()
    {
        // Numbering starts on the 3rd page (index 2) with 1.
        PageNumbering.NumberOf(0, firstPage: 2, startNumber: 1).ShouldBeNull();
        PageNumbering.NumberOf(2, 2, 1).ShouldBe(1);
        PageNumbering.NumberOf(9, 2, 1).ShouldBe(8);
        PageNumbering.LastNumber(pageCount: 10, firstPage: 2, startNumber: 1).ShouldBe(8);
        PageNumbering.Format(4, PageNumberStyle.LowerRoman).ShouldBe("iv");
        PageNumbering.Format(4, PageNumberStyle.UpperRoman).ShouldBe("IV");
        PageNumbering.Format(4, PageNumberStyle.Arabic).ShouldBe("4");
    }

    [Fact]
    public void Bates_RunsOnAcrossFilesWithoutGapsOrRepeats()
    {
        var plan = BatesNumbering.Plan([3, 1, 5], startNumber: 120);

        plan.ShouldBe([new BatesRange(0, 120, 122), new BatesRange(1, 123, 123), new BatesRange(2, 124, 128)]);
        plan[2].NumberOf(4).ShouldBe(128);
        Enumerable.Range(0, 3).SelectMany(f => Enumerable.Range(0, (int)(plan[f].Last - plan[f].First + 1)).Select(p => plan[f].NumberOf(p)))
            .ShouldBe(Enumerable.Range(120, 9).Select(n => (long)n));
        Should.Throw<ArgumentOutOfRangeException>(() => BatesNumbering.Plan([2, 0], 1));
    }

    [Fact]
    public void BatesLabels_AreZeroPadded()
    {
        var format = new BatesFormat("HS-", 6, "-A");

        format.Label(123).ShouldBe("HS-000123-A");
        format.Label(1234567).ShouldBe("HS-1234567-A", "a larger number just has more digits");
        format.Overflows(999999).ShouldBeFalse();
        format.Overflows(1000000).ShouldBeTrue();
    }

    [Theory]
    [InlineData(StampAnchor.TopLeft, 30, 25)]
    [InlineData(StampAnchor.TopCenter, 300, 25)]
    [InlineData(StampAnchor.Center, 300, 400)]
    [InlineData(StampAnchor.BottomRight, 570, 775)]
    [InlineData(StampAnchor.MiddleLeft, 30, 400)]
    public void Anchors_PlaceContentInsideTheMargins(StampAnchor anchor, double x, double y)
    {
        // 40 × 30 content, margins 10 × 10, on a 600 × 800 page.
        StampPlacement.Place(new PdfSize(600, 800), new PdfSize(40, 30), 0, anchor, 10, 10).ShouldBe(new ViewPoint(x, y));
    }

    [Fact]
    public void ATurnedStamp_UsesItsTurnedSize()
    {
        StampPlacement.RotatedSize(new PdfSize(40, 30), 90).Width.ShouldBe(30, 1e-9);
        StampPlacement.Place(new PdfSize(600, 800), new PdfSize(40, 30), 90, StampAnchor.TopLeft, 0, 0).X.ShouldBe(15, 1e-9);
    }

    [Fact]
    public void Tiles_CoverTheWholePage()
    {
        var page = new PdfSize(600, 800);
        var content = new PdfSize(100, 40);

        var centers = StampPlacement.Tile(page, content, 0, 20);

        foreach (var probe in new[] { new ViewPoint(1, 1), new ViewPoint(599, 1), new ViewPoint(300, 400), new ViewPoint(1, 799), new ViewPoint(599, 799) })
        {
            // Every point of the page is within one step of a tile centre.
            centers.ShouldContain(c => Math.Abs(c.X - probe.X) <= 60 && Math.Abs(c.Y - probe.Y) <= 30);
        }

        centers.ShouldAllBe(c => c.X + 50 >= 0 && c.X - 50 <= 600 && c.Y + 20 >= -30 && c.Y - 20 <= 800);
        StampPlacement.Tile(page, new PdfSize(1, 1), 0, 0).Count.ShouldBe(StampPlacement.MaxTiles);
    }
}
