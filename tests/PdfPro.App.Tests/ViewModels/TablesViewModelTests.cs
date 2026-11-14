using System.Data;
using System.Globalization;
using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Extracting;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Extracting;
using PdfPro.Core.Tables;
using PdfPro.Operations.Extracting;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>
/// "Trích bảng" on samples/tables.pdf for real: both tables found and previewed cell by cell, a box drawn on a page read
/// the way asked, tables left out or removed, and the rest written as Excel or CSV.
/// </summary>
public sealed class TablesViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly IShellLauncher _shell = Substitute.For<IShellLauncher>();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task BothTables_AreFound_ListedAndPreviewed_TheFirstBoxedOnItsPage()
    {
        await using var dialog = Create();

        await dialog.FindCommand.ExecuteAsync(null);
        await dialog.Showing;

        dialog.ErrorText.ShouldBeNull();
        dialog.StatusText.ShouldBe(Format(Strings.Tables_Found, 2));
        dialog.Tables.Select(t => t.Title).ShouldBe([Format(Strings.Tables_Item, 1, 1), Format(Strings.Tables_Item, 2, 2)]);
        dialog.Tables[0].Details.ShouldBe(Format(Strings.Tables_ItemSize, 7, 4, Strings.Tables_Method_Lattice));
        dialog.SelectedTable.ShouldBe(dialog.Tables[0]);
        var preview = dialog.SelectedTable!.Preview;
        preview.Count.ShouldBe(7);
        preview.Table!.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ShouldBe(["A", "B", "C", "D"]);
        preview[1]["B"].ShouldBe("Nguyễn Văn An");
        dialog.ShownPage.ShouldBe(0);
        dialog.PageImage.ShouldNotBeNull();
        dialog.PageWidth.ShouldBe(595 * TablesViewModel.DipPerPoint, 1);
        dialog.Boxes.ShouldHaveSingleItem().IsSelected.ShouldBeTrue();

        dialog.SelectedTable = dialog.Tables[1];
        await dialog.Showing;

        dialog.ShownPage.ShouldBe(1);
        dialog.PageText.ShouldBe(Format(Strings.Tables_Page, 2, 2));
        dialog.Boxes.ShouldHaveSingleItem().Item.ShouldBe(dialog.Tables[1]);
    }

    [Fact]
    public async Task ABoxDrawnOnThePage_IsReadTheWayAsked_AndReplacesTheTableItCovers()
    {
        await using var dialog = Create();
        await dialog.FindCommand.ExecuteAsync(null);
        dialog.SelectedTable = dialog.Tables[1];
        await dialog.Showing;
        dialog.Method = TableMethod.Lattice;

        // Around the table of page 2 (x 60–520, y 500–710 in points), in pixels of the page at 100 %.
        await dialog.ReadAreaCommand.ExecuteAsync(new Rect(80, 176, 613, 280));

        dialog.StatusText.ShouldBe(Strings.Tables_NoTableInArea, "page 2 has no ruled lines");
        dialog.Tables.Count.ShouldBe(2);

        dialog.Method = TableMethod.Stream;
        await dialog.ReadAreaCommand.ExecuteAsync(new Rect(80, 176, 613, 280));

        dialog.Tables.Count.ShouldBe(2, "the table it covers is replaced");
        var read = dialog.SelectedTable.ShouldNotBeNull();
        read.ShouldBe(dialog.Tables[1]);
        (read.Table.Page, read.Table.Method, read.Table.RowCount, read.Table.ColumnCount).ShouldBe((1, TableMethod.Stream, 9, 4));
        read.Number.ShouldBe(2);
        dialog.StatusText.ShouldBe(Format(Strings.Tables_Read, 9, 4));
    }

    [Fact]
    public async Task TheTablesKept_AreWrittenAsAWorkbook_AndTheFolderOpens()
    {
        var path = _temp.Combine("bảng lương.xlsx");
        _files.PickTableSavePath("tables", _temp.Path, false).Returns(path);
        await using var dialog = Create();
        await dialog.FindCommand.ExecuteAsync(null);
        dialog.Tables[1].IsIncluded = false;

        await dialog.ExportCommand.ExecuteAsync(null);

        dialog.ErrorText.ShouldBeNull();
        dialog.ReportText.ShouldBe(Format(Strings.Tables_Written, 1, "bảng lương.xlsx"));
        await using (var stream = File.OpenRead(path))
        {
            var table = XlsxTable.Read(stream);
            table.Columns.ShouldBe(["STT", "Họ và tên", "Đơn vị", "Số tiền (đồng)"]);
            table.Rows.Count.ShouldBe(6);
            table.Rows[0][3].ShouldBe("1250000", "Vietnamese numbers become numbers");
        }

        dialog.OpenFolderCommand.Execute(null);
        _shell.Received(1).OpenFolder(_temp.Path);
    }

    [Fact]
    public async Task AsCsv_EachTableIsAFile_AndRemovedTablesAreLeftOut()
    {
        _files.PickTableSavePath(Arg.Any<string>(), Arg.Any<string?>(), true).Returns(_temp.Combine("vpp.csv"));
        await using var dialog = Create();
        await dialog.FindCommand.ExecuteAsync(null);
        dialog.Format = TableFileFormat.Csv;
        dialog.Separator = CsvSeparator.Semicolon;

        await dialog.ExportCommand.ExecuteAsync(null);

        dialog.ReportText.ShouldBe(Format(Strings.Tables_WrittenFiles, 2, "vpp-1.csv", "vpp-2.csv"));
        CsvTable.Read(File.ReadAllBytes(_temp.Combine("vpp-2.csv"))).Rows[7].ShouldBe(["VPP-08", "Ghim bấm", "90", "6.000"]);

        dialog.SelectedTable = dialog.Tables[0];
        dialog.RemoveCommand.Execute(null);

        dialog.Tables.ShouldHaveSingleItem().Number.ShouldBe(1);
        dialog.SelectedTable.ShouldBe(dialog.Tables[0]);
        dialog.Tables[0].Table.Page.ShouldBe(1);
    }

    [Fact]
    public async Task OnlyThePagesAsked_AreSearched_AndWrongPagesAreExplained()
    {
        await using var dialog = Create();
        dialog.PagesText = "7";

        await dialog.FindCommand.ExecuteAsync(null);

        dialog.ErrorText.ShouldBe(Format(Strings.Tables_Error_Pages, 2));
        dialog.Tables.ShouldBeEmpty();

        dialog.PagesText = "2";
        await dialog.FindCommand.ExecuteAsync(null);

        dialog.ErrorText.ShouldBeNull();
        dialog.Tables.ShouldHaveSingleItem().Table.Page.ShouldBe(1);
        dialog.ExportCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task NothingTicked_IsNotExported()
    {
        await using var dialog = Create();
        await dialog.FindCommand.ExecuteAsync(null);
        foreach (var table in dialog.Tables)
        {
            table.IsIncluded = false;
        }

        await dialog.ExportCommand.ExecuteAsync(null);

        dialog.ErrorText.ShouldBe(Strings.Tables_Error_NoneChosen);
        _files.DidNotReceiveWithAnyArgs().PickTableSavePath(default!, default, default);
    }

    private TablesViewModel Create()
    {
        var services = new ExtractingServices(new ExtractTablesOperation(), new PdfDocumentLoader(TestOperations.PdfiumGate), new PageImageRenderer());
        return new TablesViewModel(new TablesSource("tables.pdf", Samples.Tables, 2, 0, _temp.Path), services, _files, _shell, NullLogger.Instance);
    }

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
