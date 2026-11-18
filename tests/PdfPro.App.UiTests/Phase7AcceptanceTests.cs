using System.Diagnostics;
using System.Globalization;
using System.Text;
using FlaUI.Core.AutomationElements;
using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// Phase 7 acceptance scenarios (SPEC P7: forms) on the real application, run by hand like
/// <see cref="AcceptanceTests"/>: a printed form turned into a form ("Nhận diện trường"), filled, saved and flattened;
/// a hundred rows of a table merged into a hundred files. Folders are never opened in Explorer.
/// </summary>
public sealed class Phase7AcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private const int VirtualKeyTab = 0x09;

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    private readonly UiTestFolder _folder = new();

    public void Dispose()
    {
        var problems = _folder.LogProblems();
        _folder.Dispose();
        problems.ShouldBeEmpty("the application logged errors");
    }

    [Fact]
    public void APrintedForm_BecomesAForm_IsFilled_Saved_AndFlattened()
    {
        RequireEnabled();
        var path = PrintedForm("to-khai.pdf");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("to-khai.pdf", Long);

        MakeForm(app, "to-khai.pdf");
        PdfFile.FieldValues(path).Keys.ShouldBe(["Ho va ten", "Que quan", "Dong y"], ignoreOrder: true);

        // Filled in the viewer: typed text (kept when the focus leaves) and a ticked box, saved into the file.
        app.SetText("FormField_Ho va ten", "Nguyễn Văn An");
        app.PressKey(VirtualKeyTab);
        app.WaitForTab("to-khai.pdf *", Long);
        app.Invoke("FormField_Dong y");
        app.Ribbon("file", "file.save");
        app.WaitForTab("to-khai.pdf", Long);
        var values = PdfFile.FieldValues(path);
        values["Ho va ten"].ShouldBe("Nguyễn Văn An");
        values["Dong y"].ShouldNotBe("Off");

        // Flattened after a confirmation: one undoable step, then nothing left to fill and the name is page text.
        app.Ribbon("edit", "forms.flatten");
        var ask = app.WaitForWindow(Text("FormFlatten_Title"));
        ask.FindFirstDescendant(cf => cf.ByName(Text("FormFlatten_Confirm")))!.AsButton().Invoke();
        app.WaitForTab("to-khai.pdf *", Long);
        app.Ribbon("edit", "edit.undo");
        app.WaitForTab("to-khai.pdf", Long);
        app.Ribbon("edit", "edit.redo");
        app.WaitForTab("to-khai.pdf *", Long);
        app.Ribbon("file", "file.save");
        app.WaitForTab("to-khai.pdf", Long);

        PdfFile.FieldValues(path).ShouldBeEmpty();
        PdfFile.PageTexts(path)[0].ShouldContain("NguyễnVănAn");
    }

    [Fact]
    public void AHundredRows_GiveAHundredFilledFiles()
    {
        RequireEnabled();
        var path = PrintedForm("don.pdf");
        var data = Path.Combine(_folder.Documents, "du-lieu.csv");
        var csv = new StringBuilder("Ma ho so,Ho va ten,Dong y\r\n");
        for (var i = 1; i <= 100; i++)
        {
            csv.Append(CultureInfo.InvariantCulture, $"HS-{i:000},Người số {i},{(i % 2 == 0 ? "x" : string.Empty)}\r\n");
        }

        File.WriteAllText(data, csv.ToString(), new UTF8Encoding(true));
        var folder = Path.Combine(_folder.Documents, "kết quả");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("don.pdf", Long);
        MakeForm(app, "don.pdf");

        app.Ribbon("edit", "forms.merge");
        var dialog = app.WaitForWindow(Text("FormMerge_Title"));
        app.Invoke("FormMergeBrowseData", dialog);
        app.UseFileDialog(Text("FormMerge_DataDialog_Title"), data);
        AppUnderTest.WaitUntil(() => app.TryFind("FormMergeSummary", dialog)?.Name == Format("FormMerge_Summary", 100, 2, 3), Long, "the table's summary");
        app.SetText("FormMergeFolder", folder, dialog);
        app.SetChecked("FormMergeOpenFolder", false, dialog);
        var watch = Stopwatch.StartNew();
        app.Invoke("FormMergeOk", dialog);
        app.WaitForStatus(Format("FormMerge_Done", 100, "kết quả"), Long);
        output.WriteLine($"100 filled files written in {watch.ElapsedMilliseconds} ms");

        Directory.GetFiles(folder, "*.pdf").Length.ShouldBe(100);
        for (var i = 1; i <= 100; i++)
        {
            var values = PdfFile.FieldValues(Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"HS-{i:000}.pdf")));
            values["Ho va ten"].ShouldBe(string.Create(CultureInfo.InvariantCulture, $"Người số {i}"));
            (values["Dong y"] != "Off").ShouldBe(i % 2 == 0);
        }
    }

    /// <summary>"Thiết kế biểu mẫu" → "Nhận diện trường" → all three proposals taken → saved; the designer is left.</summary>
    private static void MakeForm(AppUnderTest app, string tab)
    {
        app.Ribbon("edit", "forms.design");
        app.Invoke("FormDesignerDetect");
        AppUnderTest.WaitUntil(() => app.TryFind("FormSuggestionsStatus")?.Name == Format("FormDesigner_Suggestions", 3, 3), Long, "three proposals");
        app.Invoke("FormSuggestionsAccept");
        app.WaitForTab(tab + " *", Long);
        app.Invoke("FormDesignerDone");
        app.Ribbon("file", "file.save");
        app.WaitForTab(tab, Long);
    }

    private string PrintedForm(string name)
    {
        Directory.CreateDirectory(_folder.Documents);
        var path = Path.Combine(_folder.Documents, name);
        PdfFile.WritePrintedForm(path);
        return path;
    }
}
