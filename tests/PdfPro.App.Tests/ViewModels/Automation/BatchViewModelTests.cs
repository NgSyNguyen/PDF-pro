using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Services;
using PdfPro.App.Services.Automation;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Automation;
using PdfPro.Core.Tables;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Stamping;

namespace PdfPro.App.Tests.ViewModels.Automation;

/// <summary>The batch window: files and folders, a job, a table of each file, the failed ones run again, a CSV log.</summary>
public sealed class BatchViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly IShellLauncher _shell = Substitute.For<IShellLauncher>();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ABrokenFile_FailsAlone_AndRunsAgainAlone()
    {
        var folder = Directory.CreateDirectory(_temp.Combine("vào")).FullName;
        File.Copy(Samples.Basic, Path.Combine(folder, "a.pdf"));
        File.Copy(Samples.Basic, Path.Combine(folder, "b.pdf"));
        File.WriteAllText(Path.Combine(folder, "c hỏng.pdf"), "%PDF-1.4 không phải PDF");
        _files.PickFolder(Arg.Any<string>(), Arg.Any<string?>()).Returns(folder);
        var numbering = new Workflow("Đánh số", [WorkflowSteps.Create(PageNumbersOperation.OperationId, new PageNumbersParams("Trang {page}"))]);
        using var batch = Create(numbering);

        batch.Job.Workflow.ShouldBe(numbering);
        batch.Jobs.ShouldContain(j => j.IsOperation, "single operations with their usual settings");
        batch.AddFolderCommand.Execute(null);
        batch.Rows.Select(r => r.Name).ShouldBe(["a.pdf", "b.pdf", "c hỏng.pdf"]);
        batch.OutputFolder = _temp.Combine("ra");
        await batch.StartCommand.ExecuteAsync(null);

        batch.Rows.Select(r => r.Status).ShouldBe([BatchItemStatus.Done, BatchItemStatus.Done, BatchItemStatus.Failed]);
        batch.Rows[2].Detail.ShouldNotBeNullOrEmpty();
        File.Exists(_temp.Combine("ra", "a.pdf")).ShouldBeTrue();
        batch.Fraction.ShouldBe(1);

        var log = _temp.Combine("nhật ký.csv");
        _files.PickTableSavePath(Arg.Any<string>(), Arg.Any<string?>(), Arg.Is(true)).Returns(log);
        batch.ExportLogCommand.Execute(null);
        CsvTable.Read(File.ReadAllBytes(log)).Rows.Count.ShouldBe(3);

        await batch.RerunFailedCommand.ExecuteAsync(null);
        batch.Rows.ShouldHaveSingleItem().Name.ShouldBe("c hỏng.pdf");
        batch.Rows[0].Status.ShouldBe(BatchItemStatus.Failed);
    }

    [Fact]
    public async Task AWorkflowThatNamesNoFolder_AsksForOne()
    {
        _files.PickPdfFiles().Returns([Samples.Basic]);
        using var batch = Create(new Workflow("x", [WorkflowSteps.Create(PageNumbersOperation.OperationId)]));
        batch.AddFilesCommand.Execute(null);

        await batch.StartCommand.ExecuteAsync(null);

        batch.Summary.ShouldBe(PdfPro.App.Resources.Strings.Batch_Error_NoOutput);
        batch.Rows.ShouldHaveSingleItem().Status.ShouldBe(BatchItemStatus.Waiting);
    }

    private BatchViewModel Create(Workflow workflow)
    {
        var catalog = TestOperations.All();
        var automation = new AutomationServices(catalog, new WorkflowRunner(catalog, TestOperations.PdfiumGate), new WorkflowLibrary(_temp.Combine("workflows")));
        return new BatchViewModel(automation, _files, _shell, _ => new PreparedSecrets(), _temp.Combine("work"), workflow, NullLogger.Instance);
    }
}
