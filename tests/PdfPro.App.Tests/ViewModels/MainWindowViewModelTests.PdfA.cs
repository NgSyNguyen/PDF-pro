using System.Text;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Core.ExternalTools;
using PdfPro.Operations.Converting;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"PDF/A" from the ribbon (veraPDF is played by the tool runner).</summary>
public sealed partial class MainWindowViewModelTests
{
    private readonly IVeraPdfLocator _veraPdf = Substitute.For<IVeraPdfLocator>();

    [Fact]
    public async Task PdfA_WritesAPdfA2bCopy_ChecksItWithVeraPdf_AndReportsBoth()
    {
        var (viewModel, path) = await CreateWithSampleAsync();
        var output = Path.Combine(Path.GetDirectoryName(path)!, Format(Strings.PdfA_Name, "hồ sơ") + ".pdf");
        _fileDialogs.PickSavePath(Format(Strings.PdfA_Name, "hồ sơ") + ".pdf", Path.GetDirectoryName(path)).Returns(output);
        _veraPdf.Find(Arg.Any<string?>()).Returns(new VeraPdfInstallation(@"C:\Users\lan\verapdf", @"C:\Java\bin\java.exe"));
        ExternalToolRequest? checkedWith = null;
        _tools.RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            checkedWith = call.Arg<ExternalToolRequest>();
            return new ExternalToolResult("java", 0, """<report><jobs><job><validationReport profileName="PDF/A-2B validation profile" isCompliant="true"><details failedRules="0"/></validationReport></job></jobs></report>""", string.Empty, TimeSpan.Zero, false);
        });
        PdfAViewModel? shown = null;
        _pageDialogs.ShowPdfA(Arg.Any<PdfAViewModel>()).Returns(call =>
        {
            shown = call.Arg<PdfAViewModel>();
            shown.ApplyCommand.Execute(null);
            return true;
        });
        string? message = null;
        _messages.AskAsync(Strings.PdfA_Done_Title, Arg.Any<string>(), Strings.Compress_Open, Arg.Is<string?>(p => p == null), Strings.Common_Close).Returns(call =>
        {
            message = call.ArgAt<string>(1);
            return DialogChoice.Cancel;
        });

        await viewModel.ToPdfACommand.ExecuteAsync(null);

        shown.ShouldNotBeNull().CanValidate.ShouldBeTrue();
        checkedWith.ShouldNotBeNull().Arguments[^1].ShouldBe(output, "veraPDF checks the copy");
        message.ShouldNotBeNull().ShouldStartWith(Format(Strings.PdfA_Report_Clean, Path.GetFileName(output), "PDF/A-2b"));
        message.ShouldContain(Format(Strings.PdfA_VeraPdf_Pass, "PDF/A-2b"));
        using var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var metadata = (pdf.Internals.Catalog.Elements["/Metadata"] as PdfSharp.Pdf.Advanced.PdfReference)!.Value as PdfDictionary;
        Encoding.UTF8.GetString(metadata!.Stream.Value).ShouldContain("<pdfaid:part>2</pdfaid:part>");
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.PdfA_Done, Path.GetFileName(output)));
    }

    [Fact]
    public async Task PdfA_WithoutVeraPdf_SaysTheCopyWasNotChecked_AndOpensItWhenAsked()
    {
        var (viewModel, path) = await CreateWithSampleAsync();
        var output = Path.Combine(Path.GetDirectoryName(path)!, "lưu trữ.pdf");
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(output);
        _veraPdf.Find(Arg.Any<string?>()).Returns((VeraPdfInstallation?)null);
        _pageDialogs.ShowPdfA(Arg.Any<PdfAViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<PdfAViewModel>();
            dialog.VeraPdfText.ShouldBe(Strings.PdfA_VeraPdf_Missing);
            dialog.IsA1B = true;
            dialog.ApplyCommand.Execute(null);
            return true;
        });
        string? message = null;
        _messages.AskAsync(Strings.PdfA_Done_Title, Arg.Any<string>(), Strings.Compress_Open, Arg.Is<string?>(p => p == null), Strings.Common_Close).Returns(call =>
        {
            message = call.ArgAt<string>(1);
            return DialogChoice.Primary;
        });

        await viewModel.ToPdfACommand.ExecuteAsync(null);

        message.ShouldNotBeNull().ShouldContain(Strings.PdfA_Report_NotValidated);
        message.ShouldContain("PDF/A-1b");
        await _tools.DidNotReceive().RunAsync(Arg.Any<ExternalToolRequest>(), Arg.Any<CancellationToken>());
        viewModel.ActiveDocument.ShouldNotBeNull().FilePath.ShouldBe(output, "the copy opens when asked");
    }

    [Fact]
    public void ThePdfAReport_ListsWhatIsLeft_WhatWasChanged_AndVeraPdfsRules()
    {
        var report = new PdfAReport(
            @"C:\a.pdf",
            PdfALevel.A1B,
            4,
            [new PdfAChange(PdfAFix.Metadata, 1), new PdfAChange(PdfAFix.Scripts, 2), new PdfAChange(PdfAFix.PagesAsPictures, 2)],
            [new PdfAProblem(PdfAProblemKind.FontNotEmbedded, [1, 2, 3], ".VnTime")],
            [2, 4]);
        var validation = new PdfAValidation(false, "PDF/A-1B validation profile", [new PdfARuleFailure("ISO 19005-1:2005", "6.3.4", 1, "The font programs for all fonts used within a conforming file shall be embedded", 3)]);

        var message = MainWindowViewModel.PdfAMessage("a.pdf", report, validation, null);

        var lines = message.Split(Environment.NewLine);
        lines[0].ShouldBe(Format(Strings.PdfA_Report_Problems, "a.pdf", "PDF/A-1b", 1));
        lines[1].ShouldBe("• " + Format(Strings.PdfA_Problem_FontNotEmbedded, "1-3", ".VnTime"));
        lines.ShouldContain("• " + Format(Strings.PdfA_Fix_Scripts, 2));
        lines.ShouldContain("• " + Format(Strings.PdfA_Fix_PagesAsPictures, 2, "2, 4"));
        lines.ShouldNotContain("• " + Format(Strings.PdfA_Fix_Metadata, 1), "the metadata is always written anew");
        lines.ShouldContain(Format(Strings.PdfA_VeraPdf_Fail, "PDF/A-1b", 1));
        lines[^1].ShouldBe("• 6.3.4-1: The font programs for all fonts used within a conforming file shall be embedded");
    }

    /// <summary>"hồ sơ.pdf" (basic-vi) open and saved.</summary>
    private async Task<(MainWindowViewModel ViewModel, string Path)> CreateWithSampleAsync()
    {
        var path = _fixture.CreateFile("hồ sơ.pdf", File.ReadAllBytes(Samples.Basic));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(6), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        return (viewModel, path);
    }
}
