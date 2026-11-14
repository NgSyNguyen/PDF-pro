using System.Collections.Concurrent;
using System.Text.Json;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations;
using PdfPro.Core.ExternalTools;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Optimizing;

namespace PdfPro.Worker;

/// <summary>
/// The worker's side of the pipe: each "Run" message runs a content operation from the input file into the output
/// file, sending its progress and then its result (with the report of an <see cref="IReportingOperation"/>); "Cancel"
/// stops a run. When the app closes the pipe, every run is cancelled and the worker ends.
/// </summary>
internal sealed class WorkerServer(Stream pipe, OperationCatalog catalog, IReadOnlyList<IMeasuringOperation>? measurements = null, WorkflowRunner? workflows = null)
{
    /// <summary>Progress is sent at most this often (the last report of a stage always goes).</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _runs = new();

    public async Task ServeAsync()
    {
        var running = new List<Task>();
        try
        {
            while (await WorkerFraming.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false) is { } message)
            {
                switch (message.Kind)
                {
                    case WorkerMessageKind.Run:
                        running.RemoveAll(t => t.IsCompleted);
                        running.Add(Task.Run(() => RunAsync(message)));
                        break;
                    case WorkerMessageKind.Cancel when _runs.TryGetValue(message.Id, out var run):
                        await run.CancelAsync().ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            // The app is gone.
        }

        foreach (var run in _runs.Values)
        {
            await run.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task RunAsync(WorkerMessage request)
    {
        using var cancel = new CancellationTokenSource();
        _runs[request.Id] = cancel;
        WorkerMessage reply;
        try
        {
            var report = await ApplyAsync(request, cancel.Token).ConfigureAwait(false);
            reply = new WorkerMessage(WorkerMessageKind.Result, request.Id) { Outcome = WorkerOutcome.Succeeded, Report = report };
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            reply = new WorkerMessage(WorkerMessageKind.Result, request.Id) { Outcome = WorkerOutcome.Cancelled };
        }
        catch (Exception ex)
        {
            reply = new WorkerMessage(WorkerMessageKind.Result, request.Id)
            {
                Outcome = WorkerOutcome.Failed,
                ErrorType = ex.GetType().FullName,
                ErrorMessage = ex.Message,
                ErrorDetail = ex is WorkflowFailedException failed ? WorkflowProgressCodec.Describe(failed, DetailOf) : DetailOf(ex),
            };
        }
        finally
        {
            _runs.TryRemove(request.Id, out _);
        }

        await SendAsync(reply).ConfigureAwait(false);
    }

    /// <summary>What the app needs to rebuild an error it explains.</summary>
    private static string? DetailOf(Exception error) => error switch
    {
        OperationValidationException validation => JsonSerializer.Serialize(validation.Errors),
        OcrException ocr => ocr.Detail is null ? ocr.Failure.ToString() : $"{ocr.Failure}\n{ocr.Detail}",
        ImageFileException image => $"{image.FileIndex}\n{image.Problem}",
        RepairFailedException repair => JsonSerializer.Serialize(repair.Attempts.Select(a => new[] { a.Method.ToString(), a.Failure.ToString() })),
        ExternalToolNotFoundException tool => tool.ToolName,
        _ => null,
    };

    /// <summary>Runs a workflow on one file; its result as JSON.</summary>
    private async Task<string?> RunWorkflowAsync(WorkerMessage request, CancellationToken cancellationToken)
    {
        if (workflows is null)
        {
            throw new NotSupportedException("This worker does not run workflows.");
        }

        if (request is not { Parameters: { } json, Input: { } input })
        {
            throw new InvalidDataException("A workflow run needs its call and an input.");
        }

        var call = JsonSerializer.Deserialize<WorkerWorkflowCall>(json, ParameterSchema.Json) ?? throw new InvalidDataException("The workflow is missing.");
        var run = new WorkflowRunRequest(input) { OutputFolder = call.OutputFolder, WorkingFolder = call.WorkingFolder, Password = request.Password };
        IProgress<OperationProgress> sent = new SentProgress(this, request.Id);
        var result = await workflows.RunAsync(WorkflowFile.Read(call.Workflow), run, new Direct<WorkflowProgress>(p => sent.Report(WorkflowProgressCodec.Encode(p))), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, ParameterSchema.ReportJson);
    }

    /// <summary>Reports at once, on the reporting thread (Progress&lt;T&gt; would post to the thread pool, out of order).</summary>
    private sealed class Direct<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Runs the operation; its report as JSON, or null.</summary>
    private async Task<string?> ApplyAsync(WorkerMessage request, CancellationToken cancellationToken)
    {
        if (request.Export)
        {
            return await ExportAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.Measure)
        {
            return await MeasureAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.Workflow)
        {
            return await RunWorkflowAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request is not { Operation: { } id, Parameters: { } json, Input: { } input, Output: { } output })
        {
            throw new InvalidDataException("A run needs an operation, its parameters, an input and an output.");
        }

        if (catalog.Find(id) is not IContentOperation operation || catalog.ParameterType(id) is not { } type)
        {
            throw new NotSupportedException($"'{id}' does not run in the worker.");
        }

        var parameters = JsonSerializer.Deserialize(json, type, ParameterSchema.Json) ?? throw new InvalidDataException("The parameters are missing.");
        var progress = new SentProgress(this, request.Id);
        await using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await operation.ApplyToFileAsync(input, stream, parameters, progress, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        return operation is IReportingOperation reporting && reporting.ReportOf(parameters) is { } report
            ? JsonSerializer.Serialize(report, reporting.ReportType, ParameterSchema.ReportJson)
            : null;
    }

    /// <summary>Looks at a file for a scan tool; what it finds as JSON.</summary>
    private async Task<string?> MeasureAsync(WorkerMessage request, CancellationToken cancellationToken)
    {
        if (request is not { Operation: { } id, Parameters: { } json, Input: { } input })
        {
            throw new InvalidDataException("A measurement needs an operation, its parameters and an input.");
        }

        if ((catalog.Find(id) as IMeasuringOperation ?? measurements?.FirstOrDefault(m => m.Id == id)) is not { } operation)
        {
            throw new NotSupportedException($"'{id}' does not measure in the worker.");
        }

        var parameters = JsonSerializer.Deserialize(json, operation.MeasureType, ParameterSchema.Json) ?? throw new InvalidDataException("The parameters are missing.");
        var result = await operation.MeasureAsync(input, parameters, new SentProgress(this, request.Id), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, operation.MeasurementType, ParameterSchema.ReportJson);
    }

    /// <summary>Runs an export (compress, convert…); its result as JSON.</summary>
    private async Task<string?> ExportAsync(WorkerMessage request, CancellationToken cancellationToken)
    {
        if (request is not { Operation: { } id, Parameters: { } json, Output: { } output })
        {
            throw new InvalidDataException("An export needs an operation, its parameters and an output.");
        }

        if (catalog.Find(id) is not IExportOperation operation || catalog.ParameterType(id) is not { } type)
        {
            throw new NotSupportedException($"'{id}' does not run in the worker.");
        }

        var inputs = request.Inputs ?? (request.Input is { } input ? [input] : []);
        var parameters = JsonSerializer.Deserialize(json, type, ParameterSchema.Json) ?? throw new InvalidDataException("The parameters are missing.");
        var result = await operation.ExportAsync(new ExportCall(inputs, output, request.Password, request.Name), parameters, new SentProgress(this, request.Id), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, operation.ResultType, ParameterSchema.ReportJson);
    }

    private async Task SendAsync(WorkerMessage message)
    {
        await _sending.WaitAsync().ConfigureAwait(false);
        try
        {
            await WorkerFraming.WriteAsync(pipe, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The app is gone; the reading loop ends the worker.
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Progress sent to the app, at most every <see cref="ProgressInterval"/> within a stage.</summary>
    private sealed class SentProgress(WorkerServer server, int id) : IProgress<OperationProgress>
    {
        private readonly Lock _gate = new();
        private DateTime _last = DateTime.MinValue;
        private string? _stage;

        public void Report(OperationProgress value)
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (value.StageKey == _stage && value.Completed < value.Total && now - _last < ProgressInterval)
                {
                    return;
                }

                _last = now;
                _stage = value.StageKey;
            }

            _ = server.SendAsync(new WorkerMessage(WorkerMessageKind.Progress, id) { Done = value.Completed, Total = value.Total, Stage = value.StageKey });
        }
    }
}
