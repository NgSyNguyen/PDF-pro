using System.Text;
using PdfPro.Core.Tables;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Stamping;
using PdfPro.Operations.Tests.TestSupport;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Automation;

/// <summary>Batch runs (SPEC Phase 12): folders expanded, a few files at a time, a broken file never stops the others, paused, cancelled, logged.</summary>
public sealed class BatchRunnerTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly Workflow Numbering = new("Đánh số", [
        WorkflowSteps.Create(PageNumbersOperation.OperationId, new PageNumbersParams("Trang {page}")),
        WorkflowSteps.Create(CompressOperation.OperationId, new CompressParams(CompressionPreset.Archive)),
    ]);

    private readonly TempDirectory _temp = new();

    private readonly OperationCatalog _catalog = TestCatalog.Create(pdfium.Gate);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Folders_GiveTheirFiles_InNaturalOrder_WithoutHiddenTemporaryOrHotFolderFiles()
    {
        var root = Folder("hồ sơ");
        Touch(root, "văn bản 10.pdf");
        Touch(root, "văn bản 2.pdf");
        Touch(root, "ghi chú.txt");
        Touch(root, ".ẩn.pdf");
        Touch(root, "~$tạm.pdf");
        Touch(Path.Combine(root, "phụ lục"), "a.PDF");
        Touch(Path.Combine(root, HotFolderNames.Done), "xong.pdf");
        Touch(Path.Combine(root, HotFolderNames.Error), "lỗi.pdf");
        var single = Touch(_temp.Path, "một mình.pdf");

        var flat = BatchInputs.Expand([root, single, root], recursive: false);
        var deep = BatchInputs.Expand([root], recursive: true, "*.pdf; *.txt");

        flat.Select(f => Path.GetFileName(f.Path)).ShouldBe(["văn bản 2.pdf", "văn bản 10.pdf", "một mình.pdf"]);
        flat.ShouldAllBe(f => f.RelativeFolder == string.Empty);
        deep.Select(f => (Path.GetFileName(f.Path), f.RelativeFolder)).ShouldBe([
            ("ghi chú.txt", string.Empty), ("văn bản 2.pdf", string.Empty), ("văn bản 10.pdf", string.Empty), ("a.PDF", "phụ lục")]);
        BatchInputs.Expand([_temp.Combine("không có.pdf")], recursive: false).ShouldHaveSingleItem().Path.ShouldBe(_temp.Combine("không có.pdf"));
    }

    [Fact]
    public async Task ABrokenFile_FailsAlone_TheOthersAreWritten_InTheirFolders_AndTheLogSaysSo()
    {
        var root = Folder("vào");
        for (var i = 1; i <= 11; i++)
        {
            File.Copy(Samples.BasicVi, Path.Combine(i % 2 == 0 ? Folder(Path.Combine("vào", "chẵn")) : root, $"tệp {i}.pdf"));
        }

        File.Copy(Samples.Broken, Path.Combine(root, "tệp 6 hỏng.pdf"));
        var files = BatchInputs.Expand([root], recursive: true);
        var reports = new List<BatchProgress>();

        var result = await Runner().RunAsync(Numbering, files, new BatchOptions { MaxParallel = 3, OutputFolder = _temp.Combine("ra") }, new SyncProgress<BatchProgress>(r => { lock (reports) { reports.Add(r); } }), Token);

        result.Items.Count.ShouldBe(12);
        (result.Succeeded, result.Failed, result.Cancelled).ShouldBe((11, 1, 0));
        var broken = result.Items.Single(i => i.Status == BatchItemStatus.Failed);
        Path.GetFileName(broken.File.Path).ShouldBe("tệp 6 hỏng.pdf");
        (broken.Failure, broken.Step, broken.Operation).ShouldBe((WorkflowFailure.Step, 0, PageNumbersOperation.OperationId));
        broken.ErrorType.ShouldNotBeNullOrEmpty();
        result.FailedFiles.ShouldBe([broken.File]);

        File.Exists(_temp.Combine("ra", "tệp 1.pdf")).ShouldBeTrue();
        File.Exists(_temp.Combine("ra", "chẵn", "tệp 2.pdf")).ShouldBeTrue("the folders under the chosen one are kept");
        Directory.GetFiles(_temp.Combine("ra"), "*", SearchOption.AllDirectories).Length.ShouldBe(11);
        using (var pig = PigDocument.Open(_temp.Combine("ra", "chẵn", "tệp 10.pdf")))
        {
            pig.GetPage(1).Text.ShouldContain("Trang 1");
        }

        reports.Where(r => r.Item.Status == BatchItemStatus.Running && r.Step is not null).ShouldNotBeEmpty("each file reports its steps");
        reports.GroupBy(r => r.Index).ShouldAllBe(g => g.Last().Item.Status != BatchItemStatus.Running);

        var labels = new BatchLog.Labels("Tệp", "Trạng thái", "Kết quả", "Lỗi", "Bước", "Thao tác", "Loại lỗi", "Giây", new Dictionary<BatchItemStatus, string> { [BatchItemStatus.Done] = "Xong", [BatchItemStatus.Failed] = "Lỗi" });
        var log = CsvTable.Read(BatchLog.Write(result.Items, labels));
        log.Rows.Count.ShouldBe(12);
        log.Columns[0].ShouldBe("Tệp");
        var failedRow = log.Rows.Single(r => r[1] == "Lỗi");
        (failedRow[0], failedRow[3], failedRow[4], failedRow[5]).ShouldBe((broken.File.Path, "Step", "1", PageNumbersOperation.OperationId));

        var again = await Runner().RunAsync(Numbering, result.FailedFiles, new BatchOptions { OutputFolder = _temp.Combine("ra") }, null, Token);
        again.Items.ShouldHaveSingleItem().Status.ShouldBe(BatchItemStatus.Failed, "running the failed files again runs only them");
    }

    [Fact]
    public async Task APausedBatch_WaitsBetweenFiles_AndACancelledOne_SaysWhatWasNotDone()
    {
        var files = Enumerable.Range(1, 4).Select(i => new BatchFile(Copy($"p{i}.pdf"))).ToList();
        var pause = new BatchPause();
        pause.Pause();
        var options = new BatchOptions { MaxParallel = 2, OutputFolder = _temp.Combine("ra"), Pause = pause };

        var running = Runner().RunAsync(Numbering, files, options, null, Token);
        await Task.Delay(300, Token);
        running.IsCompleted.ShouldBeFalse();
        Directory.Exists(_temp.Combine("ra")).ShouldBeFalse("nothing starts while paused");
        pause.Resume();
        (await running).Succeeded.ShouldBe(4);

        pause.Pause();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var stopped = Runner().RunAsync(Numbering, files, options with { OutputFolder = _temp.Combine("ra2") }, null, cancel.Token);
        await Task.Delay(100, Token);
        await cancel.CancelAsync();
        var result = await stopped;
        result.Items.ShouldAllBe(i => i.Status == BatchItemStatus.Cancelled);
        result.Cancelled.ShouldBe(4);
    }

    [Fact]
    public async Task AnInvalidWorkflow_RunsNoFile()
    {
        var error = await Should.ThrowAsync<WorkflowFailedException>(() => Runner().RunAsync(new Workflow("x", [new WorkflowStep("organize.merge")]), [new BatchFile(Copy("a.pdf"))], new BatchOptions(), null, Token));

        error.Failure.ShouldBe(WorkflowFailure.Invalid);
        error.Issues.ShouldHaveSingleItem().Kind.ShouldBe(WorkflowIssueKind.NeedsSeveralFiles);
    }

    private BatchRunner Runner() => new(new WorkflowRunner(_catalog, pdfium.Gate));

    private string Folder(string relative)
    {
        var path = _temp.Combine(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Copy(string name)
    {
        var path = _temp.Combine(name);
        File.Copy(Samples.BasicVi, path);
        return path;
    }

    private static string Touch(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("%PDF-1.4"));
        return path;
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
