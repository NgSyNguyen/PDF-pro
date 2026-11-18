using PdfPro.Core.Text;

namespace PdfPro.Core.Tests.Text;

/// <summary>Command names matched without tone marks or case, by word starts, by words in any order, by letters in order.</summary>
public sealed class FuzzyMatchTests
{
    [Theory]
    [InlineData("Xóa trang trắng", "xoa trang trang")]
    [InlineData("Đánh số trang", "danh so")]
    [InlineData("Lưu thành…", "luu")]
    [InlineData("Nhận dạng chữ (OCR)", "ocr")]
    [InlineData("Xóa trang trắng", "xtt")]
    [InlineData("Xóa trang trắng", "trang xoa")]
    [InlineData("Đóng dấu số Bates", "dsb")]
    [InlineData("Mở…", "MỞ")]
    [InlineData("Nén", "nen")]
    public void TypedWords_FindTheCommand(string command, string typed)
        => FuzzyMatch.Score(typed, command).ShouldNotBeNull();

    [Theory]
    [InlineData("Nhận dạng chữ (OCR)", "nen")]
    [InlineData("Xoay trái", "xyz")]
    [InlineData("Mở", "mo khoa")]
    public void UnrelatedWords_DoNot(string command, string typed)
        => FuzzyMatch.Score(typed, command).ShouldBeNull();

    [Fact]
    public void BetterMatches_ScoreHigher()
    {
        int Score(string candidate) => FuzzyMatch.Score("xoa", candidate) ?? -1;

        Score("Xóa").ShouldBeGreaterThan(Score("Xóa trang"), "exactly what was typed");
        Score("Xóa trang").ShouldBeGreaterThan(Score("Trang xóa"), "at the start");
        Score("Trang xóa").ShouldBeGreaterThan(Score("Hộp thư bxoa"), "a word start beats letters inside a word");
        Score("Hộp thư bxoa").ShouldBeGreaterThan(Score("Ẩn x o a"), "letters together beat letters apart");
        FuzzyMatch.Score(string.Empty, "Mở").ShouldBe(0, "nothing typed matches everything");
    }

    [Fact]
    public void Folding_DropsMarksAndCase_AndSpacing()
        => FuzzyMatch.Fold("  Đồng   Ý̀ HỢP ĐỒNG ").ShouldBe("dong y hop dong");
}
