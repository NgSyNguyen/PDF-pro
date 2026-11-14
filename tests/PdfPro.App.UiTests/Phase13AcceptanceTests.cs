using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// Phase 13 acceptance scenarios on the real application (SPEC P13: shortcut sheet, About, quick-start guide). Run by
/// hand like <see cref="AcceptanceTests"/>.
/// </summary>
public sealed class Phase13AcceptanceTests : IDisposable
{
    private const int VkF1 = 0x70;
    private const int VkF5 = 0x74;
    private const int VkRight = 0x27;
    private const int VkEscape = 0x1B;

    private readonly UiTestFolder _folder = new();

    public void Dispose()
    {
        var problems = _folder.LogProblems();
        _folder.Dispose();
        problems.ShouldBeEmpty("the application logged errors");
    }

    /// <summary>
    /// "Gộp bằng PDF_Pro" as Explorer runs it, once per selected file ("PdfPro.exe --merge file"): the first launch is the
    /// window, the second hands its file over and exits, and one merge dialog lists both, in natural order of name.
    /// </summary>
    [Fact]
    public void MergeFromExplorer_GathersTheFilesOfEachLaunch_IntoOneMergeDialog()
    {
        RequireEnabled();
        var second = _folder.Copy("basic-vi.pdf", "trang 2.pdf");
        var tenth = _folder.Copy("basic-vi.pdf", "trang 10.pdf");
        using var app = AppUnderTest.Start(_folder, "--merge", tenth);

        var start = new System.Diagnostics.ProcessStartInfo(Exe) { UseShellExecute = false, ArgumentList = { "--merge", second } };
        start.Environment["PDFPRO_DATA_DIR"] = _folder.DataDirectory;
        using (var other = System.Diagnostics.Process.Start(start)!)
        {
            other.WaitForExit(30_000).ShouldBeTrue("the second launch hands its file over and exits");
            other.ExitCode.ShouldBe(0);
        }

        var dialog = app.WaitForWindow(Text("Merge_Title"), TimeSpan.FromSeconds(30));
        AppUnderTest.WaitUntil(() => app.Name("MergeSummary", dialog).StartsWith(Format("Merge_Summary", 2, 12), StringComparison.Ordinal), TimeSpan.FromSeconds(30), "both files to be listed");
        app.Text("MergeOutput", dialog).ShouldStartWith(Path.GetDirectoryName(second)!, customMessage: "the result is proposed next to the files");
        app.DocumentTabs().ShouldNotContain(t => t.StartsWith("trang", StringComparison.Ordinal), "the files are merged, not opened");
        app.Invoke("MergeCancel", dialog);
    }

    /// <summary>
    /// F5 shows the document on the whole screen from the page being read; → turns the page, Esc ends, and the viewer
    /// shows the page the presentation ended on. With PDFPRO_SCREENSHOTS, the screen is kept in the test's output.
    /// </summary>
    [Fact]
    public void F5_PresentsTheDocument_PageByPage()
    {
        RequireEnabled();
        var path = _folder.Copy("vanban-vi.pdf");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("vanban-vi.pdf", TimeSpan.FromSeconds(30));

        app.PressKey(VkF5);
        var show = app.WaitForWindow(Text("Present_Title"));
        AppUnderTest.WaitUntil(() => app.Name("PresentationPage", show) == "1 / 14", TimeSpan.FromSeconds(10), "the first page");
        app.PressKey(VkRight, show);
        AppUnderTest.WaitUntil(() => app.Name("PresentationPage", show) == "2 / 14", TimeSpan.FromSeconds(10), "the second page");
        if (Environment.GetEnvironmentVariable("PDFPRO_SCREENSHOTS") == "1")
        {
            System.Threading.Thread.Sleep(2500);
            AppUnderTest.SaveScreenshot(show, Path.Combine(RepositoryRoot, "docs", "screenshots", "presentation.png"));
        }

        app.PressKey(VkEscape, show);
        AppUnderTest.WaitUntil(() => !app.HasWindow(Text("Present_Title")), TimeSpan.FromSeconds(10), "the presentation to end");
        app.Text("PageNumberBox").ShouldStartWith("2", customMessage: "the viewer is on the page the presentation ended on");
    }

    /// <summary>F1 shows every shortcut; "Giới thiệu" shows the version and the licenses shipped next to the app.</summary>
    [Fact]
    public void F1_ShowsTheShortcuts_AndAboutShowsTheVersionAndTheLicenses()
    {
        RequireEnabled();
        using var app = AppUnderTest.Start(_folder);

        app.PressKey(VkF1);
        var sheet = app.WaitForWindow(Text("Shortcuts_Title"));
        app.Find("ShortcutsList", sheet).ShouldNotBeNull();
        app.Invoke("ShortcutsClose", sheet);

        app.Ribbon("file", "help.about");
        var about = app.WaitForWindow(Text("About_Title"));
        app.Name("AboutVersion", about).ShouldNotBeNullOrWhiteSpace();
        app.Text("AboutNotices", about).ShouldContain("PDFsharp");
        about.Close();
    }

    /// <summary>The start page shows the four-step guide until it is put away; the ribbon brings it back.</summary>
    [Fact]
    public void TheQuickStartGuide_CanBePutAway_AndBroughtBack()
    {
        RequireEnabled();
        using var app = AppUnderTest.Start(_folder);
        app.Find("QuickStartGuide").ShouldNotBeNull();

        app.Invoke("HideGuide");
        AppUnderTest.WaitUntil(() => app.TryFind("QuickStartGuide") is null || app.Find("QuickStartGuide").IsOffscreen, TimeSpan.FromSeconds(10), "the guide to go");

        app.Ribbon("file", "help.guide");
        AppUnderTest.WaitUntil(() => app.TryFind("QuickStartGuide") is { IsOffscreen: false }, TimeSpan.FromSeconds(10), "the guide to come back");
    }
}
