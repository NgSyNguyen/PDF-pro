using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services.Web;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Converting;
using PdfPro.Core.Operations;
using PdfPro.Core.Web;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Trang web sang PDF" from the ribbon, and HTML files opened like PDFs.</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task AWebPage_IsPrintedWhereTheUserSaves_AndOpens()
    {
        var web = Printer();
        var output = _fixture.CreateFile("ví dụ.pdf");
        File.Delete(output);
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(2), call.ArgAt<string>(0)));
        _pageDialogs.ShowWebToPdf(Arg.Any<WebToPdfViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<WebToPdfViewModel>();
            dialog.ApplyCommand.CanExecute(null).ShouldBeFalse("no address yet");
            dialog.Address = "www.example.com/gioi-thieu";
            dialog.Landscape = true;
            dialog.Paper = WebPaper.A3;
            dialog.ApplyCommand.Execute(null);
            return true;
        });
        _fileDialogs.PickSavePath("example.com - gioi-thieu.pdf", null).Returns(output);
        var viewModel = Create(isDebugBuild: false, web: web);

        viewModel.WebToPdfCommand.CanExecute(null).ShouldBeTrue("no document is needed");
        await viewModel.WebToPdfCommand.ExecuteAsync(null);

        await web.Received(1).PrintAsync(
            new Uri("https://www.example.com/gioi-thieu"),
            output,
            new WebPrintSettings(WebPaper.A3, Landscape: true),
            true,
            Arg.Any<IProgress<OperationProgress>>(),
            Arg.Any<CancellationToken>());
        viewModel.ActiveDocument.ShouldNotBeNull().FileName.ShouldBe("ví dụ.pdf");
    }

    [Fact]
    public async Task WithoutTheWebView2Runtime_TheUserIsToldWhereToGetIt()
    {
        var web = Substitute.For<IWebPagePrinter>();
        web.RuntimeVersion.Returns((string?)null);
        var viewModel = Create(isDebugBuild: false, web: web);

        await viewModel.WebToPdfCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.WebToPdf_Error_Title, Strings.WebToPdf_Error_NoRuntime);
        _pageDialogs.DidNotReceiveWithAnyArgs().ShowWebToPdf(default!);
    }

    [Fact]
    public async Task APageThatDoesNotLoad_IsExplained()
    {
        var web = Printer();
        web.PrintAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<WebPrintSettings>(), Arg.Any<bool>(), Arg.Any<IProgress<OperationProgress>?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new WebPrintException(WebPrintFailure.HttpError, 404));
        _pageDialogs.ShowWebToPdf(Arg.Any<WebToPdfViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<WebToPdfViewModel>();
            dialog.Address = "https://example.com/khong-co";
            dialog.ApplyCommand.Execute(null);
            return true;
        });
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(_fixture.CreateFile("lỗi.pdf"));
        var viewModel = Create(isDebugBuild: false, web: web);

        await viewModel.WebToPdfCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.WebToPdf_Error_Title, Format(Strings.WebToPdf_Error_Http, 404));
        viewModel.ActiveDocument.ShouldBeNull();
    }

    [Fact]
    public async Task AnHtmlFile_OpensLikeAPdf_ThroughAPdfBesideIt()
    {
        var web = Printer();
        var page = _fixture.CreateFile("trang.html");
        File.WriteAllBytes(Path.ChangeExtension(page, ".pdf"), [1]); // "trang.pdf" is taken: "trang (2).pdf"
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(1), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false, web: web);

        await viewModel.OpenFilesAsync([page]);

        var expected = Path.Combine(Path.GetDirectoryName(page)!, "trang (2).pdf");
        await web.Received(1).PrintAsync(new Uri(page), expected, new WebPrintSettings(), false, Arg.Any<IProgress<OperationProgress>?>(), Arg.Any<CancellationToken>());
        viewModel.ActiveDocument.ShouldNotBeNull().FileName.ShouldBe("trang (2).pdf");
    }

    [Fact]
    public void TheDialog_ReadsAddressesAndFiles_AndSaysWhatIsWrong()
    {
        var html = _fixture.CreateFile("có.html");
        _fileDialogs.PickHtmlFile().Returns(html);
        var dialog = new WebToPdfViewModel(_fileDialogs);

        dialog.ErrorText.ShouldBeNull("nothing typed yet");
        dialog.Address = "javascript:alert(1)";
        dialog.ErrorText.ShouldBe(Strings.WebToPdf_Error_Address);
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
        dialog.Address = Path.Combine(Path.GetDirectoryName(html)!, "không có.html");
        dialog.ErrorText.ShouldBe(Format(Strings.WebToPdf_Error_NoFile, "không có.html"));
        dialog.PickFileCommand.Execute(null);
        dialog.ErrorText.ShouldBeNull();
        dialog.Margins = WebMargins.None;
        dialog.Backgrounds = false;
        dialog.HeaderAndFooter = true;
        dialog.ApplyCommand.Execute(null);

        dialog.Result.ShouldBe(new WebToPdfDialogResult(new Uri(html), new WebPrintSettings(Margins: WebMargins.None, Backgrounds: false, HeaderAndFooter: true), OpenWhenDone: true));
        dialog.PaperOptions.Select(o => o.Value).ShouldBe([WebPaper.A4, WebPaper.A3, WebPaper.A5, WebPaper.Letter, WebPaper.Legal]);
    }

    /// <summary>A printer that "prints" a small PDF into the place asked.</summary>
    private static IWebPagePrinter Printer()
    {
        var web = Substitute.For<IWebPagePrinter>();
        web.RuntimeVersion.Returns("157.0.4341.60");
        web.PrintAsync(Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<WebPrintSettings>(), Arg.Any<bool>(), Arg.Any<IProgress<OperationProgress>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var output = call.ArgAt<string>(1);
            File.Copy(Samples.Basic, output, overwrite: true);
            return new WebPrintResult(output, "Tiêu đề");
        });
        return web;
    }
}
