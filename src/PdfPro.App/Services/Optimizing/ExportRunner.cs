using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure.Settings;
using PdfPro.Infrastructure.Worker;
using PdfPro.Operations;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Optimizing;

namespace PdfPro.App.Services.Optimizing;

/// <summary>Runs the file tools' operations (compress, convert, repair…): in the worker process when it is on.</summary>
public interface IExportRunner
{
    /// <summary>Runs <paramref name="operation"/> and returns its result (a report, the files written…).</summary>
    Task<TResult> RunAsync<TResult>(IExportOperation operation, ExportCall call, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}

/// <summary>Runs export operations in the app's own process (tests, the worker off or missing).</summary>
public sealed class InProcessExportRunner : IExportRunner
{
    public static InProcessExportRunner Instance { get; } = new();

    public async Task<TResult> RunAsync<TResult>(IExportOperation operation, ExportCall call, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return (TResult)await operation.ExportAsync(call, parameters, progress, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Runs export operations in the worker process (SPEC Phase 13, Settings › "Chạy thao tác nặng trong tiến trình
/// riêng"): a file that crashes or exhausts PDFium, PdfPig or Skia — a page that is a "content bomb" — stops the worker,
/// never the window. The errors the file tools explain come back as their own types; a worker that does not start lets
/// the operation run in the app.
/// </summary>
internal sealed class WorkerExportRunner(IWorkerClient worker, ISettingsService settings, ILogger<WorkerExportRunner> logger) : IExportRunner
{
    public async Task<TResult> RunAsync<TResult>(IExportOperation operation, ExportCall call, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!settings.Current.IsolateOperations || !worker.IsAvailable)
        {
            return await InProcessExportRunner.Instance.RunAsync<TResult>(operation, call, parameters, progress, cancellationToken).ConfigureAwait(false);
        }

        var json = JsonSerializer.Serialize(parameters, parameters.GetType(), ParameterSchema.Json);
        var run = new WorkerRun(operation.Id, json, call.Inputs.FirstOrDefault() ?? string.Empty, call.Output)
        {
            Export = true,
            Inputs = call.Inputs,
            Password = call.Password,
            Name = call.Name,
        };

        string? result;
        try
        {
            result = await worker.RunAsync(run, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerException ex) when (ex.Failure == WorkerFailure.NotStarted)
        {
            logger.LogWarning("The worker did not start: {OperationId} runs in the app", operation.Id);
            return await InProcessExportRunner.Instance.RunAsync<TResult>(operation, call, parameters, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerOperationException ex)
        {
            throw Rebuild(ex);
        }

        return result is not null && JsonSerializer.Deserialize(result, operation.ResultType, ParameterSchema.ReportJson) is TResult read
            ? read
            : throw new InvalidDataException("The worker did not return the result of the operation.");
    }

    /// <summary>
    /// The worker's error as the type the file tools explain (a picture that cannot be read, the ways a repair tried);
    /// anything else as a file that could not be read or written.
    /// </summary>
    internal static Exception Rebuild(WorkerOperationException error)
    {
        try
        {
            if (error.ErrorType == typeof(ImageFileException).FullName
                && error.Detail?.Split('\n') is [var index, var problem]
                && int.TryParse(index, out var fileIndex)
                && Enum.TryParse<ImageFileProblem>(problem, out var kind))
            {
                return new ImageFileException(fileIndex, kind);
            }

            if (error.ErrorType == typeof(RepairFailedException).FullName
                && error.Detail is { } detail
                && JsonSerializer.Deserialize<string[][]>(detail) is { } attempts)
            {
                return new RepairFailedException([.. attempts.Select(a => (Enum.Parse<RepairMethod>(a[0]), Enum.Parse<RepairAttemptFailure>(a[1])))]);
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IndexOutOfRangeException)
        {
            // Told as a file that could not be read, below.
        }

        return new InvalidDataException(error.Message, error);
    }
}
