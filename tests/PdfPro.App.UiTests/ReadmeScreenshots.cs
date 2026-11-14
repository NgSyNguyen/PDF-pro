using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// The pictures of docs/screenshots shown by the README, taken from the real application on the sample files (made-up
/// data only). Run by hand, after a Release build, when the look of the app changed:
/// <c>$env:PDFPRO_SCREENSHOTS=1; dotnet test --project tests/PdfPro.App.UiTests -c Release -- --filter-class "*ReadmeScreenshots"</c>.
/// </summary>
public sealed class ReadmeScreenshots : IDisposable
{
    private const string EnableVariable = "PDFPRO_SCREENSHOTS";

    private const int Width = 1600;

    private const int Height = 1000;

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    private readonly UiTestFolder _folder = new();

    private static string Output => Path.Combine(RepositoryRoot, "docs", "screenshots");

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void TheReadmeScreenshots_AreTaken()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(EnableVariable) == "1", $"Set {EnableVariable}=1 to take the README's screenshots.");

        using (var app = AppUnderTest.Start(_folder))
        {
            Take(app.Main, "home.png");
        }

        var text = _folder.Copy("vanban-vi.pdf", "Công văn mẫu.pdf");
        using (var app = AppUnderTest.Start(_folder, text))
        {
            app.WaitForTab("Công văn mẫu.pdf", Long);
            Take(app.Main, "viewer.png");

            app.Ribbon("annotate", "search.find");
            app.SetText("SearchQuery", "cong hoa");
            app.Invoke("SearchRun");
            AppUnderTest.WaitUntil(() => app.ItemCount("SearchResults") > 0, Long, "the search results");
            Take(app.Main, "search.png");

            app.OpenOrganizer();
            app.SelectRange("2-3");
            Take(app.Main, "organizer.png");

            app.Ribbon("tools", "tools.commandPalette");
            var palette = app.WaitForWindow(Text("Palette_Title"));
            app.SetText("CommandPaletteQuery", "nen", palette);
            Thread.Sleep(Settle);
            AppUnderTest.SaveScreenshot(palette, Path.Combine(Output, "palette.png"));
            app.PressKey(0x1B, palette);
        }

        var older = _folder.Copy("contract-v1.pdf", "Hợp đồng (bản 1).pdf");
        var newer = _folder.Copy("contract-v2.pdf", "Hợp đồng (bản 2).pdf");
        using (var app = AppUnderTest.Start(_folder, newer, older))
        {
            app.WaitForTab("Hợp đồng (bản 2).pdf", Long);
            app.WaitForTab("Hợp đồng (bản 1).pdf", Long);
            app.Ribbon("tools", "compare.documents");
            var window = app.WaitForWindow(Text("Compare_Title"));
            app.Invoke("CompareRun", window);
            AppUnderTest.WaitUntil(() => app.ItemCount("CompareChanges", window) > 0, Long, "the changes");
            Take(window, "compare.png");
            app.Invoke("CompareClose", window);
        }

        // A highlight of the session: on the page and in the comments panel.
        var marked = _folder.Copy("vanban-vi.pdf", "Quyết định.pdf");

        // The comments' author is a made-up name, never the Windows user taking the pictures.
        Directory.CreateDirectory(_folder.DataDirectory);
        File.WriteAllText(Path.Combine(_folder.DataDirectory, "settings.json"), """{ "schemaVersion": 1, "annotationAuthor": "Nguyễn Văn A" }""");
        using (var app = AppUnderTest.Start(_folder, marked))
        {
            app.WaitForTab("Quyết định.pdf", Long);
            app.Ribbon("annotate", "search.find");
            app.SetText("SearchQuery", "quyet dinh");
            app.Invoke("SearchRun");
            AppUnderTest.WaitUntil(() => app.ItemCount("SearchResults") > 0, Long, "the search results");
            app.Ribbon("annotate", "annotate.highlight");
            app.WaitForTab("Quyết định.pdf *", Long);
            app.Ribbon("annotate", "annotate.comments");
            AppUnderTest.WaitUntil(() => app.ItemCount("CommentsList") == 1, Long, "the highlight in the comments list");
            Take(app.Main, "comments.png");
            app.Kill(); // the change is not kept: nothing comes after it in this data folder
        }
    }

    private static void Take(FlaUI.Core.AutomationElements.AutomationElement window, string name)
    {
        AppUnderTest.Resize(window, Width, Height);
        Thread.Sleep(Settle);
        AppUnderTest.SaveScreenshot(window, Path.Combine(Output, name));
    }
}
