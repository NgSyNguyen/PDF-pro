using FlaUI.Core.AutomationElements;
using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// Phase 9 acceptance scenarios (SPEC P9: optimizing and converting) on the real application. Run by hand like
/// <see cref="AcceptanceTests"/>.
/// </summary>
public sealed class Phase9AcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(120);

    private readonly UiTestFolder _folder = new();

    public void Dispose()
    {
        var problems = _folder.LogProblems();
        _folder.Dispose();
        problems.ShouldBeEmpty("the application logged errors");
    }

    [Fact]
    public void Compressing_ShowsOnePageBeforeAndAfter_ThenWritesACopyNoLarger_WithTheSamePages()
    {
        RequireEnabled();
        var path = _folder.Copy("scan-vi.pdf");
        var original = Sha256(path);
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("scan-vi.pdf", Long);

        app.Ribbon("convert", "optimize.compress");
        var dialog = app.WaitForWindow(Text("Compress_Title"));
        app.Select("CompressPreset_Screen", dialog);
        app.Invoke("CompressPreview", dialog);
        var summary = AppUnderTest.WaitFor(
            () => app.TryFind("CompressPreviewSummary", dialog) is { } text && !string.IsNullOrEmpty(text.Name) ? text.Name : null,
            Long,
            "the preview of the first page");
        output.WriteLine(summary);
        app.Toggle("CompressActualSize", dialog);

        app.Invoke("CompressApply", dialog);
        var compressed = Path.Combine(_folder.Documents, Format("Compress_Name", "scan-vi") + ".pdf");
        app.UseFileDialog(Text("Save_Dialog_Title"), compressed);
        var done = app.WaitForWindow(Text("Compress_Done_Title"), Long);
        done.FindFirstDescendant(cf => cf.ByName(Text("Common_Close")))!.AsButton().Invoke();

        new FileInfo(compressed).Length.ShouldBeLessThanOrEqualTo(new FileInfo(path).Length);
        PdfFile.PageCount(compressed).ShouldBe(PdfFile.PageCount(path));
        Sha256(path).ShouldBe(original, "the document's own file is not touched");
        output.WriteLine($"{new FileInfo(path).Length:N0} → {new FileInfo(compressed).Length:N0} bytes");
    }
}
