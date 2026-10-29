using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.ExternalTools;
using PdfSharp.Pdf.IO;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Tối ưu cho web" and "Sửa tệp hỏng".</summary>
public sealed partial class MainWindowViewModelTests
{
    private readonly IExternalToolRunner _tools = Substitute.For<IExternalToolRunner>();

    [Fact]
    public async Task Repair_ABrokenFile_IsReadBack_Explained_AndOpened()
    {
        var input = _fixture.CreateFile("hỏng.pdf", File.ReadAllBytes(Samples.Broken));
        var output = Path.Combine(Path.GetDirectoryName(input)!, Format(Strings.Repair_Name, "hỏng") + ".pdf");
        _fileDialogs.PickPdfFile(Strings.Repair_Pick_Title).Returns(input);
        _fileDialogs.PickSavePath(Format(Strings.Repair_Name, "hỏng") + ".pdf", Path.GetDirectoryName(input)).Returns(output);
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(3), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);

        await viewModel.RepairCommand.ExecuteAsync(null);

        using (var repaired = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            repaired.PageCount.ShouldBe(3);
        }

        await _messages.Received(1).ShowInfoAsync(Strings.Repair_Done_Title, Format(Strings.Repair_Done_Message, Strings.Repair_Method_CrossReference, 3));
        viewModel.ActiveDocument.ShouldNotBeNull().FilePath.ShouldBe(output, "the repaired file opens");
    }

    [Fact]
    public async Task Repair_AFileNothingReads_SaysWhatWasTried()
    {
        var input = _fixture.CreateFile("rác.pdf", "%PDF-1.7\nnot a document\n"u8.ToArray());
        var output = Path.Combine(Path.GetDirectoryName(input)!, "ra.pdf");
        _fileDialogs.PickPdfFile(Strings.Repair_Pick_Title).Returns(input);
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(output);
        var viewModel = Create(isDebugBuild: false);

        await viewModel.RepairCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(
            Strings.Repair_Error_Title,
            Arg.Is<string>(m => m.StartsWith(Strings.Repair_Error_Message, StringComparison.Ordinal)
                && m.Contains(Format(Strings.Repair_Error_Line, Strings.Repair_Method_Qpdf, Strings.Repair_Failure_Missing), StringComparison.Ordinal)));
        File.Exists(output).ShouldBeFalse();
        viewModel.Documents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Linearize_WritesAWebCopy_OfTheDocumentsOwnFile()
    {
        var (viewModel, path) = await CreateWithFileAsync("a.pdf");
        var output = Path.Combine(Path.GetDirectoryName(path)!, Format(Strings.Linearize_Name, "a") + ".pdf");
        _fileDialogs.PickSavePath(Format(Strings.Linearize_Name, "a") + ".pdf", Path.GetDirectoryName(path)).Returns(output);
        ExternalToolRequest? ran = null;
        _tools.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            ran = call.Arg<ExternalToolRequest>();
            File.Copy(ran.Arguments[^2], ran.Arguments[^1], overwrite: true);
            return new ExternalToolResult("qpdf", 0, string.Empty, string.Empty, TimeSpan.Zero, false);
        });

        await viewModel.LinearizeCommand.ExecuteAsync(null);

        ran.ShouldNotBeNull().Arguments.ShouldContain("--linearize");
        ran.Arguments[^2].ShouldBe(path, "nothing was unsaved: the document's own file is laid out");
        File.Exists(output).ShouldBeTrue();
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Linearize_Done, Path.GetFileName(output)));
    }

    [Fact]
    public async Task Linearize_WithoutQpdf_SaysHowToGetIt()
    {
        var (viewModel, path) = await CreateWithFileAsync("a.pdf");
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(Path.Combine(Path.GetDirectoryName(path)!, "web.pdf"));
        _tools.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ExternalToolNotFoundException("qpdf"));

        await viewModel.LinearizeCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Linearize_Error_Title, Strings.Optimize_Error_NoQpdf);
    }
}
