using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Security;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Security;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Làm sạch tài liệu": the dialog, the content change it asks for, and the report.</summary>
public sealed class SanitizeCommandTests : IDisposable
{
    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly IPageDialogService _dialogs = Substitute.For<IPageDialogService>();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly DocumentTabFactory _factory;

    public SanitizeCommandTests()
        => _factory = new DocumentTabFactory(
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
            NullLoggerFactory.Instance);

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task TheChosenItems_AreCleanedAsOneContentChange()
    {
        var tab = _factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(2)));
        ContentChangeRequest? request = null;
        tab.ContentChangeRequested += (_, r) => request = r;
        _dialogs.ShowSanitize(Arg.Any<SanitizeViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<SanitizeViewModel>();
            dialog.IsSigned.ShouldBeFalse();
            dialog.ExternalLinks.ShouldBeFalse("external links are kept unless asked");
            dialog.ExternalLinks = true;
            dialog.Thumbnails = false;
            dialog.ApplyCommand.Execute(null);
            return true;
        });

        tab.SanitizeCommand.Execute(null);

        request.ShouldNotBeNull().Operation.ShouldBeOfType<SanitizeOperation>();
        request.Parameters.ShouldBe(new SanitizeParams(ExternalLinks: true, Thumbnails: false));
        request.Description.ShouldBe(Strings.History_Sanitize);
    }

    [Fact]
    public async Task Cancelling_CleansNothing_AndASignedDocumentIsWarnedAbout()
    {
        var document = FakePdfDocument.WithPages(1);
        document.Features = FakePdfDocument.PlainFeatures with { SignatureCount = 1 };
        var tab = _factory.Create(await _fixture.CreateAsync(document));
        var asked = false;
        tab.ContentChangeRequested += (_, _) => asked = true;
        SanitizeViewModel? shown = null;
        _dialogs.ShowSanitize(Arg.Do<SanitizeViewModel>(d => shown = d)).Returns(false);

        tab.SanitizeCommand.Execute(null);

        asked.ShouldBeFalse();
        shown.ShouldNotBeNull().IsSigned.ShouldBeTrue();
    }

    [Fact]
    public void TheReport_HasOneLinePerKindOfThingRemoved()
    {
        var report = new SanitizeReport(["Author", "CreationDate"], 1, 3, 0, 2, 0, 1, 4, 0, 1, 0, 2, 11, [3]);

        var text = DocumentTabViewModel.Describe(report);

        text.ShouldStartWith(Strings.Sanitize_Done_Intro);
        text.ShouldContain(Format(Strings.Sanitize_Report_Metadata, "Author, CreationDate"));
        text.ShouldContain(Format(Strings.Sanitize_Report_Scripts, 3));
        text.ShouldContain(Format(Strings.Sanitize_Report_HiddenContent, 4));
        text.ShouldContain(Format(Strings.Sanitize_Report_Revisions, 2));
        text.ShouldContain(Format(Strings.Sanitize_Report_Trailing, 11));
        text.ShouldContain(Format(Strings.Sanitize_Report_PagesKept, "3"));
        text.ShouldNotContain(Format(Strings.Sanitize_Report_Attachments, 0));
        DocumentTabViewModel.Describe(new SanitizeReport([], 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])).ShouldBe(Strings.Sanitize_Done_Nothing);
    }

    [Fact]
    public async Task TheReport_CanBeSavedAsText()
    {
        var tab = _factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(1)));
        var path = Path.Combine(Path.GetTempPath(), $"report-{Guid.NewGuid():N}.txt");
        _messages.AskAsync(Strings.Sanitize_Done_Title, Arg.Any<string>(), Strings.Sanitize_SaveReport, Arg.Is<string?>(s => s == null), Strings.Common_Close)
            .Returns(DialogChoice.Primary);
        _files.PickReportSavePath(Arg.Any<string>()).Returns(path);
        try
        {
            await tab.ShowSanitizeReportAsync(new SanitizeReport(["Author"], 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []));

            var saved = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            saved.ShouldContain(Format(Strings.Sanitize_Report_Metadata, "Author"));
            saved.ShouldContain(Format(Strings.Sanitize_Report_Scripts, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Format(string template, params object[] values) => string.Format(CultureInfo.CurrentCulture, template, values);
}
