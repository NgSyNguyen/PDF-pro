using System.Diagnostics;
using System.Text;
using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// Phase 4 acceptance scenarios (SPEC P4: split, merge, layouts) on the real application. Run by hand like
/// <see cref="AcceptanceTests"/>. Folders are never opened in Explorer: "Mở thư mục khi xong" is cleared.
/// </summary>
public sealed class Phase4AcceptanceTests(ITestOutputHelper output) : IDisposable
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
    public void SplittingAScanAtMarkedFirstPages_GivesOneFilePerDocument_AndTheMarksAreRemembered()
    {
        RequireEnabled();
        var path = _folder.Copy("scan-vi.pdf");
        var original = Sha256(path);
        string folder;
        using (var app = AppUnderTest.Start(_folder, path))
        {
            app.WaitForTab("scan-vi.pdf", Long);
            app.OpenOrganizer();

            // The documents of the bundle start on pages 1, 5, 8, 11 and 15; page 1 always starts the first file.
            app.SelectRange("5, 8, 11, 15");
            app.Invoke("organize.markSplit");
            AppUnderTest.WaitUntil(() => app.Name("OrganizerSplitSummary") == Format("Split_Summary", 4, 5), TimeSpan.FromSeconds(10), "the marks");
            app.DocumentTabs().ShouldContain("scan-vi.pdf", "marks do not make the document unsaved");

            app.Invoke("OrganizerSplitByMarks");
            var dialog = app.WaitForWindow(Text("Split_Title"));
            app.SetChecked("SplitOpenFolder", false, dialog);
            app.SetChecked("SplitSkipBlank", true, dialog);

            // The blank separator sheets (4, 7, 10, 14) are left out: 18 - 4 pages.
            AppUnderTest.WaitUntil(() => app.Name("SplitSummary", dialog) == Format("Split_Summary_Files", 5, 14), Long, "the blank pages to be found");
            folder = app.Text("SplitFolder", dialog);
            var watch = Stopwatch.StartNew();
            app.Invoke("SplitRun", dialog);
            app.WaitForStatus(Prefix("Split_Done"), Long);
            output.WriteLine($"split by marks (18 scanned pages into 5 files): {watch.ElapsedMilliseconds} ms");
        }

        var files = Directory.GetFiles(folder, "*.pdf").Order(StringComparer.Ordinal).ToArray();
        files.Select(Path.GetFileName).ShouldBe(["scan-vi_001.pdf", "scan-vi_002.pdf", "scan-vi_003.pdf", "scan-vi_004.pdf", "scan-vi_005.pdf"]);
        files.Select(PdfFile.PageCount).ShouldBe([3, 2, 2, 3, 4]);
        Directory.GetFiles(folder).Length.ShouldBe(5, "no temporary files are left");
        Sha256(path).ShouldBe(original, "splitting never writes the original");

        using var restarted = AppUnderTest.Start(_folder, path);
        restarted.WaitForTab("scan-vi.pdf", Long);
        restarted.OpenOrganizer();
        AppUnderTest.WaitUntil(() => restarted.Name("OrganizerSplitSummary") == Format("Split_Summary", 4, 5), TimeSpan.FromSeconds(10), "the remembered marks");
    }

    [Fact]
    public void MergingTwentyFiles_KeepsEveryBookmarkAndLinksTheTableOfContents()
    {
        RequireEnabled();
        string[] samples = ["basic-vi.pdf", "links-annots.pdf", "vanban-vi.pdf"];
        var files = Enumerable.Range(0, 20).Select(i => _folder.Copy(samples[i % 3], $"Hồ sơ {i + 1:00}.pdf")).ToArray();
        var pageCounts = files.Select(PdfFile.PageCount).ToArray();
        var merged = Path.Combine(_folder.Documents, "Tập hồ sơ.pdf");
        using var app = AppUnderTest.Start(_folder);
        app.Invoke("WelcomeMerge");
        var dialog = app.WaitForWindow(Text("Merge_Title"));

        for (var i = 0; i < files.Length; i++)
        {
            app.Invoke("MergeAddFiles", dialog);
            app.UseFileDialog(Text("Open_Dialog_Title"), files[i]);
            if (i == 0)
            {
                AppUnderTest.WaitUntil(() => app.Name("MergeError", dialog) == Text("Merge_Error_TooFew"), Long, "one file to be listed");
                continue;
            }

            var summary = Format("Merge_Summary", i + 1, pageCounts.Take(i + 1).Sum());
            AppUnderTest.WaitUntil(() => app.Name("MergeSummary", dialog).StartsWith(summary, StringComparison.Ordinal), Long, summary);
        }

        app.SetChecked("MergeKeepBookmarks", true, dialog);
        app.SetChecked("MergeBookmarkPerFile", true, dialog);
        app.SetChecked("MergeTableOfContents", true, dialog);
        app.SetText("MergeOutput", merged, dialog);
        var watch = Stopwatch.StartNew();
        app.Invoke("MergeRun", dialog);
        app.WaitForTab("Tập hồ sơ.pdf", Long);
        output.WriteLine($"merge of 20 files ({pageCounts.Sum()} pages) with bookmarks and contents, until the result is open: {watch.ElapsedMilliseconds} ms");

        var tocPages = PdfFile.PageCount(merged) - pageCounts.Sum();
        tocPages.ShouldBeInRange(1, 2);
        var starts = new int[files.Length];
        for (int i = 0, page = tocPages; i < files.Length; page += pageCounts[i], i++)
        {
            starts[i] = page;
        }

        var bookmarks = PdfFile.Bookmarks(merged);
        bookmarks.Where(b => b.Depth == 0).ShouldBe(files.Select((f, i) => new PdfBookmark(Path.GetFileNameWithoutExtension(f), starts[i], 0)));
        bookmarks.ShouldAllBe(b => b.Page >= 0, "every bookmark leads to its page");
        var own = Groups(bookmarks);
        for (var i = 0; i < files.Length; i++)
        {
            var source = PdfFile.Bookmarks(files[i]);
            own[i].Select(b => (b.Title, b.Depth)).ShouldBe(source.Select(b => (b.Title, b.Depth + 1)), $"the bookmarks of {Path.GetFileName(files[i])}");
            own[i].ShouldAllBe(b => b.Page >= starts[i] && b.Page < starts[i] + pageCounts[i]);
        }

        Enumerable.Range(0, tocPages).SelectMany(p => PdfFile.LinkTargets(merged, p)).ShouldBe(starts, "each contents line links to its file");
    }

    [Fact]
    public void SplittingByVietnameseBookmarks_NamesTheFilesAfterTheirTitles()
    {
        RequireEnabled();
        var path = _folder.Copy("vanban-vi.pdf");
        var titles = PdfFile.Bookmarks(path).Where(b => b.Depth == 0).ToArray();
        titles.Length.ShouldBe(5);
        string folder;
        using (var app = AppUnderTest.Start(_folder, path))
        {
            app.WaitForTab("vanban-vi.pdf", Long);
            app.Ribbon("page", "organize.split");
            var dialog = app.WaitForWindow(Text("Split_Title"));
            app.Select("SplitMode_Bookmarks", dialog);
            app.SetChecked("SplitOpenFolder", false, dialog);
            AppUnderTest.WaitUntil(() => app.Name("SplitSummary", dialog) == Format("Split_Summary_Files", 5, 14), Long, "the bookmark split preview");
            app.Text("SplitNamePattern", dialog).ShouldBe("{bookmark}");
            folder = app.Text("SplitFolder", dialog);
            app.Invoke("SplitRun", dialog);
            app.WaitForStatus(Prefix("Split_Done"), Long);
        }

        var expected = titles.Select(t => ExpectedFileName(t.Title) + ".pdf").ToArray();
        var names = Directory.GetFiles(folder, "*.pdf").Select(f => Path.GetFileName(f)!).ToArray();
        names.ShouldBe(expected, ignoreOrder: true);
        names.ShouldAllBe(n => n.IsNormalized(NormalizationForm.FormC));
        var ends = titles.Skip(1).Select(t => t.Page).Append(14).ToArray();
        for (var i = 0; i < titles.Length; i++)
        {
            PdfFile.PageCount(Path.Combine(folder, expected[i])).ShouldBe(ends[i] - titles[i].Page, expected[i]);
            output.WriteLine($"{expected[i]}: {ends[i] - titles[i].Page} pages");
        }
    }

    [Fact]
    public void ABookletOfTenPages_IsSixLandscapeSidesOnThreeSheets()
    {
        RequireEnabled();
        var path = _folder.Copy("vanban-vi.pdf");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("vanban-vi.pdf", Long);

        // A booklet takes the whole document as it is shown: delete 4 pages, the booklet is made from a working copy.
        app.OpenOrganizer();
        app.SelectRange("11-14");
        app.Invoke("organize.delete");
        app.WaitForTab("vanban-vi.pdf *");
        app.Ribbon("page", "layout.booklet");
        var dialog = app.WaitForWindow(Text("Layout_Booklet_Title"));
        AppUnderTest.WaitUntil(() => app.Name("LayoutSheetCount", dialog) == Format("Layout_BookletSheetCount", 6, 3), TimeSpan.FromSeconds(10), "10 pages padded to 12 on 3 sheets");
        var result = app.Text("LayoutOutput", dialog);
        Path.GetFileName(result).ShouldBe(Format("Layout_DefaultName_Booklet", "vanban-vi") + ".pdf");

        app.Invoke("LayoutRun", dialog);
        app.WaitForTab(Path.GetFileName(result), Long);

        PdfFile.Pages(result).ShouldBe(Enumerable.Repeat(new PdfPageInfo(0, 842, 595), 6), "A4 landscape sides, printed two-sided and folded");
        Sha256(path).ShouldBe(Sha256(Sample("vanban-vi.pdf")), "the edited document itself is not written");

        // Leave the document as opened so that closing asks nothing.
        app.SelectTab("vanban-vi.pdf *");
        app.Ribbon("edit", "edit.undo");
        app.WaitForTab("vanban-vi.pdf");
    }

    /// <summary>The text of a UI string before its first placeholder ("Đã tách thành {0} tệp…" → "Đã tách thành ").</summary>
    private static string Prefix(string name) => Text(name)[..Text(name).IndexOf('{', StringComparison.Ordinal)];

    /// <summary>Windows file name rules of SPEC P4: "/", "\" and "|" become "-", ":*?\"&lt;&gt;" are dropped, NFC.</summary>
    private static string ExpectedFileName(string title)
    {
        var builder = new StringBuilder();
        foreach (var c in title.Normalize(NormalizationForm.FormC))
        {
            if (c is '/' or '\\' or '|')
            {
                builder.Append('-');
            }
            else if (c is not (':' or '*' or '?' or '"' or '<' or '>'))
            {
                builder.Append(c);
            }
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
    }

    /// <summary>The bookmarks under each top-level (file) bookmark.</summary>
    private static List<List<PdfBookmark>> Groups(PdfBookmark[] bookmarks)
    {
        var groups = new List<List<PdfBookmark>>();
        foreach (var bookmark in bookmarks)
        {
            if (bookmark.Depth == 0)
            {
                groups.Add([]);
            }
            else
            {
                groups[^1].Add(bookmark);
            }
        }

        return groups;
    }
}
