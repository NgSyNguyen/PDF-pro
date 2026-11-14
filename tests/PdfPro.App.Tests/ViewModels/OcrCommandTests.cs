using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Ocr;
using PdfPro.Core.Ocr;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Ocr;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>
/// "Nhận dạng chữ (OCR)" of a tab: an engine is needed; the dialog's run becomes one content change (with the time
/// left shown and OCR's failures explained); the languages are remembered; the report, and the text and hOCR saved.
/// </summary>
public sealed class OcrCommandTests : IDisposable
{
    private static readonly OcrEngineInfo Installed = new("Tesseract", "tesseract.exe", OcrDataKind.Best, ["eng", "vie"], true);

    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly IPageDialogService _dialogs = Substitute.For<IPageDialogService>();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly IOcrEngine _engine = Substitute.For<IOcrEngine>();
    private readonly FakeSettingsService _settings = new(AppSettings.Default);

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task WithoutTheEngine_TheUserIsToldHowToGetIt()
    {
        _engine.Describe().Returns((OcrEngineInfo?)null);
        var tab = Factory(_engine).Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(2)));

        await tab.OcrCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Ocr_Title, Strings.Ocr_Error_NotInstalled);
        _dialogs.DidNotReceive().ShowOcr(Arg.Any<OcrViewModel>());
        Factory(null).Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(1))).OcrCommand.CanExecute(null).ShouldBeFalse("no engine registered");
    }

    [Fact]
    public async Task TheRun_IsOneContentChange_WithTheTimeLeft_AndTheLanguagesAreRemembered()
    {
        _engine.Describe().Returns(Installed);
        var tab = Factory(_engine).Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(4)));
        ContentChangeRequest? request = null;
        tab.ContentChangeRequested += (_, r) => request = r;
        _dialogs.ShowOcr(Arg.Any<OcrViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<OcrViewModel>();
            dialog.ChosenLanguages.ShouldBe(["vie"], "the default settings");
            dialog.Languages.Single(l => l.Code == "eng").IsChecked = true;
            dialog.Target.IsRange = true;
            dialog.Target.RangeText = "2-3";
            dialog.Parallelism = 2;
            dialog.ApplyCommand.Execute(null);
            return true;
        });

        await tab.OcrCommand.ExecuteAsync(null);

        var change = request.ShouldNotBeNull();
        change.Operation.ShouldBeOfType<OcrOperation>();
        var parameters = change.Parameters.ShouldBeOfType<OcrParams>();
        parameters.Languages.ShouldBe(["vie", "eng"]);
        parameters.Pages.ShouldBe([1, 2]);
        change.Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.History_OcrPages, 2));
        change.ShowsTimeLeft.ShouldBeTrue();
        change.Explain.ShouldNotBeNull()(new OcrException(OcrFailure.TimedOut)).ShouldBe(Strings.Ocr_Error_TimedOut);
        change.Explain(new IOException()).ShouldBeNull();
        _settings.Current.OcrLanguages.ShouldBe(["vie", "eng"]);
        _settings.Current.OcrParallelism.ShouldBe(2);
    }

    [Fact]
    public async Task PagesARedactionTurnedIntoPictures_AreOfferedToOcr_PreselectedInItsDialog()
    {
        _engine.Describe().Returns(Installed);
        var tab = Factory(_engine).Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(4)));
        _messages.AskAsync(Strings.Redaction_Done_Title, Arg.Any<string>(), Strings.Redaction_OcrRun, Arg.Is<string?>(s => s == null), Strings.Redaction_OcrLater).Returns(DialogChoice.Primary);
        OcrViewModel? shown = null;
        _dialogs.ShowOcr(Arg.Any<OcrViewModel>()).Returns(call =>
        {
            shown = call.Arg<OcrViewModel>();
            return false;
        });

        await tab.ReportRedactionAsync(new PdfPro.Operations.Security.RedactionReport(2, [2, 4], [2, 4], 0, 0, 0, [], false, 0), PdfPro.Operations.Security.RedactionMode.Raster);

        shown.ShouldNotBeNull().Target.IsSelected.ShouldBeTrue("the pages that became pictures are proposed");
        shown.Target.RangeText.ShouldBe(PdfPro.Core.Organizing.PageRanges.Format([1, 3]));

        await tab.ReportRedactionAsync(new PdfPro.Operations.Security.RedactionReport(1, [1], [], 0, 0, 0, [], false, 0), PdfPro.Operations.Security.RedactionMode.Vector);
        await _messages.Received(1).ShowInfoAsync(Strings.Redaction_Done_Title, Arg.Any<string>());
    }

    [Fact]
    public void Failures_AreExplained()
    {
        DocumentTabViewModel.OcrFailureMessage(new OcrException(OcrFailure.NotInstalled)).ShouldBe(Strings.Ocr_Error_NotInstalled);
        DocumentTabViewModel.OcrFailureMessage(new OcrException(OcrFailure.LanguageMissing, "eng"))
            .ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Error_LanguageMissing, Strings.Ocr_Language_eng));
        DocumentTabViewModel.OcrFailureMessage(new OcrException(OcrFailure.Failed)).ShouldBe(Strings.Ocr_Error_Failed);
    }

    [Fact]
    public void TheReport_SaysWhatWasRead_AndWhatIsWorthChecking()
    {
        var report = Report();

        var text = DocumentTabViewModel.DescribeOcr(report);

        text.ShouldStartWith(string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Report_Recognized, 2, 70.0, 30, 12.0));
        text.ShouldContain(string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Report_LowConfidence, 60.0, "3"));
        text.ShouldContain(string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Report_Turned, "3"));
        text.ShouldContain(string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Report_Blank, "2"));
        text.ShouldContain(string.Format(CultureInfo.CurrentCulture, Strings.Ocr_Report_HadText, "4"));
        text.ShouldNotContain(Strings.Ocr_Report_Failed[..10]);
        DocumentTabViewModel.DescribeOcr(report with { Pages = [report.Pages[1]] }).ShouldStartWith(Strings.Ocr_Report_Nothing);
    }

    [Fact]
    public async Task TheTextAndTheHocr_AreSaved_WhenAsked()
    {
        var tab = Factory(_engine).Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(4)));
        var folder = Path.Combine(Path.GetTempPath(), "ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        _files.PickOcrExportPath(Arg.Any<string>(), false).Returns(Path.Combine(folder, "a.txt"));
        _files.PickOcrExportPath(Arg.Any<string>(), true).Returns(Path.Combine(folder, "a.hocr"));
        try
        {
            await tab.ShowOcrReportAsync(Report(), new OcrDialogResult(new OcrParams(["vie"]), SaveText: true, SaveHocr: true));

            await _messages.Received(1).ShowInfoAsync(Strings.Ocr_Done_Title, Arg.Any<string>());
            (await File.ReadAllTextAsync(Path.Combine(folder, "a.txt"), Encoding.UTF8, TestContext.Current.CancellationToken)).ShouldBe("Ủy ban\fĐiều 1.");
            (await File.ReadAllTextAsync(Path.Combine(folder, "a.hocr"), Encoding.UTF8, TestContext.Current.CancellationToken)).ShouldContain("id='page_3'");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static OcrReport Report() => new(
        [
            new OcrPageReport(0, OcrPageOutcome.Recognized, 90, 20, 0, 0, "Ủy ban", "<html><body><div class='ocr_page' id='page_1' title='bbox 0 0 10 10; ppageno 0'></div></body></html>"),
            new OcrPageReport(1, OcrPageOutcome.Blank, null, 0, 0, 0, string.Empty),
            new OcrPageReport(2, OcrPageOutcome.Recognized, 50, 10, 180, 0, "Điều 1.", "<html><body><div class='ocr_page' id='page_1' title='bbox 0 0 10 10; ppageno 0'></div></body></html>"),
            new OcrPageReport(3, OcrPageOutcome.HadText, null, 0, 0, 0, string.Empty),
        ],
        60,
        [],
        [],
        TimeSpan.FromSeconds(12));

    private DocumentTabFactory Factory(IOcrEngine? engine)
        => new(
            new FakePageImageRenderer { AutoComplete = true },
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            new ManualDebouncerFactory(),
            TestOperations.Catalog(),
            _messages,
            _dialogs,
            _files,
            Substitute.For<IPdfDocumentLoader>(),
            NullLoggerFactory.Instance,
            settings: _settings,
            ocr: engine);
}
