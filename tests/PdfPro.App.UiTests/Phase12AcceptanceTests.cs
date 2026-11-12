using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using PdfPro.App.UiTests.TestSupport;
using static PdfPro.App.UiTests.TestSupport.UiTestEnvironment;

namespace PdfPro.App.UiTests;

/// <summary>
/// Phase 12 acceptance scenarios (SPEC P12: command palette, workflows, batch runs, CLI) on the real application. Run
/// by hand like <see cref="AcceptanceTests"/>; the digitizing workflow needs Tesseract (tools/fetch-tools.ps1).
/// </summary>
public sealed class Phase12AcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(10);

    private readonly UiTestFolder _folder = new();

    public void Dispose()
    {
        var problems = _folder.LogProblems();
        _folder.Dispose();
        problems.ShouldBeEmpty("the application logged errors");
    }

    /// <summary>Typing a command's name in the palette and pressing Enter runs it.</summary>
    [Fact]
    public void ThePalette_FindsACommandByName_AndRunsIt()
    {
        RequireEnabled();
        var path = _folder.Copy("basic-vi.pdf");
        using var app = AppUnderTest.Start(_folder, path);
        app.WaitForTab("basic-vi.pdf");

        app.Ribbon("tools", "tools.commandPalette");
        var palette = app.WaitForWindow(Text("Palette_Title"));
        app.SetText("CommandPaletteQuery", Text("Cmd_Properties"), palette);
        app.Find("CommandPaletteQuery", palette).Focus();
        Keyboard.Press(VirtualKeyShort.ENTER);

        var properties = app.WaitForWindow(Text("DocProps_Title"));
        properties.Close();
    }

    /// <summary>
    /// "Số hóa hồ sơ" on a folder of 50 files, one of them damaged: 49 are written, the damaged one is reported — and the
    /// command line gives the same pages and text for the same workflow.
    /// </summary>
    [Fact]
    public void TheDigitizingWorkflow_OnFiftyFiles_DoesFortyNine_AndTheCommandLineAgrees()
    {
        RequireEnabled();
        Assert.SkipUnless(TesseractInstalled(), "Tesseract is not installed (run tools/fetch-tools.ps1)");
        var input = Directory.CreateDirectory(Path.Combine(_folder.Documents, "hồ sơ")).FullName;
        for (var i = 1; i <= 49; i++)
        {
            File.Copy(Sample("basic-vi.pdf"), Path.Combine(input, $"hồ sơ {i:00}.pdf"));
        }

        File.Copy(Sample("broken.pdf"), Path.Combine(input, "hồ sơ 50 hỏng.pdf"));
        var results = Path.Combine(_folder.Documents, "đã số hóa");
        using var app = AppUnderTest.Start(_folder);

        app.Ribbon("tools", "batch.run");
        var batch = app.WaitForWindow(Text("Batch_Title"));
        app.Find("BatchJob", batch).AsComboBox().Select(Text("Workflow_Template_Digitize"));
        app.Invoke("BatchAddFolder", batch);
        app.UseFileDialog(Text("Batch_AddFolder_Title"), input);
        AppUnderTest.WaitUntil(() => app.ItemCount("BatchRows", batch) == 50, TimeSpan.FromSeconds(30), "the 50 files");
        app.SetText("BatchOutput", results, batch);
        var watch = Stopwatch.StartNew();
        app.Invoke("BatchStart", batch);
        AppUnderTest.WaitUntil(() => app.Name("BatchSummary", batch) == Format("Batch_Done", 49, 1, 0), Long, "the batch");
        output.WriteLine($"50 files in {watch.Elapsed.TotalSeconds:N0} s");
        Directory.GetFiles(results, "*.pdf").Length.ShouldBe(49);
        File.Exists(Path.Combine(results, "hồ sơ 50 hỏng.pdf")).ShouldBeFalse();

        // The same workflow from the command line.
        var cliOutput = Path.Combine(_folder.Documents, "dòng lệnh");
        var cli = Process.Start(new ProcessStartInfo(CliExe)
        {
            ArgumentList = { "run-workflow", "digitize", Path.Combine(input, "hồ sơ 01.pdf"), "-o", cliOutput, "-q" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        cli.WaitForExit((int)Long.TotalMilliseconds).ShouldBeTrue();
        cli.ExitCode.ShouldBe(0, cli.StandardError.ReadToEnd());
        var fromApp = Path.Combine(results, "hồ sơ 01.pdf");
        var fromCli = Path.Combine(cliOutput, "hồ sơ 01.pdf");
        PdfFile.PageCount(fromCli).ShouldBe(PdfFile.PageCount(fromApp));
        PdfFile.PageTexts(fromCli).ShouldBe(PdfFile.PageTexts(fromApp));
    }

    /// <summary>pdfpro.exe built next to the app (same configuration).</summary>
    private static string CliExe
    {
        get
        {
            var configuration = new DirectoryInfo(Path.GetDirectoryName(Exe)!).Parent!.Name;
            var path = Path.Combine(RepositoryRoot, "src", "PdfPro.Cli", "bin", configuration, "net10.0", "pdfpro.exe");
            return File.Exists(path) ? path : throw new FileNotFoundException("Build PdfPro.Cli first.", path);
        }
    }

    /// <summary>Tesseract where the app looks for it: next to it in tools, installed, or on PATH.</summary>
    private static bool TesseractInstalled()
    {
        var candidates = new List<string>
        {
            Path.Combine(Path.GetDirectoryName(Exe)!, "tools", "tesseract", "tesseract.exe"),
            Path.Combine(RepositoryRoot, "tools", "tesseract", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe"),
        };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, "tesseract.exe")));
        return candidates.Any(File.Exists);
    }
}
