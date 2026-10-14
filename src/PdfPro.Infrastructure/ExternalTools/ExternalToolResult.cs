namespace PdfPro.Infrastructure.ExternalTools;

/// <summary>Outcome of a finished external process. A non-zero exit code is not an exception by itself.</summary>
public sealed record ExternalToolResult(
    string ToolName,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool OutputTruncated)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>Throws <see cref="ExternalToolFailedException"/> when the exit code is not 0.</summary>
    public ExternalToolResult EnsureSuccess()
        => Succeeded ? this : throw new ExternalToolFailedException(ToolName, ExitCode, ExternalToolText.Tail(StandardError));
}
