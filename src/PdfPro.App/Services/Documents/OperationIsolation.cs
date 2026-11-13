using System.IO;
using System.Text.Json;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Infrastructure.Settings;
using PdfPro.Infrastructure.Worker;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Ocr;
using PdfPro.Operations.Scanning;
using PdfPro.Operations.Security;

namespace PdfPro.App.Services.Documents;

/// <summary>Runs content operations in the worker process instead of the app (seam for tests).</summary>
public interface IOperationIsolation
{
    /// <summary>Whether this operation runs in the worker now (the setting is on and the operation is a heavy one).</summary>
    bool Isolates(IContentOperation operation);

    /// <summary>Does what <see cref="IContentOperation.ApplyToFileAsync"/> does, in the worker; its report comes back to <paramref name="operation"/>.</summary>
    /// <exception cref="WorkerException">The worker could not start, crashed or overran; the next run starts a new one.</exception>
    Task ApplyAsync(IContentOperation operation, string inputPath, Stream output, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// The operations a damaged or hostile file is most likely to crash — they render every page, rewrite page content
/// through PDFium or run Tesseract on it — go to PdfPro.Worker when "Chạy thao tác nặng trong tiến trình riêng" is on
/// (SPEC Phase 13). The result is written to a file of its own next to the output, then copied into it.
/// </summary>
internal sealed class WorkerIsolation(IWorkerClient worker, ISettingsService settings) : IOperationIsolation
{
    /// <summary>The operations sent to the worker.</summary>
    public static IReadOnlySet<string> Heavy { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        OcrOperation.OperationId,
        RedactOperation.OperationId,
        SanitizeOperation.OperationId,
        DeskewOperation.OperationId,
        CleanBordersOperation.OperationId,
    };

    public bool Isolates(IContentOperation operation)
        => settings.Current.IsolateOperations && Heavy.Contains(operation.Id) && worker.IsAvailable;

    public async Task ApplyAsync(IContentOperation operation, string inputPath, Stream output, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(parameters);
        var written = Path.Combine(Path.GetDirectoryName(inputPath)!, $"{Guid.NewGuid():N}.worker.pdf");
        try
        {
            var json = JsonSerializer.Serialize(parameters, parameters.GetType(), ParameterSchema.Json);
            var report = await worker.RunAsync(new WorkerRun(operation.Id, json, inputPath, written), progress, cancellationToken).ConfigureAwait(false);
            await using (var result = new FileStream(written, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await result.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            if (report is not null && operation is IReportingOperation reporting && JsonSerializer.Deserialize(report, reporting.ReportType, ParameterSchema.ReportJson) is { } read)
            {
                reporting.Remember(parameters, read);
            }
        }
        finally
        {
            try
            {
                File.Delete(written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The work folder is cleaned with the session.
            }
        }
    }
}
