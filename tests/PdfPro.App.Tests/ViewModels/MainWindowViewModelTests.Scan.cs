using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Scanning;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Splitting;
using PdfPro.Core.Ocr;
using PdfPro.Operations.Scanning;
using PdfPro.Operations.Splitting;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>
/// The scan tools of the ribbon on samples/scan-vi.pdf: blank sheets found and removed (or shown in the organizer),
/// turned pages, tilted pages and dark borders listed before anything changes, and the split at blank sheets.
/// </summary>
public sealed partial class MainWindowViewModelTests
{
    private readonly IOcrEngine _ocr = Substitute.For<IOcrEngine>();

    [Fact]
    public async Task RemoveBlankPages_FindsTheSeparatorSheets_AndRemovesThemAsOneStep()
    {
        var viewModel = await CreateWithScanAsync();
        string? message = null;
        _messages.AskAsync(Strings.Scan_Blank_Title, Arg.Any<string>(), Strings.Scan_Blank_Remove, Strings.Scan_ShowInOrganizer, Strings.Common_Cancel).Returns(call =>
        {
            message = call.ArgAt<string>(1);
            return DialogChoice.Primary;
        });

        await viewModel.RemoveBlankPagesCommand.ExecuteAsync(null);

        message.ShouldBe(Format(Strings.Scan_Blank_Found, 4, "4, 7, 10, 14"));
        var document = viewModel.ActiveDocument.ShouldNotBeNull();
        document.Viewer.PageCount.ShouldBe(14);
        document.UndoCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveBlankPages_CanShowTheSheetsInTheOrganizerFirst()
    {
        var viewModel = await CreateWithScanAsync();
        _messages.AskAsync(Strings.Scan_Blank_Title, Arg.Any<string>(), Strings.Scan_Blank_Remove, Strings.Scan_ShowInOrganizer, Strings.Common_Cancel).Returns(DialogChoice.Secondary);

        await viewModel.RemoveBlankPagesCommand.ExecuteAsync(null);

        var document = viewModel.ActiveDocument.ShouldNotBeNull();
        document.IsOrganizing.ShouldBeTrue();
        document.Organizer.ShouldNotBeNull().SelectedIndices.ShouldBe([3, 6, 9, 13]);
        document.Viewer.PageCount.ShouldBe(18, "nothing removed yet");
    }

    [Fact]
    public async Task AutoRotate_NeedsOrientationData_ThenTurnsThePagesFound()
    {
        var viewModel = await CreateWithScanAsync();
        _ocr.Describe().Returns(new OcrEngineInfo("Tesseract", "tesseract.exe", OcrDataKind.Best, ["vie"], DetectsOrientation: false));

        await viewModel.AutoRotateCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Scan_Rotate_Title, Strings.Scan_Rotate_NoEngine);

        _ocr.Describe().Returns(new OcrEngineInfo("Tesseract", "tesseract.exe", OcrDataKind.Best, ["vie"], DetectsOrientation: true));
        _ocr.DetectOrientationAsync(Arg.Any<OcrImage>(), Arg.Any<CancellationToken>()).Returns(new OcrOrientation(180, 30, "Latin"));
        _messages.AskAsync(Strings.Scan_Rotate_Title, Arg.Any<string>(), Strings.Scan_Rotate_Apply, Strings.Scan_ShowInOrganizer, Strings.Common_Cancel).Returns(DialogChoice.Primary);

        await viewModel.AutoRotateCommand.ExecuteAsync(null);

        var plan = viewModel.ActiveDocument.ShouldNotBeNull().Workspace.Session.Plan;
        plan.Where((_, i) => i is not (3 or 6 or 9 or 13)).ShouldAllBe(p => p.RotationDelta == 180, "every page with ink was said to be upside down");
        plan[3].RotationDelta.ShouldBe(0, "blank sheets are not asked about");
    }

    [Fact]
    public async Task Deskew_ListsTheTiltedPages_ThenStraightensThemAsOneContentChange()
    {
        var viewModel = await CreateWithScanAsync();
        _messages.AskAsync(Strings.Scan_Deskew_Title, Arg.Any<string>(), Strings.Scan_Deskew_Apply, Strings.Scan_ShowInOrganizer, Strings.Common_Cancel).Returns(DialogChoice.Primary);

        await viewModel.DeskewCommand.ExecuteAsync(null);

        var call = _contentChanger.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IContentChanger.ApplyAsync));
        call.GetArguments()[1].ShouldBeOfType<DeskewOperation>();
        var parameters = call.GetArguments()[2].ShouldBeOfType<DeskewParams>();
        parameters.Pages.Select(p => p.Page).ShouldContain(1);
        parameters.Pages.Select(p => p.Page).ShouldContain(7);
        parameters.Pages.Single(p => p.Page == 7).Degrees.ShouldBe(-1.145, 0.25);
    }

    [Fact]
    public async Task CleanBorders_OnPagesWithoutDarkEdges_SaysSo()
    {
        var path = _fixture.CreateFile("văn bản.pdf", File.ReadAllBytes(Samples.Basic));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(6), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);

        await viewModel.CleanBordersCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowInfoAsync(Strings.Scan_Borders_Title, Strings.Scan_Borders_None);
        _contentChanger.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task SplitByBlankPages_OpensTheSplitDialog_AtBlankSheets()
    {
        var viewModel = await CreateWithScanAsync();
        SplitMode? mode = null;
        _pageDialogs.ShowSplit(Arg.Do<SplitViewModel>(d => mode = d.SelectedMode.Mode)).Returns(false);

        viewModel.SplitByBlankPagesCommand.Execute(null);

        mode.ShouldBe(SplitMode.BlankSeparators);
    }

    private async Task<MainWindowViewModel> CreateWithScanAsync()
    {
        var path = _fixture.CreateFile("bản quét.pdf", File.ReadAllBytes(Samples.ScanVi));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(18), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        return viewModel;
    }

    private ScanServices Scan()
    {
        var operations = TestOperations.Catalog();
        return new ScanServices(
            TestOperations.PdfiumGate,
            operations.Get<RemoveBlankPagesOperation>(),
            operations.Get<AutoRotateOperation>(),
            operations.Get<DeskewOperation>(),
            operations.Get<CleanBordersOperation>(),
            _ocr);
    }
}
