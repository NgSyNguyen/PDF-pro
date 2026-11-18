using System.IO;
using NSubstitute;
using PdfPro.App.Services;
using PdfPro.App.Services.Automation;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Automation;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Security;
using PdfPro.Operations.Stamping;

namespace PdfPro.App.Tests.ViewModels.Automation;

/// <summary>The workflow designer (forms made from parameter records, the chain checked after every change) and the library window.</summary>
public sealed class WorkflowViewModelsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ABuiltInWorkflow_OpensAsSteps_WithFormsMadeFromTheirParameters()
    {
        var digitize = WorkflowLibrary.BuiltIn().Single(e => e.TemplateId == WorkflowTemplates.Digitize).Workflow;

        var editor = new WorkflowEditorViewModel(digitize, TestOperations.All());

        editor.Steps.Select(s => s.Operation.Id).ShouldBe(["ocr.run", "scan.removeBlankPages", "optimize.compress", "convert.toPdfA"]);
        editor.Steps.Select(s => s.Number).ShouldBe([1, 2, 3, 4]);
        editor.HasErrors.ShouldBeFalse();
        editor.IsDirty.ShouldBeFalse();
        var ocr = editor.Steps[0];
        ocr.Fields.Single(f => f.Field.Name == "dpi").Text.ShouldBe("300");
        ocr.Fields.Single(f => f.Field.Name == "autoRotate").IsChecked.ShouldBeTrue();
        ocr.Fields.Single(f => f.Field.Name == "existingText").Choice.ShouldBe("skip");
        ocr.Fields.Single(f => f.Field.Name == "languages").IsJson.ShouldBeTrue();
        editor.Steps[1].Fields.ShouldHaveSingleItem().Field.Name.ShouldBe("thresholdPercent", "a measured step's form is how to measure");
        editor.Operations.ShouldNotContain(o => o.Id == "organize.merge" || o.Id == "security.protection");
        editor.Operations.ShouldContain(o => o.Id == "convert.officeToPdf");
    }

    [Fact]
    public void TheChain_IsCheckedAfterEveryChange()
    {
        var editor = new WorkflowEditorViewModel(new Workflow("x", [WorkflowSteps.Create(CompressOperation.OperationId)]), TestOperations.All());

        editor.SelectedStep = null;
        editor.OperationToAdd = editor.Operations.Single(o => o.Id == ProtectDocumentOperation.OperationId);
        editor.AddStepCommand.Execute(null);
        editor.Steps.Select(s => s.Operation.Id).ShouldBe([CompressOperation.OperationId, ProtectDocumentOperation.OperationId]);
        editor.SelectedStep!.AsksWhenRun.ShouldBeTrue();
        editor.SelectedStep.Fields.ShouldNotContain(f => f.Field.Secret);
        editor.HasErrors.ShouldBeFalse("the passwords are asked when it runs");

        editor.MoveStepUpCommand.Execute(null);
        editor.HasErrors.ShouldBeTrue("protection must come last");
        editor.Problems.ShouldContain(p => p.Contains(PdfPro.App.Resources.Strings.Workflow_Issue_ProtectionNotLast));
        editor.MoveStepDownCommand.Execute(null);
        editor.HasErrors.ShouldBeFalse();

        var compress = editor.Steps[0];
        compress.Fields.Single(f => f.Field.Name == "preset").Choice = "custom";
        compress.Fields.Single(f => f.Field.Name == "dpi").Text = "5";
        editor.HasErrors.ShouldBeTrue("5 DPI is out of range");
        compress.Fields.Single(f => f.Field.Name == "dpi").Text = "120";
        editor.HasErrors.ShouldBeFalse();
        compress.Enabled = false;
        editor.IsDirty.ShouldBeTrue();

        editor.OutputMode = editor.OutputModes.Single(m => m.Value == WorkflowOutputMode.Suffix);
        editor.OutputSuffix = " (gửi)";
        var workflow = editor.ToWorkflow();
        workflow.Steps[0].Enabled.ShouldBeFalse();
        workflow.Steps[0].Parameters!["dpi"]!.GetValue<long>().ShouldBe(120);
        workflow.Destination.ShouldBe(new WorkflowOutput(WorkflowOutputMode.Suffix, null, " (gửi)", false));
    }

    [Fact]
    public void TheLibrary_SavesCopiesDeletesImportsAndExports()
    {
        var library = new WorkflowLibrary(_temp.Combine("workflows"));
        var window = new WorkflowsViewModel(library, TestOperations.All(), _files, hasDocument: true);

        window.Items.Count.ShouldBe(3, "the built-in workflows");
        window.Selected!.IsBuiltIn.ShouldBeTrue();
        window.DeleteCommand.CanExecute(null).ShouldBeFalse("a built-in workflow cannot be deleted");

        window.Editor!.Name = "Số hóa của tôi";
        window.SaveCommand.Execute(null);
        window.Items.Count.ShouldBe(4, "a built-in workflow is saved as a new one");
        window.Selected!.Entry.Path.ShouldNotBeNull().ShouldEndWith(".pdfflow");
        window.Selected.Name.ShouldBe("Số hóa của tôi");

        var shared = _temp.Combine("chia sẻ.pdfflow");
        _files.PickWorkflowSavePath(Arg.Any<string>()).Returns(shared);
        window.ExportCommand.Execute(null);
        File.Exists(shared).ShouldBeTrue();
        File.ReadAllText(shared).ShouldNotContain("assword");

        window.DeleteCommand.Execute(null);
        window.Items.Count.ShouldBe(3);

        _files.PickWorkflowFile().Returns(shared);
        window.ImportCommand.Execute(null);
        window.Items.Count.ShouldBe(4);
        window.Selected!.Name.ShouldBe("Số hóa của tôi");

        File.WriteAllText(_temp.Combine("lạ.pdfflow"), "{}");
        _files.PickWorkflowFile().Returns(_temp.Combine("lạ.pdfflow"));
        window.ImportCommand.Execute(null);
        window.Message.ShouldBe(PdfPro.App.Resources.Strings.Workflow_Error_NotAWorkflow);
    }

    [Fact]
    public void Running_IsChosenOnlyForAValidChain()
    {
        var window = new WorkflowsViewModel(new WorkflowLibrary(_temp.Combine("w")), TestOperations.All(), _files, hasDocument: false);
        bool? closed = null;
        window.CloseRequested += (_, accepted) => closed = accepted;

        window.RunOnDocumentCommand.CanExecute(null).ShouldBeFalse("no document is open");
        window.NewCommand.Execute(null);
        window.RunBatchCommand.CanExecute(null).ShouldBeFalse("a workflow without steps cannot run");
        window.Editor!.OperationToAdd = window.Editor.Operations.Single(o => o.Id == PageNumbersOperation.OperationId);
        window.Editor.AddStepCommand.Execute(null);
        window.RunBatchCommand.CanExecute(null).ShouldBeTrue();
        window.RunBatchCommand.Execute(null);

        closed.ShouldBe(true);
        window.Result!.Target.ShouldBe(WorkflowRunTarget.Batch);
        window.Result.Workflow.Steps.ShouldHaveSingleItem().Operation.ShouldBe(PageNumbersOperation.OperationId);
    }
}
