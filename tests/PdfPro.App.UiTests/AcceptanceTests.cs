using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// Phase 3 acceptance scenarios (SPEC P3) on the real application. Run by hand:
/// <c>set PDFPRO_UI_TESTS=1</c>, then <c>dotnet test --project tests/PdfPro.App.UiTests -c Release</c>.
/// </summary>
public sealed class AcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private const int Enter = 0x0D;
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    private readonly UiTestFolder _folder = new();

    public void Dispose()
    {
        var problems = _folder.LogProblems();
        _folder.Dispose();
        problems.ShouldBeEmpty("the application logged errors");
    }

    [Fact]
    public void TheOriginalFileIsOnlyWrittenWhenSaved()
    {
        RequireEnabled();
        var path = _folder.Copy("basic-vi.pdf");
        var original = Sha256(path);
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("basic-vi.pdf", Long);

        app.Ribbon("page", "organize.rotateRight");
        app.Ribbon("page", "organize.duplicate");
        app.WaitForTab("basic-vi.pdf *");

        Sha256(path).ShouldBe(original, "edits live in the session until the user saves");

        app.Ribbon("file", "file.save");
        app.WaitForTab("basic-vi.pdf", Long);

        Sha256(path).ShouldNotBe(original);
        PdfFile.Pages(path).Select(p => p.Rotate).ShouldBe([90, 90, 0, 0, 0, 0, 0]);
        UiTestFolder.Leftovers(path).ShouldBeEmpty();
    }

    [Fact]
    public void ThirtyEditsUndoneOneByOne_LeaveTheDocumentAsOpened()
    {
        RequireEnabled();
        var path = _folder.Copy("basic-vi.pdf");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("basic-vi.pdf", Long);
        OpenOrganizer(app);

        var commands = new[] { "organize.rotateRight", "organize.duplicate", "organize.rotateLeft", "organize.delete", "organize.rotateRight" };
        for (var i = 0; i < 30; i++)
        {
            SelectRange(app, ((i % 5) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            app.Invoke(commands[i % commands.Length]);
        }

        app.WaitForTab("basic-vi.pdf *");
        app.Find("RibbonTab_edit").AsTabItem().Select();
        var undone = 0;
        while (app.IsEnabled("edit.undo"))
        {
            app.Invoke("edit.undo");
            undone++;
            undone.ShouldBeLessThan(40);
            Thread.Sleep(30);
        }

        undone.ShouldBe(30);
        app.WaitForTab("basic-vi.pdf");
        app.IsEnabled("edit.redo").ShouldBeTrue();
        WaitForSummary(app, null, 6);
    }

    [Fact]
    public void AfterACrash_UnsavedChangesComeBackWithTheirHistory()
    {
        RequireEnabled();
        var path = _folder.Copy("basic-vi.pdf");
        var original = Sha256(path);
        using (var app = AppUnderTest.Start(_folder, path))
        {
            app.WaitForTab("basic-vi.pdf", Long);
            app.Ribbon("page", "organize.rotateRight");
            app.Ribbon("page", "organize.duplicate");
            app.WaitForTab("basic-vi.pdf *");
            Thread.Sleep(1500); // the manifest is written in the background
            app.Kill();
        }

        Sha256(path).ShouldBe(original);
        using var restarted = AppUnderTest.Start(_folder);
        var recovery = restarted.WaitForWindow(Text("Recovery_Title"), Long);
        restarted.Invoke("RecoveryRestore", recovery);
        restarted.WaitForTab("basic-vi.pdf *", Long);

        restarted.Find("RibbonTab_edit").AsTabItem().Select();
        restarted.Find("edit.undo").HelpText.ShouldBe(Format("Cmd_UndoWith", Format("History_Duplicate", 1)));
        restarted.Invoke("edit.undo");
        restarted.Invoke("edit.undo");
        restarted.WaitForTab("basic-vi.pdf");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(150)]
    [InlineData(300)]
    [InlineData(600)]
    public void AKillDuringASave_NeverLeavesABrokenFile(int delayMilliseconds)
    {
        RequireEnabled();
        var path = _folder.Copy("big-1000.pdf");
        using (var app = AppUnderTest.Start(_folder, path))
        {
            app.WaitForTab("big-1000.pdf", Long);
            app.Ribbon("page", "organize.rotateRight");
            app.WaitForTab("big-1000.pdf *");
            Thread.Sleep(1000);
            app.Find("RibbonTab_file").AsTabItem().Select();
            app.Invoke("file.save");
            Thread.Sleep(delayMilliseconds);
            app.Kill();
        }

        PdfFile.Opens(path, 1000).ShouldBeTrue("the file is either the old or the new version, never a broken one");

        // The next start repairs an interrupted swap and removes the temporary files.
        using var restarted = AppUnderTest.Start(_folder);
        if (AppUnderTest.WaitUntil(() => restarted.HasWindow(Text("Recovery_Title")) || restarted.DocumentTabs().Length > 0, TimeSpan.FromSeconds(15), "the start") > TimeSpan.Zero
            && restarted.HasWindow(Text("Recovery_Title")))
        {
            var recovery = restarted.WaitForWindow(Text("Recovery_Title"));
            restarted.Invoke("RecoveryDiscardAll", recovery);
            restarted.Invoke("RecoveryConfirmDiscard", recovery);
        }

        AppUnderTest.WaitUntil(() => UiTestFolder.Leftovers(path).Length == 0, TimeSpan.FromSeconds(15), "the leftovers to be removed");
        PdfFile.Opens(path, 1000).ShouldBeTrue();
        output.WriteLine($"killed {delayMilliseconds} ms after Save: rotation of page 1 is {PdfFile.Pages(path)[0].Rotate}");
    }

    [Fact]
    public void SavingAFileAnotherProgramHolds_KeepsItAndExplains()
    {
        RequireEnabled();
        var path = _folder.Copy("basic-vi.pdf");
        var original = Sha256(path);
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("basic-vi.pdf", Long);
        app.Ribbon("page", "organize.rotateRight");
        app.WaitForTab("basic-vi.pdf *");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            app.Ribbon("file", "file.save");
            var error = app.WaitForWindow(Text("Save_Error_Title"), Long);
            error.FindFirstDescendant(cf => cf.ByName(Text("Save_SaveAsButton"))).ShouldNotBeNull("Save as is offered");
            error.FindFirstDescendant(cf => cf.ByName(Text("Common_Close")))!.AsButton().Invoke();
        }

        AppUnderTest.WaitUntil(() => !app.HasWindow(Text("Save_Error_Title")), TimeSpan.FromSeconds(10), "the message to close");
        Sha256(path).ShouldBe(original);
        app.DocumentTabs().ShouldContain("basic-vi.pdf *");
        UiTestFolder.Leftovers(path).ShouldBeEmpty();
    }

    [Fact]
    public void PageOperationsOnAThousandPages_RespondAtOnce()
    {
        RequireEnabled();
        var path = _folder.Copy("big-1000.pdf");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("big-1000.pdf", Long);
        OpenOrganizer(app);
        SelectRange(app, "1-200");
        WaitForSummary(app, 200, 1000);

        // End-to-end through UI Automation (polling included); the operations themselves are measured in-process
        // by PageOperationTimingTests.
        var rotate = Measure(() => app.Invoke("organize.rotateRight"), () => app.DocumentTabs().Contains("big-1000.pdf *"));
        var duplicate = Measure(() => app.Invoke("organize.duplicate"), () => Summary(app) == Format("Organizer_SelectionSummary", 200, 1200));
        var delete = Measure(() => app.Invoke("organize.delete"), () => Summary(app) == Format("Organizer_SelectionSummary", 1, 1000));
        output.WriteLine($"rotate 200: {rotate.TotalMilliseconds:0} ms, duplicate 200: {duplicate.TotalMilliseconds:0} ms, delete 200: {delete.TotalMilliseconds:0} ms");

        foreach (var time in new[] { rotate, duplicate, delete })
        {
            time.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        }
    }

    private static void OpenOrganizer(AppUnderTest app)
    {
        app.Ribbon("page", "organize.mode");
        app.Find("OrganizerGrid");
    }

    private static void SelectRange(AppUnderTest app, string range)
    {
        app.SetText("OrganizerRange", range);
        app.PressKey(Enter);
        AppUnderTest.WaitUntil(() => app.Text("OrganizerRange") == range, TimeSpan.FromSeconds(10), $"the range '{range}' to be selected");
    }

    private static string Summary(AppUnderTest app) => app.Name("OrganizerSelection");

    /// <summary>Waits for "Đã chọn n/total trang" (any n when <paramref name="selected"/> is null).</summary>
    private static void WaitForSummary(AppUnderTest app, int? selected, int total)
    {
        var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(Text("Organizer_SelectionSummary"))
            .Replace(@"\{0}", selected?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? @"\d+", StringComparison.Ordinal)
            .Replace(@"\{1}", total.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) + "$";
        AppUnderTest.WaitUntil(() => System.Text.RegularExpressions.Regex.IsMatch(Summary(app), pattern), TimeSpan.FromSeconds(10), $"{selected}/{total} pages");
    }

    private static TimeSpan Measure(Action action, Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        action();
        AppUnderTest.WaitUntil(done, Long, "the operation");
        return watch.Elapsed;
    }
}
