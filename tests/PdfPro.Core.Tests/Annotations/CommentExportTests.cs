using System.Globalization;
using PdfPro.Core.Annotations;

namespace PdfPro.Core.Tests.Annotations;

public sealed class CommentExportTests
{
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");
    private static readonly DateTimeOffset When = new(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(7));

    private static readonly CommentExportLabels Labels = new(
        "Bình luận", "Trang", "Loại", "Tác giả", "Ngày", "Nội dung", "Chữ được đánh dấu", "Trạng thái", "Trả lời", "Không có bình luận.");

    private static readonly CommentRecord[] Comments =
    [
        new(3, "Ghi chú", "Minh", null, "Thiếu chữ ký", null, null, []),
        new(1, "Tô sáng", "Nguyễn Lan", When, "Số liệu sai,\ncần kiểm tra", "Số: 12/2026", "Đã chấp nhận",
            [new CommentReplyRecord("Minh", When.AddHours(1), "Đã sửa \"bảng 2\"")]),
    ];

    [Fact]
    public void Markdown_GroupsByPage_WithTheConversation()
    {
        var markdown = CommentExport.ToMarkdown("Báo cáo_quý 3.pdf", Comments, Labels, Vietnamese);

        markdown.ShouldStartWith("# Bình luận — Báo cáo\\_quý 3.pdf\n");
        markdown.IndexOf("## Trang 1", StringComparison.Ordinal).ShouldBeLessThan(markdown.IndexOf("## Trang 3", StringComparison.Ordinal), "pages in order");
        markdown.ShouldContain($"- **Tô sáng** · Nguyễn Lan · {When.ToString("g", Vietnamese)} · *Đã chấp nhận*\n");
        markdown.ShouldContain("  > Số: 12/2026\n");
        markdown.ShouldContain("  Số liệu sai,\n  cần kiểm tra\n", Case.Sensitive, "each line of the comment stays in the item");
        markdown.ShouldContain($"  - Minh · {When.AddHours(1).ToString("g", Vietnamese)}: Đã sửa \"bảng 2\"\n");
        markdown.ShouldContain("- **Ghi chú** · Minh\n  Thiếu chữ ký\n");
    }

    [Fact]
    public void Markdown_SaysWhenThereAreNoComments()
        => CommentExport.ToMarkdown("a.pdf", [], Labels, Vietnamese).ShouldEndWith("\nKhông có bình luận.\n");

    [Fact]
    public void Csv_HasARowPerCommentAndPerReply_QuotedWhereNeeded()
    {
        var csv = CommentExport.ToCsv(Comments, Labels);

        var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        rows[0].ShouldBe("Trang,Loại,Tác giả,Ngày,Nội dung,Chữ được đánh dấu,Trạng thái");
        rows[1].ShouldBe("3,Ghi chú,Minh,,Thiếu chữ ký,,");
        csv.ShouldContain("1,Tô sáng,Nguyễn Lan,2026-10-27T09:30:00+07:00,\"Số liệu sai,\ncần kiểm tra\",Số: 12/2026,Đã chấp nhận\r\n");
        csv.ShouldContain("1,Trả lời,Minh,2026-10-27T10:30:00+07:00,\"Đã sửa \"\"bảng 2\"\"\",,\r\n");
    }

    [Theory]
    [InlineData("=1+2", "'=1+2")]
    [InlineData("+84 912", "'+84 912")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Ổn", "Ổn")]
    public void Csv_KeepsSpreadsheetsFromRunningFormulas(string contents, string cell)
    {
        var csv = CommentExport.ToCsv([new CommentRecord(1, "Ghi chú", "A", null, contents, null, null, [])], Labels);

        csv.Split("\r\n")[1].ShouldBe($"1,Ghi chú,A,,{cell},,");
    }
}
