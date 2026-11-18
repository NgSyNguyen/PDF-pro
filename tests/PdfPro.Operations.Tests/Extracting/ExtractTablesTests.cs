using System.IO.Compression;
using System.Xml.Linq;
using PdfPro.Core.Geometry;
using PdfPro.Core.Tables;
using PdfPro.Operations.Extracting;
using PdfPro.Operations.Security;
using PdfPro.Operations.Tests.TestSupport;

namespace PdfPro.Operations.Tests.Extracting;

/// <summary>
/// The tables of samples/tables.pdf — a ruled table on page 1, one without rules on page 2 — found, read cell by cell
/// with their Vietnamese intact, and written as an Excel workbook (a sheet each, right numbers of rows and columns) or
/// as CSV files Excel opens right.
/// </summary>
public sealed class ExtractTablesTests
{
    private static readonly string[][] Ruled =
    [
        ["STT", "Họ và tên", "Đơn vị", "Số tiền (đồng)"],
        ["1", "Nguyễn Văn An", "Phòng Kế toán", "1.250.000"],
        ["2", "Trần Thị Bích", "Phòng Hành chính", "980.000"],
        ["3", "Lê Hoàng Cường", "Phòng Kỹ thuật", "2.400.000"],
        ["4", "Phạm Thu Dung", "Phòng Kinh doanh", "1.575.000"],
        ["5", "Võ Minh Đức", "Ban Giám đốc", "3.100.000"],
        ["6", "Đặng Ngọc Ánh", "Phòng Kế toán", "865.000"],
    ];

