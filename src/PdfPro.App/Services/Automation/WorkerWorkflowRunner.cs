using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure.Settings;
using PdfPro.Infrastructure.Worker;
using PdfPro.Operations;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Converting;

namespace PdfPro.App.Services.Automation;

/// <summary>
/// Runs a workflow on one file of a batch or of the hot folder in the worker process (SPEC Phase 13) when "Chạy thao
/// tác nặng trong tiến trình riêng" is on: a file that crashes or exhausts PDFium, PdfPig or Tesseract — unattended, in
/// the hot folder — stops the worker, never the window, and the next file gets a new worker. A workflow with a step that
/// asks for a password or a certificate when it runs (protection, signing: the certificate may live on a token) runs in
/// the app, as does any run when the worker is off, missing or does not start. A worker runs one file at a time: the
/// files a batch runs at once each get a worker of their own (<paramref name="newWorker"/>), kept for the next files.
/// </summary>
public sealed class WorkerWorkflowRunner(WorkflowRunner inApp, Func<IWorkerClient> newWorker, ISettingsService settings, ILogger<WorkerWorkflowRunner> logger)
    : IWorkflowFileRunner, IAsyncDisposable
{
    private readonly ConcurrentBag<IWorkerClient> _idle = [];
    private readonly ConcurrentQueue<IWorkerClient> _all = new();

    public OperationCatalog Catalog => inApp.Catalog;

    public async ValueTask DisposeAsync()
    {
        while (_all.TryDequeue(out var worker))
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<WorkflowRunResult> RunAsync(Workflow workflow, WorkflowRunRequest request, IProgress<WorkflowProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(request);
        if (!RunsInWorker(workflow) || Rent() is not { } worker)
        {
            return await inApp.RunAsync(workflow, request, progress, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await RunAsync(worker, workflow, request, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _idle.Add(worker);
        }
    }

    private async Task<WorkflowRunResult> RunAsync(IWorkerClient worker, Workflow workflow, WorkflowRunRequest request, IProgress<WorkflowProgress>? progress, CancellationToken cancellationToken)
    {

        var call = new WorkerWorkflowCall(WorkflowFile.Write(workflow), request.OutputFolder, request.WorkingFolder);
        var run = new WorkerRun(WorkflowsInWorker, JsonSerializer.Serialize(call, ParameterSchema.Json), request.Input, string.Empty)
        {
            Workflow = true,
            Password = request.Password,
        };
        var sent = new Progress<OperationProgress>(p =>
        {
            if (progress is not null && WorkflowProgressCodec.Decode(p) is { } step)
            {
                progress.Report(step);
            }
        });

        string? result;
        try
        {
            result = await worker.RunAsync(run, sent, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerException ex) when (ex.Failure == WorkerFailure.NotStarted)
        {
            logger.LogWarning("The worker did not start: the workflow runs in the app");
            return await inApp.RunAsync(workflow, request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerException ex)
        {
            // The worker died or overran on this file: the file failed, the next one gets a new worker.
            throw new WorkflowFailedException(WorkflowFailure.Step, innerException: ex);
        }
        catch (WorkerOperationException ex)
        {
            throw Rebuild(ex);
        }

        return result is not null && JsonSerializer.Deserialize<WorkflowRunResult>(result, ParameterSchema.ReportJson) is { } read
            ? read
            : throw new WorkflowFailedException(WorkflowFailure.Step, innerException: new InvalidDataException("The worker did not return the result of the workflow."));
    }

    /// <summary>The name the worker's workflow runs go by (not an operation of the catalog).</summary>
    public const string WorkflowsInWorker = "workflow.run";

    /// <summary>Whether the run goes to a worker: they are on, and no step asks for a secret when it runs.</summary>
    internal bool RunsInWorker(Workflow workflow)
        => settings.Current.IsolateOperations
            && workflow.EnabledSteps.All(s => inApp.Catalog.Find(s.Step.Operation) is { } operation && !WorkflowValidator.AsksWhenRun(operation));

    /// <summary>An idle worker, or a new one; null when the worker's program is not there.</summary>
    private IWorkerClient? Rent()
    {
        if (_idle.TryTake(out var idle))
        {
            return idle;
        }

        var worker = newWorker();
        _all.Enqueue(worker);
        if (worker.IsAvailable)
        {
            return worker;
        }

        _idle.Add(worker);
        return null;
    }

    /// <summary>
    /// The worker's error as the app explains it: a workflow failure with its step, issues and the error at its bottom
    /// (OCR not installed, a missing tool or LibreOffice, a file that cannot be read…); anything else as a failed step.
    /// </summary>
    internal static WorkflowFailedException Rebuild(WorkerOperationException error)
    {
        if (error.ErrorType == typeof(WorkflowFailedException).FullName && error.Detail is { } json)
        {
            try
            {
                if (JsonSerializer.Deserialize<WorkflowFailureDetail>(json, ParameterSchema.ReportJson) is { } detail)
                {
                    return new WorkflowFailedException(detail.Failure, detail.Step, detail.Operation, Inner(detail), detail.Issues, detail.Messages);
                }
            }
            catch (JsonException)
            {
                // Told as a failed step, below.
            }
        }

        return new WorkflowFailedException(WorkflowFailure.Step, innerException: error);
    }

    private static Exception? Inner(WorkflowFailureDetail detail)
    {
        if (detail.InnerType is not { } type)
        {
            return null;
        }

        var message = detail.InnerMessage ?? string.Empty;
        if (type == typeof(OcrException).FullName && detail.InnerDetail?.Split('\n', 2) is [var failure, ..] parts && Enum.TryParse<OcrFailure>(failure, out var kind))
        {
            return new OcrException(kind, parts.Length > 1 ? parts[1] : null);
        }

        if (type == typeof(ExternalToolNotFoundException).FullName && detail.InnerDetail is { } tool)
        {
            return new ExternalToolNotFoundException(tool);
        }

        if (type == typeof(LibreOfficeNotFoundException).FullName)
        {
            return new LibreOfficeNotFoundException();
        }

        if (type == typeof(OperationValidationException).FullName)
        {
            return new InvalidOperationException(message);
        }

        // Unreadable files (PDFium, PdfPig, PDFsharp) are told as such; anything else keeps its type's name.
        return new WorkerOperationException(type, message, detail.InnerDetail);
    }
}
