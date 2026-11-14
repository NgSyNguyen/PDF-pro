using System.IO;
using NSubstitute;
using PdfPro.App.Hosting;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Giới thiệu" and "Sao chép thông tin lỗi": the version and the system, never the user or their files.</summary>
public sealed class AboutViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheDetails_SayTheVersionAndTheSystem_AndAreCopied()
    {
        var notices = Path.Combine(_temp.Path, "THIRD_PARTY_NOTICES.md");
        File.WriteAllText(notices, "# Third-party notices\nPDFsharp 6.2.4 MIT");
        var clipboard = Substitute.For<IClipboardService>();
        clipboard.SetText(Arg.Any<string>()).Returns(true);
        var opened = false;
        var viewModel = new AboutViewModel(new AppBuildInfo("1.2.3+abc", false), notices, clipboard, () => opened = true);

        viewModel.Version.ShouldBe("1.2.3+abc");
        viewModel.Notices.ShouldContain("PDFsharp 6.2.4 MIT");
        viewModel.CopyDetailsCommand.Execute(null);
        viewModel.OpenLogFolderCommand.Execute(null);

        clipboard.Received(1).SetText(viewModel.Details);
        viewModel.CopyStatus.ShouldBe(Strings.About_Copied);
        viewModel.Details.ShouldContain("1.2.3+abc");
        viewModel.Details.ShouldContain(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        opened.ShouldBeTrue();
    }

    [Fact]
    public void ABusyClipboard_AndMissingNotices_AreSaid()
    {
        var clipboard = Substitute.For<IClipboardService>();
        var viewModel = new AboutViewModel(new AppBuildInfo("1.0.0", false), Path.Combine(_temp.Path, "không có.md"), clipboard, () => { });

        viewModel.CopyDetailsCommand.Execute(null);

        viewModel.CopyStatus.ShouldBe(Strings.About_CopyFailed);
        viewModel.Notices.ShouldBe(Strings.About_NoticesMissing);
    }

    [Fact]
    public void ErrorDetails_NameTheIncident_ButNothingAboutTheUser()
    {
        var details = AppDiagnostics.Describe(new AppBuildInfo("1.0.0", false), "a1b2c3", new DateTimeOffset(2026, 11, 12, 9, 30, 0, TimeSpan.FromHours(7)));

        details.ShouldContain("a1b2c3");
        details.ShouldContain("2026-11-12 09:30:00 +07:00");
        details.ShouldNotContain(Environment.UserName);
        details.ShouldNotContain(Environment.MachineName);
        details.ShouldNotContain(Path.GetTempPath().TrimEnd('\\', '/'));
    }
}
