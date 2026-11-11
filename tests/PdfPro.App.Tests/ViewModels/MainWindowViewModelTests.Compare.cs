using NSubstitute;
using PdfPro.App.Services;
using PdfPro.App.Services.Comparing;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Comparing;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"So sánh" in the ribbon: the window opens with the active document as the old version and another open one as the new version.</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task Compare_NeedsNoOpenDocument()
    {
        var viewModel = Create(isDebugBuild: false);
        CompareViewModel? shown = null;
        _pageDialogs.ShowCompare(Arg.Do<CompareViewModel>(d => shown = d)).Returns(false);

        await viewModel.CompareDocumentsCommand.ExecuteAsync(null);

        shown.ShouldNotBeNull();
        (shown.OldPath, shown.NewPath).ShouldBe((null, null));
    }

    [Fact]
    public async Task Compare_StartsWithTheActiveDocument_AgainstAnotherOpenOne()
    {
        var first = _fixture.CreateFile("hợp đồng 1.pdf", File.ReadAllBytes(Samples.ContractV1));
        var second = _fixture.CreateFile("hợp đồng 2.pdf", File.ReadAllBytes(Samples.ContractV2));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(4), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([first]);
        await viewModel.OpenFilesAsync([second]);
        CompareViewModel? shown = null;
        _pageDialogs.ShowCompare(Arg.Do<CompareViewModel>(d => shown = d)).Returns(false);

        await viewModel.CompareDocumentsCommand.ExecuteAsync(null);

        shown.ShouldNotBeNull();
        (shown.OldName, shown.NewName).ShouldBe(("hợp đồng 2.pdf", "hợp đồng 1.pdf"));
        (shown.OldPath, shown.NewPath).ShouldBe((viewModel.ActiveDocument!.FilePath, viewModel.Documents.First().FilePath));
    }

    [Fact]
    public async Task ExtractTables_OpensTheTableWindow_OnTheActiveDocument()
    {
        var path = _fixture.CreateFile("bảng biểu.pdf", File.ReadAllBytes(Samples.Tables));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(2), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        PdfPro.App.ViewModels.Extracting.TablesViewModel? shown = null;
        _pageDialogs.ShowTables(Arg.Do<PdfPro.App.ViewModels.Extracting.TablesViewModel>(d => shown = d)).Returns(false);

        await viewModel.ExtractTablesCommand.ExecuteAsync(null);

        shown.ShouldNotBeNull();
        shown.FileName.ShouldBe("bảng biểu.pdf");
        shown.PageCount.ShouldBe(2);
    }

    private PdfPro.App.Services.Extracting.ExtractingServices Extracting()
        => new(new PdfPro.Operations.Extracting.ExtractTablesOperation(), Substitute.For<IPdfDocumentLoader>(), new FakePageImageRenderer());

    private CompareServices Compare()
        => new(Substitute.For<IDocumentComparison>(), Substitute.For<IPdfDocumentLoader>(), new FakePageImageRenderer(), Substitute.For<IPasswordPrompt>());
}
