using System.Text;
using PdfPro.Core.Text;

namespace PdfPro.Core.Tests.Text;

public sealed class TextNormalizerTests
{
    // "Tiếng Việt" written with combining marks (NFD), as some PDF producers store it.
    private const string DecomposedVietnamese = "Tiếng Việt";

    [Fact]
    public void ToNfc_ComposesVietnameseLetters()
    {
        var result = TextNormalizer.ToNfc(DecomposedVietnamese);

        result.ShouldBe("Tiếng Việt".Normalize(NormalizationForm.FormC));
        result.Length.ShouldBe(10);
        result.IsNormalized(NormalizationForm.FormC).ShouldBeTrue();
    }

    [Fact]
    public void ToNfc_KeepsTextThatIsAlreadyNormalized()
    {
        const string text = "Hợp đồng số 12/2026";

        TextNormalizer.ToNfc(text).ShouldBeSameAs(text);
    }

    [Fact]
    public void ToNfc_ReplacesUnpairedSurrogates()
    {
        // Built in code: invalid UTF-16 does not survive theory-data serialization.
        TextNormalizer.ToNfc("A\uD800B").ShouldBe("A�B");
        TextNormalizer.ToNfc("\uDC00").ShouldBe("�");
        TextNormalizer.ToNfc("end\uD83D").ShouldBe("end�");
        TextNormalizer.ToNfc("\uDE00\uD83D").ShouldBe("��");
        TextNormalizer.ToSingleLine("Mục\uD800").ShouldBe("Mục�".Normalize(NormalizationForm.FormC));
    }

    [Fact]
    public void ToNfc_KeepsSurrogatePairs()
    {
        const string text = "Ghi chú 😀";

        TextNormalizer.ToNfc(text).ShouldBe(text);
    }

    [Theory]
    [InlineData("Chương 1\r\n\tGiới thiệu", "Chương 1 Giới thiệu")]
    [InlineData("  Mục   lục  ", "Mục lục")]
    [InlineData("﻿Title", "Title")]
    [InlineData("A\u0000B\u0007C", "A B C")]
    [InlineData("\r\n\t ", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ToSingleLine_CollapsesWhiteSpaceAndControlCharacters(string? text, string expected)
    {
        TextNormalizer.ToSingleLine(text).ShouldBe(expected);
    }

    [Fact]
    public void ToSingleLine_NormalizesToNfc()
    {
        TextNormalizer.ToSingleLine(" " + DecomposedVietnamese + "\n").ShouldBe("Tiếng Việt".Normalize(NormalizationForm.FormC));
    }
}
