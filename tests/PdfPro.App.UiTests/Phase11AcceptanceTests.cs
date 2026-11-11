using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// Phase 11 acceptance scenarios (SPEC P11: comparing, extracting, structure) on the real application. Run by hand
/// like <see cref="AcceptanceTests"/>.
/// </summary>
public sealed class Phase11AcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(120);

    private readonly UiTestFolder _folder = new();

    public void Dispose()
    {
        var problems = _folder.LogProblems();
        _folder.Dispose();
        problems.ShouldBeEmpty("the application logged errors");
    }

    /// <summary>contract-v2 has a page inserted and three places changed: exactly four changes, and a report listing them.</summary>
    [Fact]
    public void TheTwoContracts_DifferByFourChanges_AndTheReportListsThem()
    {
        RequireEnabled();
        var newer = _folder.Copy("contract-v2.pdf");
        var older = _folder.Copy("contract-v1.pdf");
        using var app = AppUnderTest.Start(_folder, newer, older);
        app.WaitForTab("contract-v2.pdf", Long);
        app.WaitForTab("contract-v1.pdf", Long);

        app.Ribbon("tools", "compare.documents");
        var window = app.WaitForWindow(Text("Compare_Title"));
        app.Name("CompareOldName", window).ShouldBe("contract-v1.pdf", "the active document is the old version");
        app.Name("CompareNewName", window).ShouldBe("contract-v2.pdf");
        var watch = Stopwatch.StartNew();
        app.Invoke("CompareRun", window);
        AppUnderTest.WaitUntil(() => app.ItemCount("CompareChanges", window) > 0, Long, "the changes");
        output.WriteLine($"Compared in {watch.Elapsed.TotalSeconds:N1} s");
        app.ItemCount("CompareChanges", window).ShouldBe(4, "not every page after the inserted one");

        var report = Path.Combine(_folder.Documents, "báo cáo so sánh.pdf");
        app.Invoke("CompareExport", window);
        app.UseFileDialog(Text("Save_Dialog_Title"), report);
        AppUnderTest.WaitUntil(() => File.Exists(report), Long, "the report");
        var text = string.Join("\n", PdfFile.PageTexts(report));
        text.ShouldContain("120.000.000");
        text.ShouldContain("Đà Nẵng");
        app.Invoke("CompareClose", window);
    }

    /// <summary>The tables of tables.pdf become an Excel workbook with the right rows and columns, Vietnamese intact.</summary>
    [Fact]
    public void TheTablesOfTablesPdf_BecomeAWorkbook_WithTheirRowsAndColumns()
    {
        RequireEnabled();
        var path = _folder.Copy("tables.pdf");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("tables.pdf", Long);

        app.Ribbon("convert", "extract.tables");
        var window = app.WaitForWindow(Text("Tables_Title"));
        AppUnderTest.WaitUntil(() => app.ItemCount("TablesList", window) == 2, Long, "the two tables");
        var workbook = Path.Combine(_folder.Documents, "bảng.xlsx");
        app.Invoke("TablesExport", window);
        app.UseFileDialog(Text("Tables_SaveDialog_Title"), workbook);
        AppUnderTest.WaitUntil(() => File.Exists(workbook), Long, "the workbook");
        app.Invoke("TablesClose", window);

        using var zip = ZipFile.OpenRead(workbook);
        Sheet(zip, 1).Rows.ShouldBe(7);
        Sheet(zip, 1).Cells.ShouldBe(28);
        Sheet(zip, 1).Text.ShouldContain("Nguyễn Văn An");
        Sheet(zip, 2).Rows.ShouldBe(9);
        Sheet(zip, 2).Text.ShouldContain("Giấy in A4");
    }

    private static (int Rows, int Cells, string Text) Sheet(ZipArchive zip, int number)
    {
        using var reader = new StreamReader(zip.GetEntry($"xl/worksheets/sheet{number}.xml")!.Open());
        var xml = reader.ReadToEnd();
        return (Regex.Matches(xml, "<row ").Count, Regex.Matches(xml, "<c ").Count, xml);
    }
}
