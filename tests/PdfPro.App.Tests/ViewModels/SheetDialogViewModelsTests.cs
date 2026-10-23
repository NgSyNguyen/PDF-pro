using System.Globalization;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Layout;
using PdfPro.Operations.Layout;
using PdfPro.Operations.Materializing;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The interleave dialog and the sheet layout dialogs (N-up, booklet, poster).</summary>
public sealed class SheetDialogViewModelsTests
{
    private static readonly PdfSize A4 = new(595.28, 841.89);
    private static readonly string Output = Path.Combine(Path.GetTempPath(), "PdfPro.Tests", "layout.pdf");

    private readonly IFileDialogService _fileDialogs = Substitute.For<IFileDialogService>();
    private readonly ISourceRepairer _repairer = Substitute.For<ISourceRepairer>();

    [Fact]
    public void Interleave_SameDocument_ProposesTheSecondHalfAndPreviewsTheOrder()
    {
        var dialog = new InterleaveViewModel(6, () => Task.FromResult<InterleaveBackFile?>(null));

        dialog.BackStartText.ShouldBe("4");
        dialog.PreviewText.ShouldBe(Format(Strings.Interleave_Preview, "1, 6, 2, 5, 3, 4"));
        dialog.ReverseBacks = false;
        dialog.PreviewText.ShouldBe(Format(Strings.Interleave_Preview, "1, 4, 2, 5, 3, 6"));
        dialog.WarningText.ShouldBeNull();

        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldBe(new Operations.Organizing.InterleaveParams(BackStart: 4, ReverseBacks: false));
    }

    [Fact]
    public void Interleave_UnevenHalves_Warn_AndABadStartIsRefused()
    {
        var dialog = new InterleaveViewModel(6, () => Task.FromResult<InterleaveBackFile?>(null));

        dialog.BackStartText = "3";
        dialog.WarningText.ShouldBe(Format(Strings.Interleave_Unbalanced, 2, 4));
        dialog.BackStartText = "1";
        dialog.ErrorText.ShouldBe(Format(Strings.Interleave_Error_BackStart, 6));
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Interleave_BacksInAnotherFile_AreChosenThenUsed()
    {
        var dialog = new InterleaveViewModel(3, () => Task.FromResult<InterleaveBackFile?>(new InterleaveBackFile("s1", "mat-sau.pdf", 3)));
        dialog.IsOtherFile = true;
        dialog.ErrorText.ShouldBe(Strings.Interleave_Error_NoFile);

        await dialog.PickBackFileCommand.ExecuteAsync(null);

        dialog.BackFileText.ShouldBe(Format(Strings.Interleave_BackFile, "mat-sau.pdf", 3));
        dialog.PreviewText.ShouldStartWith(Format(Strings.Interleave_Preview, "1, s3, 2, s2, 3, s1"));
        dialog.ApplyCommand.Execute(null);
        dialog.Result.ShouldBe(new Operations.Organizing.InterleaveParams(BackSourceId: "s1", ReverseBacks: true));
    }

    [Fact]
    public void NUp_DrawsTheFirstSheetAndCountsSheets()
    {
        var dialog = Layout(SheetLayoutKind.NUp, 5);

        dialog.PreviewItems.Select(i => i.Label).ShouldBe(["1", "2"]);
        dialog.SheetCountText.ShouldBe(Format(Strings.Layout_SheetCount, 3));
        dialog.PreviewWidth.ShouldBe(SheetLayoutViewModel.PreviewSize, 0.01, "two portrait pages go on a landscape sheet");
        dialog.PreviewItems[0].Left.ShouldBeLessThan(dialog.PreviewItems[1].Left);

        dialog.PagesPerSheet = 4;
        dialog.PreviewItems.Count.ShouldBe(4);
        dialog.SheetCountText.ShouldBe(Format(Strings.Layout_SheetCount, 2));
        dialog.IsColumnsFirst = true;
        dialog.PreviewItems[1].Top.ShouldBeGreaterThan(dialog.PreviewItems[0].Top, "N order fills the column first");

        dialog.RunCommand.Execute(null);

        var result = dialog.Result.ShouldNotBeNull();
        var parameters = result.Parameters.ShouldBeOfType<NUpParams>();
        parameters.PagesPerSheet.ShouldBe(4);
        parameters.Order.ShouldBe(NUpOrder.ColumnsFirst);
        parameters.Margin.ShouldBe(14.17, 0.01);
        result.OutputPath.ShouldBe(Output);
    }

    [Fact]
    public void NUp_MarginsThatLeaveNoRoom_AreRefused()
    {
        var dialog = Layout(SheetLayoutKind.NUp, 4);

        dialog.MarginText = "abc";
        dialog.ErrorText.ShouldBe(Strings.Layout_Error_Number);
        dialog.MarginText = "160";
        dialog.PagesPerSheet = 16;
        dialog.ErrorText.ShouldBe(Strings.Layout_Error_Room);
        dialog.RunCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void Booklet_ShowsTheFrontOfTheFirstSheet()
    {
        var dialog = Layout(SheetLayoutKind.Booklet, 10);

        dialog.SheetCountText.ShouldBe(Format(Strings.Layout_SheetCount, 6));
        dialog.PreviewItems.Select(i => i.Label).ShouldBe(["1"], "page 12 (left of page 1) is blank");
        dialog.PreviewItems[0].Left.ShouldBeGreaterThan(SheetLayoutViewModel.PreviewSize / 2 - 1);
        dialog.IsRightBinding = true;
        dialog.PreviewItems[0].Left.ShouldBeLessThan(SheetLayoutViewModel.PreviewSize / 2);

        dialog.RunCommand.Execute(null);
        dialog.Result!.Parameters.ShouldBeOfType<BookletParams>().Binding.ShouldBe(BookletBinding.Right);
    }

    [Fact]
    public void Tile_ShowsTheSheetsOfTheFirstPage()
    {
        var dialog = Layout(SheetLayoutKind.Tile, 1);

        dialog.ScaleText = "200";
        dialog.PreviewItems[0].Label.ShouldBe("A1");
        dialog.PreviewItems.Count.ShouldBeGreaterThan(1);

        dialog.UsesFit = true;
        dialog.ColumnsText = "2";
        dialog.RowsText = "2";
        dialog.PreviewItems.Select(i => i.Label).ShouldBe(["A1", "A2", "B1", "B2"]);
        dialog.SheetCountText.ShouldBe(Format(Strings.Layout_SheetCount, 4));
        dialog.RowsText = "0";
        dialog.ErrorText.ShouldBe(Strings.Layout_Error_Number);
    }

    [Fact]
    public void Layout_NeedsAFullOutputPathAndAValidPageList()
    {
        var dialog = Layout(SheetLayoutKind.Tile, 3);

        dialog.PagesText = "7";
        dialog.ErrorText.ShouldBe(Strings.Layout_Error_Pages);
        dialog.PagesText = "2";
        dialog.OutputPath = "x.pdf";
        dialog.ErrorText.ShouldBe(Strings.Layout_Error_Output);
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(Output);
        dialog.PickOutputCommand.Execute(null);
        dialog.ErrorText.ShouldBeNull();
    }

    private SheetLayoutViewModel Layout(SheetLayoutKind kind, int pages)
        => new(kind, [.. Enumerable.Repeat(A4, pages)], Output, _fileDialogs, new NUpOperation(_repairer), new BookletOperation(_repairer), new TileOperation(_repairer));

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
