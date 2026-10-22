using PdfPro.Core.Splitting;

namespace PdfPro.Core.Tests.Splitting;

public sealed class FileNameTests
{
    [Theory]
    [InlineData("Số: 12/QĐ-UBND", "Số 12-QĐ-UBND")]
    [InlineData("  Báo cáo   quý\t3 ", "Báo cáo quý 3")]
    [InlineData("a*b?c\"d<e>f|g\\h", "abcdef-g-h")]
    [InlineData("Hợp đồng...", "Hợp đồng")]
    [InlineData("line\nbreak\u0001", "line break")]
    [InlineData("CON", "CON_")]
    [InlineData("nul.backup", "nul.backup_")]
    [InlineData("COM1", "COM1_")]
    [InlineData("CONSOLE", "CONSOLE")]
    [InlineData("???", "")]
    [InlineData(null, "")]
    public void Sanitize_MakesNamesWindowsAccepts(string? text, string expected)
        => FileNames.Sanitize(text).ShouldBe(expected);

    [Fact]
    public void Sanitize_ComposesDecomposedVietnamese()
        => FileNames.Sanitize("Quyết định").ShouldBe("Quyết định");

    [Fact]
    public void Sanitize_TruncatesWithoutBreakingLetters()
    {
        // "ẹ́" has no precomposed form: after NFC it is "ẹ" + combining acute, one letter of two code units.
        var text = new string('a', 8) + "ẹ́xyz";

        FileNames.Sanitize(text, maxLength: 10).ShouldBe(new string('a', 8) + "ẹ́");
        FileNames.Sanitize(text, maxLength: 9).ShouldBe(new string('a', 8));
        FileNames.Sanitize(new string('a', 8) + "\U0001F600xyz", maxLength: 9).ShouldBe(new string('a', 8));
        FileNames.Sanitize("abc. def", maxLength: 4).ShouldBe("abc");
    }

    [Fact]
    public void MakeUnique_NumbersRepeatsAndTakenNames()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "báo cáo" };

        var names = FileNames.MakeUnique(["Báo cáo", "a", "A", "a", "b"], taken.Contains);

        names.ShouldBe(["Báo cáo (2)", "a", "A (2)", "a (3)", "b"]);
        FileNames.MakeUnique(["x", "x (2)", "x"]).ShouldBe(["x", "x (2)", "x (3)"]);
        Should.Throw<ArgumentException>(() => FileNames.MakeUnique([" "]));
    }

    [Theory]
    [InlineData("{name}_{index:000}", 7, "Hồ sơ_007")]
    [InlineData("{name}_{index}", 12, "Hồ sơ_12")]
    [InlineData("{NAME} trang {first}-{last}", 1, "Hồ sơ trang 3-5")]
    [InlineData("{bookmark}", 1, "Số 12-QĐ")]
    [InlineData("{name} - {match}", 2, "Hồ sơ - Số 12-QĐ")]
    [InlineData("tệp {index:00}", 3, "tệp 03")]
    public void Render_FillsTheFields(string pattern, int index, string expected)
        => FileNameTemplate.Parse(pattern).Render(new FileNameValues("Hồ sơ", index, "Số: 12/QĐ", 3, 5)).ShouldBe(expected);

    [Fact]
    public void Render_FallsBackWhenTheTitleIsMissing()
    {
        var values = new FileNameValues("Hồ sơ", 4, null, 1, 2);

        FileNameTemplate.Parse("{bookmark}").Render(values).ShouldBe("Hồ sơ_004");
        FileNameTemplate.Parse("{match}.").Render(values).ShouldBe("Hồ sơ_004");
        FileNameTemplate.Parse("{name} {bookmark}").Render(values).ShouldBe("Hồ sơ");
        FileNameTemplate.Parse("???").Render(values).ShouldBe("Hồ sơ_004");
        FileNameTemplate.Default.Render(new FileNameValues("???", 4, null, 1, 2)).ShouldBe("_004");
        FileNameTemplate.Parse("{bookmark}").UsesTitle.ShouldBeTrue();
        FileNameTemplate.Default.UsesTitle.ShouldBeFalse();
    }

    [Theory]
    [InlineData("", FileNameTemplateError.Empty, null)]
    [InlineData("{name", FileNameTemplateError.UnbalancedBrace, "{name")]
    [InlineData("a}b", FileNameTemplateError.UnbalancedBrace, "}")]
    [InlineData("{na{me}", FileNameTemplateError.UnbalancedBrace, "{na{me}")]
    [InlineData("{date}", FileNameTemplateError.UnknownField, "{date}")]
    [InlineData("{name:00}", FileNameTemplateError.UnknownField, "{name:00}")]
    [InlineData("{index:0a}", FileNameTemplateError.UnknownField, "{index:0a}")]
    public void TryParse_ReportsWhatIsWrong(string pattern, FileNameTemplateError expected, string? token)
    {
        FileNameTemplate.TryParse(pattern, out var template, out var error, out var bad).ShouldBeFalse();

        template.ShouldBeNull();
        error.ShouldBe(expected);
        bad.ShouldBe(token);
        Should.Throw<FormatException>(() => FileNameTemplate.Parse(pattern));
    }
}
