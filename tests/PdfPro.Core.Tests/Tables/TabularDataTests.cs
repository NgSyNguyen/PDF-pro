using System.IO.Compression;
using System.Text;
using PdfPro.Core.Tables;

namespace PdfPro.Core.Tests.Tables;

/// <summary>Tables for form merges: CSV as Excel and others write it, and Excel workbooks.</summary>
public sealed class TabularDataTests
{
    [Theory]
    [InlineData("hoTen,tinh\r\nAn,Hà Nội\r\n")]
    [InlineData("hoTen;tinh\nAn;Hà Nội")]
    [InlineData("hoTen\ttinh\nAn\tHà Nội\n\n")]
    public void Csv_UsesTheSeparatorOfItsHeader(string text)
    {
        var table = CsvTable.Parse(text);

        table.Columns.ShouldBe(["hoTen", "tinh"]);
        table.Rows.ShouldHaveSingleItem().ShouldBe(["An", "Hà Nội"]);
    }

    [Fact]
    public void Csv_QuotedCells_HoldSeparatorsQuotesAndLineBreaks()
    {
        var table = CsvTable.Parse("ten,ghiChu\r\n\"Trần, Bình\",\"Dòng 1\r\nDòng \"\"2\"\"\"\r\n,\r\nCúc\r\n");

        table.Rows.Count.ShouldBe(2, "an empty row is left out");
        table.Rows[0].ShouldBe(["Trần, Bình", "Dòng 1\r\nDòng \"2\""]);
        table.Rows[1].ShouldBe(["Cúc", ""], "short rows are padded");
    }

    [Fact]
    public void Csv_BlankAndRepeatedColumnNames_AreMadeUnique()
        => CsvTable.Parse("ten,,Ten\n1,2,3").Columns.ShouldBe(["ten", "#2", "Ten (2)"]);

    [Fact]
    public void Csv_IsDecoded_FromUtf8WithOrWithoutBom_AndWindows1258()
    {
        var text = "hoTen\nNguyễn Văn An";
        CsvTable.Read([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]).Rows[0][0].ShouldBe("Nguyễn Văn An");
        CsvTable.Read(Encoding.UTF8.GetBytes(text)).Rows[0][0].ShouldBe("Nguyễn Văn An");

        // Windows-1258 (older Excel): "ê" is one byte (0xEA), the tilde a combining mark (0xDE); read back as NFC.
        byte[] legacy = [.. "hoTen\nNguy"u8, 0xEA, 0xDE, .. "n"u8];
        CsvTable.Read(legacy).Rows[0][0].ShouldBe("Nguyễn");
    }

    [Fact]
    public void Csv_WithoutAnyRow_IsRefused()
    {
        Should.Throw<TableFormatException>(() => CsvTable.Parse(""));
        Should.Throw<TableFormatException>(() => CsvTable.Parse("\n,\n"));
    }

    [Fact]
    public void Xlsx_FirstSheet_IsRead_WithSharedTextNumbersDatesAndBooleans()
    {
        var workbook = Workbook(
            sheet: """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="inlineStr"><is><t>ngaySinh</t></is></c><c r="D1" t="s"><v>2</v></c><c r="E1" t="str"><v>dongY</v></c></row>
                  <row r="2"><c r="A2" t="s"><v>3</v></c><c r="B2"><v>1250000.5</v></c><c r="C2" s="1"><v>46322</v></c><c r="E2" t="b"><v>1</v></c></row>
                  <row r="3"><c r="B3"><v>7</v></c><c r="D3" t="s"><v>4</v></c></row>
                </sheetData></worksheet>
                """,
            strings: ["hoTen", "soTien", "ghiChu", "Phạm Thị Hoa", "<r><t>đậm</t></r><r><t> thường</t></r>"]);

        var table = XlsxTable.Read(workbook);

        table.Columns.ShouldBe(["hoTen", "soTien", "ngaySinh", "ghiChu", "dongY"]);
        table.Rows.Count.ShouldBe(2);
        table.Rows[0].ShouldBe(["Phạm Thị Hoa", "1250000.5", "27/10/2026", "", "true"]);
        table.Rows[1].ShouldBe(["", "7", "", "đậm thường", ""]);
    }

    [Fact]
    public void AFileThatIsNotAWorkbook_IsRefused()
    {
        Should.Throw<TableFormatException>(() => XlsxTable.Read(new MemoryStream("hoTen,tinh"u8.ToArray())));
        Should.Throw<TableFormatException>(() => XlsxTable.Read(Workbook(sheet: null, strings: [])));
    }

    /// <summary>A minimal workbook: one sheet, shared strings, a date style (numFmtId 14) at index 1.</summary>
    private static MemoryStream Workbook(string? sheet, IReadOnlyList<string> strings)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }

            if (sheet is not null)
            {
                Add("xl/workbook.xml", """
                    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                      <sheets><sheet name="Dữ liệu" sheetId="1" r:id="rId1"/></sheets></workbook>
                    """);
                Add("xl/_rels/workbook.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/data.xml"/></Relationships>
                    """);
                Add("xl/worksheets/data.xml", sheet);
            }

            Add("xl/sharedStrings.xml", "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + string.Concat(strings.Select(s => s.StartsWith("<r>", StringComparison.Ordinal) ? $"<si>{s}</si>" : $"<si><t>{s}</t></si>")) + "</sst>");
            Add("xl/styles.xml", """
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14"/></cellXfs></styleSheet>
                """);
        }

        stream.Position = 0;
        return stream;
    }
}
