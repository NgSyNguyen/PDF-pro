using PdfPro.Core.Operations;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Robustness;

/// <param name="Dpi">How finely the pages are drawn (small: the point is to draw each one once).</param>
public sealed record FileProbeParams(int Dpi = 36);

/// <summary>What looking at a file found.</summary>
/// <param name="Pages">The pages drawn.</param>
/// <param name="NeedsPassword">The file is protected: it could not be drawn without its password.</param>
public sealed record FileProbeResult(int Pages, bool NeedsPassword);

/// <summary>
/// Looks at a file the way the viewer will (SPEC Phase 13, "Chế độ an toàn"): PDFium opens it, draws every page small and
/// reads its text layer. Run in the worker process, a file that crashes PDFium stops the worker, not the window. A file
/// PDFium refuses (damaged, protected) is not a crash: the viewer explains it as usual.
/// </summary>
public sealed class FileProbe(PdfiumGate? gate) : IMeasuringOperation
{
    public const string OperationId = "safety.probe";

    public const string Stage = "Stage_Probe";

    public string Id => OperationId;

    public Type MeasureType => typeof(FileProbeParams);

    public Type MeasurementType => typeof(FileProbeResult);

    /// <summary>PDFium (the app's side only names the probe: the worker's draws).</summary>
    private PdfiumGate Gate => gate ?? throw new InvalidOperationException("Looking at a file needs PDFium: the probe was built without it.");

    /// <exception cref="PdfDocumentException">PDFium cannot open the file (other than for a password).</exception>
    public async Task<FileProbeResult> ProbeAsync(string path, FileProbeParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(progress);
        PdfiumDocument? document = null;
        try
        {
            int count;
            try
            {
                count = await Gate.RunAsync(
                    () =>
                    {
                        document = PdfiumDocument.Open(Gate, path);
                        return document.PageCount;
                    },
                    PdfiumPriority.Low,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PdfDocumentException ex) when (ex.Failure is PdfOpenFailure.PasswordRequired or PdfOpenFailure.WrongPassword)
            {
                return new FileProbeResult(0, NeedsPassword: true);
            }

            var scale = Math.Clamp(parameters.Dpi, 9, 150) / 72.0;
            for (var i = 0; i < count; i++)
            {
                progress.Report(new OperationProgress(i, count, Stage));
                var page = i;
                await Gate.RunAsync(
                    () =>
                    {
                        using var bitmap = document!.RenderPage(page, new PageRenderOptions { Scale = scale });
                        document.GetPageTextCounts(page);
                    },
                    PdfiumPriority.Low,
                    cancellationToken).ConfigureAwait(false);
            }

            progress.Report(new OperationProgress(count, count, Stage));
            return new FileProbeResult(count, NeedsPassword: false);
        }
        finally
        {
            if (document is { } opened)
            {
                await Gate.RunAsync(opened.Dispose, PdfiumPriority.High, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    async Task<object> IMeasuringOperation.MeasureAsync(string input, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => await ProbeAsync(input, parameters as FileProbeParams ?? new FileProbeParams(), progress, cancellationToken).ConfigureAwait(false);
}
