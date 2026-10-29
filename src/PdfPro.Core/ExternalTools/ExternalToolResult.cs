namespace PdfPro.Core.ExternalTools;

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
        => Succeeded ? this : throw new ExternalToolFailedException(ToolName, ExitCode, Tail(StandardError));

    /// <summary>The last characters of a tool's output, for logs and error messages.</summary>
    public static string Tail(string text, int maxLength = 2000)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= maxLength ? text : text[^maxLength..];
    }
}
