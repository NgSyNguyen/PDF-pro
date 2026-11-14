using PdfPro.Core.Geometry;
using PdfPro.Core.Redaction;
using PdfPro.Core.Text;

namespace PdfPro.Core.Tests.Redaction;

public sealed class RedactionFinderTests
{
    private const double CharWidth = 6;
    private const double LineHeight = 12;

    private static readonly RedactionPattern[] All = [RedactionPattern.NationalId, RedactionPattern.Phone, RedactionPattern.Email, RedactionPattern.BankAccount, RedactionPattern.Date];

    [Fact]
    public void EveryKindOfPersonalData_IsFound_WithItsKind()
    {
        var page = Page(
            "Họ và tên: Nguyễn Thị Minh Anh",
            "Số CCCD: 001203004567",
            "Ngày sinh: 15/08/1990",
            "Điện thoại: 0912 345 678 hoặc +84 98 765 4321",
            "Máy bàn: 024 3825 1234",
            "Email: minhanh.nguyen@example.com",
            "Số tài khoản: 1903 6655 4433 21");

        var hits = RedactionFinder.Find(page, 0, All);

        hits.Select(h => (h.Pattern, h.Text)).ShouldBe(
        [
            (RedactionPattern.NationalId, "001203004567"),
            (RedactionPattern.Date, "15/08/1990"),
            (RedactionPattern.Phone, "0912 345 678"),
            (RedactionPattern.Phone, "+84 98 765 4321"),
            (RedactionPattern.Phone, "024 3825 1234"),
            (RedactionPattern.Email, "minhanh.nguyen@example.com"),
            (RedactionPattern.BankAccount, "1903 6655 4433 21"),
        ]);
    }

    [Fact]
    public void OnlyTheChosenPatterns_AreLookedFor()
    {
        var page = Page("CCCD 001203004567, điện thoại 0912345678, email a.b@example.vn");

        RedactionFinder.Find(page, 3, [RedactionPattern.Email]).ShouldHaveSingleItem().Text.ShouldBe("a.b@example.vn");
        RedactionFinder.Find(page, 3, [RedactionPattern.BankAccount]).ShouldBeEmpty("a CCCD or a phone number is not taken for an account");
        RedactionFinder.Find(page, 3, [RedactionPattern.NationalId]).ShouldHaveSingleItem().PageIndex.ShouldBe(3);
    }

    [Theory]
    [InlineData("0012030045678")]
    [InlineData("12345678901")]
    public void NumbersThatAreNotACccd_AreNotTakenForOne(string text)
        => RedactionFinder.Find(Page("Mã " + text), 0, [RedactionPattern.NationalId]).ShouldBeEmpty();

    [Theory]
    [InlineData("0123 456 789")]
    [InlineData("091234567")]
    [InlineData("84912345678901")]
    public void NumbersThatAreNotAPhone_AreNotTakenForOne(string text)
        => RedactionFinder.Find(Page("Số " + text), 0, [RedactionPattern.Phone]).ShouldBeEmpty();

    [Theory]
    [InlineData("32/01/1990")]
    [InlineData("15/13/1990")]
    [InlineData("15/08/1890")]
    public void ImpossibleDates_AreNotFound(string text)
        => RedactionFinder.Find(Page("Ngày " + text), 0, [RedactionPattern.Date]).ShouldBeEmpty();

    [Fact]
    public void AMatchAcrossTwoLines_IsCoveredLineByLine()
    {
        var page = Page("Liên hệ: 0912 345", "678 ngay.");

        var hit = RedactionFinder.Find(page, 0, [RedactionPattern.Phone]).ShouldHaveSingleItem();

        hit.Text.ShouldBe("0912 345\r\n678");
        hit.Rects.Count.ShouldBe(2, "one rectangle per line");
        hit.Rects[0].Bottom.ShouldBe(LineHeight * 2);
        hit.Rects[1].Left.ShouldBe(0);
    }

    [Fact]
    public void TheUsersOwnExpression_FindsWhatItSays()
    {
        RedactionFinder.TryCreateCustom(@"HS-\d{4}", out var custom, out _).ShouldBeTrue();
        var page = Page("Hồ sơ HS-0042 và HS-0043, CCCD 001203004567");

        var hits = RedactionFinder.Find(page, 0, [RedactionPattern.Custom, RedactionPattern.NationalId], custom);

        hits.Select(h => (h.Pattern, h.Text)).ShouldBe([(RedactionPattern.Custom, "HS-0042"), (RedactionPattern.Custom, "HS-0043"), (RedactionPattern.NationalId, "001203004567")]);
        RedactionFinder.TryCreateCustom("(", out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Hits_NeverShowTheirTextWhenPrinted()
    {
        var hit = RedactionFinder.Find(Page("CCCD 001203004567"), 0, [RedactionPattern.NationalId]).ShouldHaveSingleItem();

        hit.ToString().ShouldNotContain("001203004567");
    }

    [Fact]
    public void APageWithoutText_HasNothing()
        => RedactionFinder.Find(PageText.Empty, 0, All).ShouldBeEmpty();

    /// <summary>Lines from the top down, each character <see cref="CharWidth"/> wide, with "\r\n" between lines.</summary>
    private static PageText Page(params string[] lines)
    {
        var chars = new List<TextChar>();
        for (var l = 0; l < lines.Length; l++)
        {
            var bottom = (lines.Length - l) * LineHeight;
            var x = 0.0;
            foreach (var c in TextNormalizer.ToNfc(lines[l]))
            {
                chars.Add(new TextChar(c.ToString(), new PdfRect(x, bottom, x + CharWidth, bottom + LineHeight)));
                x += CharWidth;
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
