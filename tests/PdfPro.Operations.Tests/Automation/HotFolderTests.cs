using PdfPro.Operations.Automation;
using PdfPro.Operations.Stamping;
using PdfPro.Operations.Tests.TestSupport;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Automation;

/// <summary>A hot folder (SPEC Phase 12): files dropped in go through a workflow once written, then move to _done or _error.</summary>
public sealed class HotFolderTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly Workflow Numbering = new("Đánh số", [WorkflowSteps.Create(PageNumbersOperation.OperationId, new PageNumbersParams("Trang {page}"))]);

    private readonly TempDirectory _temp = new();
    private readonly OperationCatalog _catalog = TestCatalog.Create(pdfium.Gate);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AFileDroppedIn_GoesThroughTheWorkflow_AndMovesToDone_ABrokenOneToError()
    {
        await using var hot = Create(TimeSpan.Zero);
        File.Copy(Samples.BasicVi, _temp.Combine("vào", "công văn.pdf"));
        File.WriteAllText(_temp.Combine("vào", "hỏng.pdf"), "%PDF-1.4 không phải PDF");
        File.WriteAllText(_temp.Combine("vào", "ghi chú.txt"), "không phải PDF");
        var events = new List<HotFolderEvent>();
        hot.Processed += (_, e) => events.Add(e);

        (await hot.ProcessReadyAsync(Token)).ShouldBe(2);

        File.Exists(_temp.Combine("vào", HotFolderNames.Done, "công văn.pdf")).ShouldBeTrue();
        File.Exists(_temp.Combine("vào", HotFolderNames.Error, "hỏng.pdf")).ShouldBeTrue();
        File.Exists(_temp.Combine("vào", "ghi chú.txt")).ShouldBeTrue("only the pattern's files are taken");
        Directory.GetFiles(_temp.Combine("vào"), "*.pdf").ShouldBeEmpty();
        var output = _temp.Combine("vào", HotFolderNames.Output, "công văn.pdf");
        using (var pig = PigDocument.Open(output))
        {
            pig.GetPage(1).Text.ShouldContain("Trang 1");
        }

        events.Count.ShouldBe(2);
        events.Single(e => e.Succeeded).Outputs.ShouldBe([output]);
        events.Single(e => !e.Succeeded).Error.ShouldBeOfType<WorkflowFailedException>();
        (await hot.ProcessReadyAsync(Token)).ShouldBe(0, "nothing is taken twice");
    }

    [Fact]
    public async Task AFileStillBeingWritten_IsNotTaken()
    {
        await using var hot = Create(TimeSpan.FromMilliseconds(300));
        var path = _temp.Combine("vào", "đang chép.pdf");
        var bytes = File.ReadAllBytes(Samples.BasicVi);

        using (var copying = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            copying.Write(bytes.AsSpan(0, bytes.Length / 2));
            copying.Flush();
            (await hot.ProcessReadyAsync(Token)).ShouldBe(0, "just seen");
            await Task.Delay(400, Token);
            (await hot.ProcessReadyAsync(Token)).ShouldBe(0, "still held open by the copy");
            copying.Write(bytes.AsSpan(bytes.Length / 2));
        }

        (await hot.ProcessReadyAsync(Token)).ShouldBe(0, "its size just changed");
        await Task.Delay(400, Token);
        (await hot.ProcessReadyAsync(Token)).ShouldBe(1);
        File.Exists(_temp.Combine("vào", HotFolderNames.Done, "đang chép.pdf")).ShouldBeTrue();
    }

    [Fact]
    public async Task Watching_PicksUpFilesByItself_UntilStopped()
    {
        await using var hot = Create(TimeSpan.FromMilliseconds(200), output: _temp.Combine("ra"));
        hot.Start();
        hot.IsRunning.ShouldBeTrue();

        File.Copy(Samples.BasicVi, _temp.Combine("vào", "a.pdf"));
        for (var i = 0; i < 200 && !File.Exists(_temp.Combine("vào", HotFolderNames.Done, "a.pdf")); i++)
        {
            await Task.Delay(50, Token);
        }

        File.Exists(_temp.Combine("ra", "a.pdf")).ShouldBeTrue();
        await hot.StopAsync();
        hot.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task AMissingFolder_OrABrokenWorkflow_CannotStart()
    {
        await using (var missing = new HotFolder(new WorkflowRunner(_catalog, pdfium.Gate), new HotFolderOptions(_temp.Combine("không có"), Numbering)))
        {
            Should.Throw<DirectoryNotFoundException>(missing.Start);
        }

        Directory.CreateDirectory(_temp.Combine("vào"));
        await using var invalid = new HotFolder(new WorkflowRunner(_catalog, pdfium.Gate), new HotFolderOptions(_temp.Combine("vào"), new Workflow("x", [])));
        Should.Throw<WorkflowFailedException>(invalid.Start).Failure.ShouldBe(WorkflowFailure.Invalid);
    }

    private HotFolder Create(TimeSpan stable, string? output = null)
    {
        Directory.CreateDirectory(_temp.Combine("vào"));
        return new HotFolder(new WorkflowRunner(_catalog, pdfium.Gate), new HotFolderOptions(_temp.Combine("vào"), Numbering, output, StableFor: stable, PollInterval: TimeSpan.FromMilliseconds(200), WorkingFolder: _temp.Combine("work")));
    }
}
