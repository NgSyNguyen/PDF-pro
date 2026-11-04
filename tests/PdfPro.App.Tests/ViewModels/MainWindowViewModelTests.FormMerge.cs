using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Forms;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Tables;
using PdfPro.Operations.Forms;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Trộn dữ liệu": the filled copies written in the background, what did not fit, failures.</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task FormMerge_WritesOneFilledFilePerRow_NamedByTheChosenColumn()
    {
        var (viewModel, document) = await OpenFormAsync();
        var folder = Path.Combine(_fixture.CreateFolder("trộn"), "kết quả");
        var request = MergeRequest("hoTen,ma\nAn,HS-01\nBình,HS-02\n", folder, nameColumn: "ma");

        (await viewModel.MergeFormAsync(document, request)).ShouldBeTrue();

        Directory.GetFiles(folder).Select(Path.GetFileName).Order().ShouldBe(["HS-01.pdf", "HS-02.pdf"]);
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.FormMerge_Done, 2, "kết quả"));
        viewModel.StatusBar.IsTaskRunning.ShouldBeFalse();
        await _messages.DidNotReceiveWithAnyArgs().ShowInfoAsync(default!, default!);
        _shell.Received(1).OpenFolder(folder);
    }

    [Fact]
    public async Task FormMerge_ListsTheColumnsAndValuesThatDidNotFit()
    {
        var (viewModel, document) = await OpenFormAsync();
        var folder = _fixture.CreateFolder("trộn");

        (await viewModel.MergeFormAsync(document, MergeRequest("hoTen,dongY,ghiChu\nAn,có lẽ,x\n", folder))).ShouldBeTrue();

        await _messages.Received(1).ShowInfoAsync(
            Strings.FormMerge_Title,
            Arg.Is<string>(m => m.Contains("ghiChu", StringComparison.Ordinal) && m.Contains(Format(Strings.FormMerge_Problem, 1, "dongY"), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FormMerge_OfADocumentWithoutAForm_IsExplained()
    {
        var (viewModel, path) = await CreateWithFileAsync("trang.pdf");
        var folder = _fixture.CreateFolder("trộn");

        (await viewModel.MergeFormAsync(viewModel.ActiveDocument!, MergeRequest("hoTen\nAn\n", folder))).ShouldBeFalse();

        await _messages.Received(1).ShowErrorAsync(Strings.FormMerge_Error_Title, Strings.FormMerge_Error_NoForm);
        viewModel.StatusBar.TaskText.ShouldBe(Strings.FormMerge_FailedStatus);
        File.Exists(path).ShouldBeTrue();
        Directory.Exists(folder).ShouldBeFalse("nothing was written");
    }

    private static FormMergeRequest MergeRequest(string csv, string folder, string? nameColumn = null)
        => new(new MergeFormDataOperation(), new FormMergeDialogResult(CsvTable.Parse(csv), new MergeFormParams("du-lieu.csv", nameColumn), folder, OpenFolderWhenDone: nameColumn is not null));

    /// <summary>An open document whose file is a real form: a name and a check box.</summary>
    private async Task<(MainWindowViewModel ViewModel, DocumentTabViewModel Document)> OpenFormAsync()
    {
        using var stream = new MemoryStream();
        using (var form = new PdfSharp.Pdf.PdfDocument())
        {
            form.AddPage();
            FormWriter.AddFields(form,
            [
                new FormFieldSpec("hoTen", FormFieldKind.Text, 0, new PdfRect(100, 700, 300, 720)),
                new FormFieldSpec("dongY", FormFieldKind.CheckBox, 0, new PdfRect(100, 650, 114, 664)),
            ]);
            form.Save(stream, closeStream: false);
        }

        var path = _fixture.CreateFile("đơn.pdf", stream.ToArray());
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(1), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        return (viewModel, viewModel.ActiveDocument.ShouldNotBeNull());
    }
}
