using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services.Automation;
using PdfPro.App.ViewModels.Automation;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Core.Security;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Security;
using PdfPro.Operations.Signing;

namespace PdfPro.App.ViewModels;

/// <summary>
/// Workflows and batch runs (SPEC Phase 12): the workflow library and designer, a workflow run on the open document
/// (as it is shown), and the batch window. Passwords and certificates a workflow needs are asked before it runs, once.
/// </summary>
public sealed partial class MainWindowViewModel
{
    [RelayCommand(CanExecute = nameof(CanAutomate))]
    private async Task WorkflowsAsync()
    {
        if (_automation is not { } automation)
        {
            return;
        }

        var dialog = new WorkflowsViewModel(automation.Library, automation.Catalog, _fileDialogs, ActiveDocument is { IsReadOnly: false });
        if (!_pageDialogs.ShowWorkflows(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        if (choice.Target == WorkflowRunTarget.Document)
        {
            await RunWorkflowOnDocumentAsync(automation, choice.Workflow);
        }
        else
        {
            await OpenBatchAsync(choice.Workflow);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAutomate))]
    private Task BatchAsync() => OpenBatchAsync(null);

    private bool CanAutomate() => _automation is not null && _activeSave is null && !StatusBar.IsTaskRunning;

    private async Task OpenBatchAsync(Workflow? workflow)
    {
        if (_automation is not { } automation)
        {
            return;
        }

        await WaitForBackgroundWorkAsync(cancelPrint: false);
        using var batch = new BatchViewModel(automation, _fileDialogs, _shell, PrepareSecrets, AutomationWorkFolder, workflow, _logger);
        _pageDialogs.ShowBatch(batch);
    }

    /// <summary>Where workflows keep their intermediate files (removed after each run).</summary>
    private string AutomationWorkFolder => Path.Combine(_paths.CacheDirectory, "automation");

    /// <summary>
    /// The open document as it is shown goes through the workflow; the result goes to a folder the user picks (or next
    /// to the document with the workflow's suffix) — never over the document's own file — and a single PDF opens.
    /// </summary>
    private async Task RunWorkflowOnDocumentAsync(AutomationServices automation, Workflow workflow)
    {
        if (ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsReadOnly || document.Workspace.Protection is not null)
        {
            await _messages.ShowErrorAsync(Strings.Workflow_Error_Title, Strings.Optimize_Error_Protected);
            return;
        }

        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        var documentFolder = Path.GetDirectoryName(document.FilePath) is { Length: > 0 } where ? where : null;
        var folder = workflow.Destination.Mode == WorkflowOutputMode.Suffix && documentFolder is not null
            ? documentFolder
            : _fileDialogs.PickFolder(Strings.Workflow_Folder_Title, documentFolder);
        if (folder is null || PrepareSecrets(workflow) is not { } secrets)
        {
            return;
        }

        if (workflow.Destination.Mode == WorkflowOutputMode.ReplaceInput)
        {
            workflow = workflow with { Output = new WorkflowOutput(WorkflowOutputMode.Folder) };
        }

        var (input, workingFolder) = await DocumentFileAsync(document, "workflow");
        IReadOnlyList<string> outputs = [];
        try
        {
            if (input is null)
            {
                await _messages.ShowErrorAsync(Strings.Workflow_Error_Title, Strings.Optimize_Error_Unreadable);
                return;
            }

            var request = new WorkflowRunRequest(input) { OutputFolder = folder, WorkingFolder = document.Workspace.WorkDirectory, Secrets = secrets };
            var done = await RunOptimizingAsync(
                Format(Strings.Workflow_Progress, workflow.Name),
                async (progress, token) =>
                {
                    var steps = new Progress<WorkflowProgress>(p => progress.Report(new OperationProgress((int)(p.Fraction * 1000), 1000)));
                    outputs = (await automation.Runner.RunAsync(workflow, request, steps, token).ConfigureAwait(false)).Outputs;
                },
                Format(Strings.Workflow_Done, workflow.Name),
                Strings.Workflow_Error_Title,
                ex => ex is WorkflowFailedException failed ? Explain(failed) : null);
            if (!done)
            {
                return;
            }
        }
        finally
        {
            secrets.Dispose();
            if (workingFolder is not null)
            {
                TryDeleteFolder(workingFolder);
            }
        }

        _logger.LogInformation("A workflow ran on the open document: {Steps} steps, {Outputs} files", workflow.EnabledSteps.Count(), outputs.Count);
        if (outputs.Count == 1 && outputs[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            await OpenFilesAsync(outputs);
        }
        else if (outputs.Count > 0)
        {
            _shell.OpenFolder(folder);
        }
    }

    /// <summary>Why a workflow failed on a file, in the user's language (no document content).</summary>
    internal static string Explain(WorkflowFailedException failed)
    {
        var inner = BatchRunner.Innermost(failed);
        var reason = failed.Failure switch
        {
            WorkflowFailure.Invalid => string.Join(Environment.NewLine, failed.Issues.Select(WorkflowEditorViewModel.Describe)),
            WorkflowFailure.Input => Strings.Workflow_Failure_Input,
            WorkflowFailure.Password => Strings.Workflow_Failure_Password,
            WorkflowFailure.Secrets => Strings.Workflow_Failure_Secrets,
            WorkflowFailure.Parameters => Format(Strings.Workflow_Failure_Parameters, inner.Message),
            WorkflowFailure.Output => Strings.Workflow_Failure_Output,
            _ when inner is OcrException { Failure: OcrFailure.NotInstalled or OcrFailure.LanguageMissing } => Strings.Workflow_Failure_NoOcr,
            _ when inner is LibreOfficeNotFoundException => Strings.OfficeToPdf_Error_NoLibreOffice,
            _ when inner is Core.ExternalTools.ExternalToolNotFoundException => Strings.Optimize_Error_NoQpdf,
            _ when BatchRunner.IsUnreadableFile(failed) => Strings.Workflow_Failure_Unreadable,
            _ when inner is Core.Operations.WorkerException { Failure: Core.Operations.WorkerFailure.TimedOut } => Strings.Content_Error_WorkerTimedOut,
            _ when inner is Core.Operations.WorkerException => Strings.Content_Error_WorkerStopped,
            _ => Format(Strings.Workflow_Failure_Step, BatchRunner.ErrorTypeName(failed)),
        };
        return failed.Step is { } step
            ? Format(Strings.Workflow_Failure_AtStep, step + 1, OperationNames.Of(failed.Operation ?? string.Empty), reason)
            : reason;
    }

    /// <summary>
    /// Asks, before a workflow runs, for what its steps need and a workflow never keeps: the passwords of a protection
    /// step (the protection dialog, with the algorithm and permissions the step keeps), the certificate and look of a
    /// signing step (the signing dialog for many files: visible signatures go in a page corner). Null when the user
    /// gave up.
    /// </summary>
    private PreparedSecrets? PrepareSecrets(Workflow workflow)
    {
        var secrets = new PreparedSecrets();
        foreach (var (index, step) in workflow.EnabledSteps)
        {
            var stored = step.Parameters ?? [];
            switch (step.Operation)
            {
                case ProtectDocumentOperation.OperationId:
                    if (AskProtection(stored) is not { } protect)
                    {
                        secrets.Dispose();
                        return null;
                    }

                    secrets.Add(index, _ => protect);
                    break;
                case SignOperation.OperationId:
                    if (AskSigning(stored, secrets) is not { } sign)
                    {
                        secrets.Dispose();
                        return null;
                    }

                    secrets.Add(index, sign);
                    break;
            }
        }

        return secrets;
    }

    private ProtectParams? AskProtection(JsonObject stored)
    {
        var security = stored["security"] as JsonObject;
        var algorithm = security?["algorithm"]?.Deserialize<EncryptionAlgorithm>(ParameterSchema.Json) ?? EncryptionAlgorithm.Aes256;
        var permissions = security?["permissions"]?.Deserialize<DocumentPermissions>(ParameterSchema.Json) ?? DocumentPermissions.All;
        var dialog = new SecurityViewModel(new ProtectionSetting(Guid.NewGuid(), algorithm, permissions, HasOpenPassword: true), known: null, needsPasswords: true);
        if (!_pageDialogs.ShowSecurity(dialog) || dialog.Result is not { Setting: { } setting, Passwords: { Owner: { } owner } passwords })
        {
            return null;
        }

        var overwrite = stored["overwrite"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
        return new ProtectParams(new DocumentSecurity(setting.HasOpenPassword ? passwords.Open : null, owner, setting.Algorithm, setting.Permissions), overwrite);
    }

    private Func<WorkflowSecretRequest, Task<object?>>? AskSigning(JsonObject stored, PreparedSecrets secrets)
    {
        if (_signing is not { } signing)
        {
            return null;
        }

        var library = signing.Signatures;
        var dialog = new SignDialogViewModel(
            signing.Certificates,
            _fileDialogs,
            [],
            library?.List() ?? [],
            id => library?.Load(id),
            _settings.Current.TimestampServer,
            _settings.Current.UseTimestamp,
            fileCount: 2);
        if (!_pageDialogs.ShowSign(dialog) || dialog.Result is not { } choice)
        {
            return null;
        }

        var certificate = choice.Certificate;
        secrets.Keep(certificate);
        var overwrite = stored["overwrite"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
        return async request =>
        {
            var file = await File.ReadAllBytesAsync(request.Input).ConfigureAwait(false);
            SignatureAppearance? appearance = null;
            if (choice.Placement == SignaturePlacementKind.Corner)
            {
                var (page, rect) = SignatureCorner.Place(file, choice.CornerPage);
                appearance = new SignatureAppearance(page, rect, choice.Lines(DateTimeOffset.Now), choice.Image);
            }

            var sign = new SignRequest(FreeSignatureFieldName(FieldsOf(file)), appearance, choice.Reason, choice.Location, choice.Contact, choice.TimestampServer, Pades: choice.Pades, LongTerm: choice.LongTerm);
            return new SignParams(sign, certificate, overwrite);
        };
    }
}
