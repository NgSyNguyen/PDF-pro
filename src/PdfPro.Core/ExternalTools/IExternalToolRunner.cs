namespace PdfPro.Core.ExternalTools;

/// <summary>Runs external programs without a shell, with timeout, cancellation and captured output.</summary>
public interface IExternalToolRunner
{
    /// <exception cref="ExternalToolNotFoundException">The executable does not exist.</exception>
    /// <exception cref="ExternalToolTimeoutException">The process tree was killed after <see cref="ExternalToolRequest.Timeout"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the process tree was killed.</exception>
    Task<ExternalToolResult> RunAsync(ExternalToolRequest request, CancellationToken cancellationToken = default);
}
