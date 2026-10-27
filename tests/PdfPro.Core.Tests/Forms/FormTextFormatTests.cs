using PdfPro.Core.Forms;

namespace PdfPro.Core.Tests.Forms;

/// <summary>Number and date formats of text fields: what is shown, what is kept, what is refused.</summary>
public sealed class FormTextFormatTests
{
    [Theory]
    [InlineData("1234.5", 2, NumberSeparators.DotComma, "1.234,50")]
    [InlineData("1234.5", 2, NumberSeparators.CommaDot, "1,234.50")]
    [InlineData("-2500000", 0, NumberSeparators.DotComma, "-2.500.000")]
    [InlineData("0.125", 1, NumberSeparators.CommaDot, "0.1")]
    [InlineData("không phải số", 2, NumberSeparators.DotComma, "không phải số")]
    [InlineData("", 2, NumberSeparators.DotComma, "")]
    public void Numbers_AreShownWithTheirSeparatorsAndDecimals(string value, int decimals, NumberSeparators separators, string shown)
        => FormTextFormat.Number(decimals, separators).Display(value).ShouldBe(shown);

    [Theory]
    [InlineData("1.234,5", NumberSeparators.DotComma, "1234.5")]
    [InlineData("1.234", NumberSeparators.DotComma, "1234", "a dot groups thousands in Vietnamese")]
    [InlineData("1234,5", NumberSeparators.DotComma, "1234.5")]
    [InlineData("1234.5", NumberSeparators.DotComma, "1234.5", "a plain number is understood too")]
    [InlineData(" 2 500 000 ", NumberSeparators.DotComma, "2500000")]
    [InlineData("1,234.5", NumberSeparators.CommaDot, "1234.5")]
    [InlineData("-7", NumberSeparators.CommaDot, "-7")]
    [InlineData("", NumberSeparators.CommaDot, "")]
    public void TypedNumbers_AreKeptAsPlainNumbers(string input, NumberSeparators separators, string kept, string because = "")
    {
        FormTextFormat.Number(2, separators).TryNormalize(input, out var value).ShouldBeTrue(because);
        value.ShouldBe(kept, because);
    }

    [Theory]
    [InlineData("12a", NumberSeparators.DotComma)]
    [InlineData("1,2,3", NumberSeparators.CommaDot)]
    [InlineData("1.23.4", NumberSeparators.DotComma)]
    [InlineData("1.234,5", NumberSeparators.CommaDot)]
    public void WhatIsNotANumber_IsRefused(string input, NumberSeparators separators)
        => FormTextFormat.Number(2, separators).TryNormalize(input, out _).ShouldBeFalse();

    [Theory]
    [InlineData("dd/mm/yyyy", "7/3/2026", "07/03/2026")]
    [InlineData("dd/mm/yyyy", "27/10/2026", "27/10/2026")]
    [InlineData("mm/dd/yyyy", "10/27/2026", "10/27/2026")]
    [InlineData("yyyy-mm-dd", "2026-10-27", "2026-10-27")]
    [InlineData("dd.mm.yyyy", "1.2.2026", "01.02.2026")]
    public void TypedDates_AreKeptInTheirPattern(string pattern, string input, string kept)
    {
        FormTextFormat.Date(pattern).TryNormalize(input, out var value).ShouldBeTrue();
        value.ShouldBe(kept);
    }

    [Theory]
    [InlineData("31/02/2026")]
    [InlineData("2026-10-27")]
    [InlineData("hôm nay")]
    public void WhatIsNotADateOfThePattern_IsRefused(string input)
        => FormTextFormat.Date().TryNormalize(input, out _).ShouldBeFalse();

    [Fact]
    public void FreeText_IsKeptAsTyped()
    {
        new FormTextFormat(FormFormatKind.None).TryNormalize("  bất kỳ  ", out var value).ShouldBeTrue();
        value.ShouldBe("bất kỳ");
    }

    [Fact]
    public void BadFormats_HaveAProblem()
    {
        FormTextFormat.Number(2).Problem().ShouldBeNull();
        FormTextFormat.Date("yyyy-mm-dd").Problem().ShouldBeNull();
        FormTextFormat.Number(11).Problem().ShouldNotBeNull();
        FormTextFormat.Number(-1).Problem().ShouldNotBeNull();
        FormTextFormat.Date("yy/m").Problem().ShouldNotBeNull();
        new FormTextFormat((FormFormatKind)9).Problem().ShouldNotBeNull();
        FormTextFormat.NetPattern("dd/mm/yyyy").ShouldBe("dd/MM/yyyy");
    }
}
