using System.Diagnostics;
using System.Globalization;
using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// Phase 5 acceptance scenarios (SPEC P5: stamps) on the real application. Run by hand like
/// <see cref="AcceptanceTests"/>. Folders are never opened in Explorer: "Mở thư mục khi xong" is cleared.
/// </summary>
public sealed class Phase5AcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    private readonly UiTestFolder _folder = new();

    public void Dispose()
    {
        var problems = _folder.LogProblems();
        _folder.Dispose();
        problems.ShouldBeEmpty("the application logged errors");
    }

    [Fact]
    public void AWatermark_IsOneUndoStep_ReadableOnEveryPage_AndCanBeTakenOffAgain()
    {
        RequireEnabled();
        var path = _folder.Copy("basic-vi.pdf");
        var original = PdfFile.PageTexts(path);
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("basic-vi.pdf", Long);

        app.Ribbon("edit", "stamp.watermark");
        var dialog = app.WaitForWindow(Text("Watermark_Title"));
        app.SetText("WatermarkText", "TÀI LIỆU MẬT", dialog);
        var watch = Stopwatch.StartNew();
        app.Invoke("StampApply", dialog);
        app.WaitForTab("basic-vi.pdf *", Long);
        output.WriteLine($"watermark on {original.Length} pages, until the tab shows the change: {watch.ElapsedMilliseconds} ms");

        // One step: undoing it gives the document as opened, redoing brings the watermark back.
        app.Ribbon("edit", "edit.undo");
        app.WaitForTab("basic-vi.pdf", Long);
        app.Ribbon("edit", "edit.redo");
        app.WaitForTab("basic-vi.pdf *", Long);
        app.Ribbon("file", "file.save");
        app.WaitForTab("basic-vi.pdf", Long);

        var stamped = PdfFile.PageTexts(path);
        stamped.ShouldAllBe(t => t.Contains("TÀILIỆUMẬT", StringComparison.Ordinal), "the watermark is text that can be found and copied");
        PdfFile.Layers(path).ShouldContain("Watermark");

        app.Ribbon("edit", "stamp.remove");
        var remove = app.WaitForWindow(Text("RemoveStamps_Title"));
        app.Name("RemoveStamps_Watermark", remove).ShouldBe(Format("RemoveStamps_Watermark", original.Length));
        app.Invoke("StampApply", remove);
        app.WaitForTab("basic-vi.pdf *", Long);
        app.Ribbon("file", "file.save");
        app.WaitForTab("basic-vi.pdf", Long);

        PdfFile.PageTexts(path).ShouldBe(original, "the pages read as before the watermark");
        PdfFile.Layers(path).ShouldNotContain("Watermark", "a layer no page uses is dropped");
    }

    [Fact]
    public void BatesNumbersOfThreeFiles_RunOnWithoutGapsOrRepeats_AndTheFilesStayAsTheyWere()
    {
        RequireEnabled();
        string[] files = [_folder.Copy("basic-vi.pdf", "A hồ sơ.pdf"), _folder.Copy("vanban-vi.pdf", "B hồ sơ.pdf"), _folder.Copy("links-annots.pdf", "C hồ sơ.pdf")];
        var hashes = files.Select(Sha256).ToArray();
        var counts = files.Select(PdfFile.PageCount).ToArray();
        var folder = Path.Combine(_folder.Documents, "Đã đánh số");
        using (var app = AppUnderTest.Start(_folder))
        {
            app.Ribbon("edit", "stamp.bates");
            var dialog = app.WaitForWindow(Text("Bates_Title"));
            app.SetText("BatesPrefix", "HS-", dialog);
            for (var i = 0; i < files.Length; i++)
            {
                app.Invoke("BatesAddFiles", dialog);
                app.UseFileDialog(Text("Open_Dialog_Title"), files[i]);
                var pages = counts.Take(i + 1).Sum();
                var summary = Format("Bates_Summary", i + 1, pages, "HS-000001", Label(pages));
                AppUnderTest.WaitUntil(() => app.Name("BatesSummary", dialog) == summary, Long, summary);
            }

            app.SetText("BatesOutput", folder, dialog);
            app.SetChecked("BatesOpenFolder", false, dialog);
            var watch = Stopwatch.StartNew();
            app.Invoke("BatesRun", dialog);
            app.WaitForStatus(Prefix("Bates_Done"), Long);
            output.WriteLine($"Bates numbering of 3 files ({counts.Sum()} pages): {watch.ElapsedMilliseconds} ms");
        }

        var next = 1;
        for (var i = 0; i < files.Length; i++)
        {
            var copy = Path.Combine(folder, Path.GetFileName(files[i]));
            var texts = PdfFile.PageTexts(copy);
            texts.Length.ShouldBe(counts[i], Path.GetFileName(copy));
            foreach (var text in texts)
            {
                text.ShouldContain(Label(next), Case.Sensitive, $"page {next} of the run");
                next++;
            }

            Sha256(files[i]).ShouldBe(hashes[i], "the file itself is not written");
        }

        Directory.GetFiles(folder).Length.ShouldBe(3, "no temporary files are left");
    }

    private static string Label(int number) => "HS-" + number.ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>The text of a UI string before its first placeholder.</summary>
    private static string Prefix(string name) => Text(name)[..Text(name).IndexOf('{', StringComparison.Ordinal)];
}
