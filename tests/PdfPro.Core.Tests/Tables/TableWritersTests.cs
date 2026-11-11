using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using PdfPro.Core.Tables;

namespace PdfPro.Core.Tests.Tables;

/// <summary>Tables written for Excel: workbooks read back by the app's own reader, valid sheet names, numbers, and CSV with a BOM.</summary>
public sealed class TableWritersTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void AWorkbook_ReadsBack_WithItsVietnameseText_AndItsNumbersAsNumbers()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["STT", "Họ và tên", "Số tiền (đồng)"],
            ["1", "Nguyễn Văn An", "1.250.000"],
            ["2", "Tra\u0302\u0300n Thi\u0323 Bi\u0301ch", "980.000"],
            ["007", "  có khoảng trắng ", "=SUM(A1)"],
        ];
        using var stream = new MemoryStream();

        XlsxWriter.Write(stream, [new SheetContent("Bảng 1 (trang 1)", rows)], NumberReading.DotGrouping);

        stream.Position = 0;
        var table = XlsxTable.Read(stream);
        table.Columns.ShouldBe(["STT", "Họ và tên", "Số tiền (đồng)"]);
        table.Rows.Select(r => r.ToArray()).ShouldBe(
        [
            ["1", "Nguyễn Văn An", "1250000"],
            ["2", "Trần Thị Bích", "980000"],
            ["007", "  có khoảng trắng ", "=SUM(A1)"],
        ]);
        var sheet = Part(stream, "xl/worksheets/sheet1.xml");
        var cells = sheet.Descendants(Main + "c").ToDictionary(c => (string)c.Attribute("r")!);
        ((string?)cells["C2"].Attribute("t")).ShouldBeNull("a number");
        ((string?)cells["A4"].Attribute("t")).ShouldBe("inlineStr", "a code with a leading zero stays text");
        ((string?)cells["C4"].Attribute("t")).ShouldBe("inlineStr", "text, never a formula");
        ((string?)cells["A1"].Attribute("s")).ShouldBe("1", "the header is bold");
        sheet.Descendants(Main + "pane").ShouldHaveSingleItem();
        Part(stream, "xl/workbook.xml").Descendants(Main + "sheet").Select(s => (string)s.Attribute("name")!).ShouldBe(["Bảng 1 (trang 1)"]);
    }

    [Fact]
    public void Sheets_AreNamedTheWayExcelAccepts()
    {
        XlsxWriter.SheetNames(["Bảng 1", "bảng 1", "a/b:c*d?[e]", "", new string('x', 40), new string('x', 40)])
            .ShouldBe(["Bảng 1", "bảng 1 (2)", "a b c d  e", "Sheet4", new string('x', 31), new string('x', 27) + " (2)"]);
        XlsxWriter.ColumnName(0).ShouldBe("A");
        XlsxWriter.ColumnName(25).ShouldBe("Z");
        XlsxWriter.ColumnName(26).ShouldBe("AA");
        XlsxWriter.ColumnName(701).ShouldBe("ZZ");
        XlsxWriter.ColumnName(702).ShouldBe("AAA");
    }

    [Fact]
    public void SeveralTables_AreSeveralSheets_AndTextStaysTextWhenAsked()
    {
        using var stream = new MemoryStream();

        XlsxWriter.Write(stream, [new SheetContent("A", [["x", "y"], ["1.500", "2"]]), new SheetContent("B", [["z"]])]);

        Part(stream, "xl/workbook.xml").Descendants(Main + "sheet").Count().ShouldBe(2);
        Part(stream, "xl/worksheets/sheet1.xml").Descendants(Main + "c").ShouldAllBe(c => (string?)c.Attribute("t") == "inlineStr");
        Part(stream, "[Content_Types].xml").ToString().ShouldContain("/xl/worksheets/sheet2.xml");
    }

    [Theory]
    [InlineData("1.250.000", NumberReading.DotGrouping, 1250000.0)]
    [InlineData("1.234,5", NumberReading.DotGrouping, 1234.5)]
    [InlineData("-45", NumberReading.DotGrouping, -45.0)]
    [InlineData("0,25", NumberReading.DotGrouping, 0.25)]
    [InlineData("1,234.5", NumberReading.CommaGrouping, 1234.5)]
    [InlineData("4.500", NumberReading.CommaGrouping, 4.5)]
    [InlineData("4.500", NumberReading.DotGrouping, 4500.0)]
    [InlineData("12.34.567", NumberReading.DotGrouping, null)]
    [InlineData("VPP-01", NumberReading.DotGrouping, null)]
    [InlineData("007", NumberReading.DotGrouping, null)]
    [InlineData("1250000", NumberReading.Text, null)]
    [InlineData("1.", NumberReading.CommaGrouping, null)]
    public void Numbers_AreRecognisedTheWayTheyAreWritten(string text, NumberReading reading, double? expected)
        => TableNumbers.Parse(text, reading).ShouldBe(expected);

    [Fact]
    public void Csv_HasABom_QuotesWhatNeedsIt_AndKeepsFormulasFromRunning()
    {
        var bytes = CsvWriter.Write([["Họ và tên", "Ghi chú"], ["Trần, Bình", "nói \"chào\""], ["=1+1", "-5"], ["+84 912", "@x"]], ',');

        bytes.AsSpan(0, 3).ToArray().ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).ShouldBe(
            "Họ và tên,Ghi chú\r\n\"Trần, Bình\",\"nói \"\"chào\"\"\"\r\n'=1+1,-5\r\n'+84 912,'@x\r\n");
        var table = CsvTable.Read(CsvWriter.Write([["a", "b"], ["Đà Nẵng; Huế", "x"]], ';'));
        table.Rows.ShouldHaveSingleItem().ShouldBe(["Đà Nẵng; Huế", "x"]);
    }

    private static XDocument Part(MemoryStream stream, string name)
    {
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        using var part = zip.GetEntry(name)!.Open();
        return XDocument.Load(part);
    }
}
