using PdfPro.Core.Documents;

namespace PdfPro.Core.Tests.Documents;

public sealed class PdfDateTests
{
    public static TheoryData<string, DateTimeOffset> ValidDates => new()
    {
        { "D:20260115083000+07'00'", new DateTimeOffset(2026, 1, 15, 8, 30, 0, TimeSpan.FromHours(7)) },
        { "D:20260115083000+07'00", new DateTimeOffset(2026, 1, 15, 8, 30, 0, TimeSpan.FromHours(7)) },
        { "D:19991231235959-05'30'", new DateTimeOffset(1999, 12, 31, 23, 59, 59, new TimeSpan(-5, -30, 0)) },
        { "D:20240229120000Z", new DateTimeOffset(2024, 2, 29, 12, 0, 0, TimeSpan.Zero) },
        { "D:20240229120000Z00'00'", new DateTimeOffset(2024, 2, 29, 12, 0, 0, TimeSpan.Zero) },
        { "D:20260115083000", new DateTimeOffset(2026, 1, 15, 8, 30, 0, TimeSpan.Zero) },
        { "20260115", new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero) },
        { "D:2026", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
        { "D:202603", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) },
        { "  D:20260115  ", new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero) },
    };

    [Theory]
    [MemberData(nameof(ValidDates))]
    public void TryParse_ReadsValidDates(string text, DateTimeOffset expected)
    {
        PdfDate.TryParse(text, out var actual).ShouldBeTrue();
        actual.ShouldBe(expected);
        actual.Offset.ShouldBe(expected.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hôm qua")]
    [InlineData("D:")]
    [InlineData("D:26")]
    [InlineData("D:20261301")]
    [InlineData("D:20250230")]
    [InlineData("D:20260115250000")]
    [InlineData("D:2026-01-01")]
    [InlineData("D:20260115083000+15'00'")]
    [InlineData("D:20260115083000X")]
    public void TryParse_RejectsInvalidDates(string? text)
        => PdfDate.TryParse(text, out _).ShouldBeFalse();

    [Theory]
    [InlineData(7, 0, "D:20261027093005+07'00'")]
    [InlineData(-5, -30, "D:20261027093005-05'30'")]
    [InlineData(0, 0, "D:20261027093005Z")]
    public void Format_WritesTheFullForm_ThatReadsBack(int hours, int minutes, string expected)
    {
        var value = new DateTimeOffset(2026, 10, 27, 9, 30, 5, new TimeSpan(hours, minutes, 0));

        PdfDate.Format(value).ShouldBe(expected);
        PdfDate.TryParse(PdfDate.Format(value), out var back).ShouldBeTrue();
        back.ShouldBe(value);
        back.Offset.ShouldBe(value.Offset);
    }
}
