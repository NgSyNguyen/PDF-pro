using System.Diagnostics;
using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// Phase 6 acceptance scenarios (SPEC P6: text, search and annotations) on the real application. Run by hand like
/// <see cref="AcceptanceTests"/>.
/// </summary>
public sealed class Phase6AcceptanceTests(ITestOutputHelper output) : IDisposable
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
    public void SearchingWithoutTones_HighlightingTheMatch_ListingIt_Saving_AndFlattening()
    {
        RequireEnabled();
        var path = _folder.Copy("basic-vi.pdf");
        var original = PdfFile.PageTexts(path);
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("basic-vi.pdf", Long);

        // "doc lap" finds "Độc lập": tones and đ are ignored for a query typed without them.
        app.Ribbon("annotate", "search.find");
        app.SetText("SearchQuery", "doc lap");
        var watch = Stopwatch.StartNew();
        app.Invoke("SearchRun");
        AppUnderTest.WaitUntil(() => app.ItemCount("SearchResults") > 0, Long, "the search results");
        output.WriteLine($"first result of 'doc lap' after {watch.ElapsedMilliseconds} ms");

        // The chosen result is selected text: "Tô sáng" marks it at once, one undoable step.
        app.Ribbon("annotate", "annotate.highlight");
        app.WaitForTab("basic-vi.pdf *", Long);
        app.Ribbon("annotate", "annotate.comments");
        AppUnderTest.WaitUntil(() => app.ItemCount("CommentsList") == 1, Long, "the highlight in the comments list");
        app.Ribbon("file", "file.save");
        app.WaitForTab("basic-vi.pdf", Long);
        PdfFile.Annotations(path, 0).ShouldContain("/Highlight", "written into the file");

        // Flattening draws it into the page: no annotation left, the words unchanged, one undoable step.
        app.Ribbon("annotate", "annotate.flatten");
        var dialog = app.WaitForWindow(Text("Flatten_Title"));
        app.Invoke("FlattenOk", dialog);
        app.WaitForTab("basic-vi.pdf *", Long);
        app.Ribbon("edit", "edit.undo");
        app.WaitForTab("basic-vi.pdf", Long);
        app.Ribbon("edit", "edit.redo");
        app.WaitForTab("basic-vi.pdf *", Long);
        app.Ribbon("file", "file.save");
        app.WaitForTab("basic-vi.pdf", Long);

        PdfFile.Annotations(path, 0).ShouldNotContain("/Highlight");
        PdfFile.PageTexts(path).ShouldBe(original, "flattening adds no words and takes none away");
    }
}