    private static readonly string[][] Plain =
    [
        ["Mã hàng", "Tên hàng", "Số lượng", "Đơn giá"],
        ["VPP-01", "Giấy in A4", "120", "65.000"],
        ["VPP-02", "Bút bi xanh", "300", "4.500"],
        ["VPP-03", "Kẹp tài liệu", "85", "12.000"],
        ["VPP-04", "Bìa hồ sơ", "200", "8.500"],
        ["VPP-05", "Sổ ghi chép", "60", "27.000"],
        ["VPP-06", "Mực máy in", "15", "450.000"],
        ["VPP-07", "Băng dính", "40", "9.000"],
        ["VPP-08", "Ghim bấm", "90", "6.000"],
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BothTables_AreFound_TheRuledOneByItsLines_TheOtherByItsColumns()
    {
        var progress = new RecordingProgress();

        var tables = await TableExtractor.FindAsync(Samples.Tables, null, null, progress, Token);

        tables.Count.ShouldBe(2);
        var (ruled, plain) = (tables[0], tables[1]);
        (ruled.Page, ruled.Method, ruled.RowCount, ruled.ColumnCount).ShouldBe((0, TableMethod.Lattice, 7, 4));
        Rows(ruled).ShouldBe(Ruled);
        (plain.Page, plain.Method, plain.RowCount, plain.ColumnCount).ShouldBe((1, TableMethod.Stream, 9, 4));
        Rows(plain).ShouldBe(Plain);
        ruled.Box.Width.ShouldBeGreaterThan(400);
        progress.Reports[^1].Completed.ShouldBe(2);
    }

    [Fact]
    public async Task AnAreaDrawnByTheUser_IsReadByTheWayAsked()
    {
        var area = new PdfRect(60, 500, 520, 710);

        var stream = await TableExtractor.ReadAsync(Samples.Tables, null, [new TableRegion(1, area, TableMethod.Stream)], null, Token);
        var lattice = await TableExtractor.ReadAsync(Samples.Tables, null, [new TableRegion(1, area, TableMethod.Lattice)], null, Token);
        var auto = await TableExtractor.ReadAsync(Samples.Tables, null, [new TableRegion(0, area)], null, Token);

        Rows(stream.ShouldHaveSingleItem()).ShouldBe(Plain);
        stream[0].Box.ShouldBe(area);
        lattice.ShouldBeEmpty("page 2 has no rules");
        var ruled = auto.ShouldHaveSingleItem();
        ruled.Method.ShouldBe(TableMethod.Lattice);
        Rows(ruled).ShouldBe(Ruled);
    }

    [Fact]
    public async Task AWorkbook_HasASheetPerTable_WithItsRowsColumnsAndNumbers()
    {
        using var temp = new TempDirectory();
        var parameters = new ExtractTablesParams(Numbers: NumberReading.DotGrouping, SheetName: "Bảng {0} (trang {1})");

        var result = await new ExtractTablesOperation().WriteAsync(Samples.Tables, null, temp.Path, "bảng biểu", parameters, null, Token);

        var file = result.Files.ShouldHaveSingleItem();
        Path.GetFileName(file).ShouldBe("bảng biểu.xlsx");
        result.Tables.Count.ShouldBe(2);
        using (var zip = ZipFile.OpenRead(file))
        {
            using var workbook = zip.GetEntry("xl/workbook.xml")!.Open();
            XDocument.Load(workbook).Descendants().Where(e => e.Name.LocalName == "sheet").Select(s => (string)s.Attribute("name")!)
                .ShouldBe(["Bảng 1 (trang 1)", "Bảng 2 (trang 2)"]);
        }

        await using var stream = File.OpenRead(file);
        var first = XlsxTable.Read(stream);
        first.Columns.ShouldBe(Ruled[0]);
        first.Rows.Count.ShouldBe(6);
        first.Rows[0].ShouldBe(["1", "Nguyễn Văn An", "Phòng Kế toán", "1250000"]);
        Directory.GetFiles(temp.Path).ShouldBe([file], "no temporary file is left");
    }

    [Fact]
    public async Task CsvFiles_OneEachTable_OpenWithTheirVietnamese()
    {
        using var temp = new TempDirectory();

        var result = await new ExtractTablesOperation().WriteAsync(Samples.Tables, null, temp.Path, "bảng", new ExtractTablesParams(TableFileFormat.Csv, Separator: CsvSeparator.Semicolon), null, Token);

        result.Files.Select(Path.GetFileName).ShouldBe(["bảng-1.csv", "bảng-2.csv"]);
        var bytes = File.ReadAllBytes(result.Files[1]);
        bytes.AsSpan(0, 3).ToArray().ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        var table = CsvTable.Read(bytes);
        table.Columns.ShouldBe(Plain[0]);
        table.Rows.Select(r => r.ToArray()).ShouldBe(Plain.Skip(1));
    }

    [Fact]
    public async Task OnlyThePagesAsked_AreSearched_AndNoTableWritesNothing()
    {
        using var temp = new TempDirectory();
        var operation = new ExtractTablesOperation();

        var second = await operation.WriteAsync(Samples.Tables, null, temp.Path, "x", new ExtractTablesParams(Pages: "2"), null, Token);
        var none = await operation.WriteAsync(Samples.BasicVi, null, temp.Path, "y", new ExtractTablesParams(Pages: "1"), null, Token);

        second.Tables.ShouldHaveSingleItem().Page.ShouldBe(1);
        none.Tables.ShouldBeEmpty();
        none.Files.ShouldBeEmpty();
        Directory.GetFiles(temp.Path).Select(Path.GetFileName).ShouldBe(["x.xlsx"]);
        await Should.ThrowAsync<Core.Operations.OperationValidationException>(() => operation.WriteAsync(Samples.Tables, null, temp.Path, "z", new ExtractTablesParams(Pages: "5"), null, Token));
        await Should.ThrowAsync<Core.Operations.OperationValidationException>(() => operation.WriteTablesAsync([], temp.Path, "z", new ExtractTablesParams(SheetName: "Bảng"), Token));
    }

    [Fact]
    public async Task AProtectedDocument_NeedsItsPassword()
    {
        await Should.ThrowAsync<DocumentPasswordException>(() => TableExtractor.FindAsync(Samples.Encrypted, null, null, null, Token));
        (await TableExtractor.FindAsync(Samples.Encrypted, "test123", null, null, Token)).ShouldNotBeNull();
    }

    private static string[][] Rows(ExtractedTable table) => [.. table.Rows.Select(r => r.ToArray())];
}
