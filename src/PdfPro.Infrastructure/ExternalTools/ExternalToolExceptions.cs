namespace PdfPro.Infrastructure.ExternalTools;

/// <summary>Base type for failures of external programs.</summary>
public class ExternalToolException : Exception
{
    public ExternalToolException(string toolName, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ToolName = toolName;
    }

    public string ToolName { get; }
}

/// <summary>The executable does not exist (tool not installed or not fetched into tools/).</summary>
public sealed class ExternalToolNotFoundException(string toolName, Exception? innerException = null)
    : ExternalToolException(toolName, $"External tool '{toolName}' was not found.", innerException);

/// <summary>The process did not finish in time and its process tree was killed.</summary>
public sealed class ExternalToolTimeoutException(string toolName, TimeSpan timeout, string standardErrorTail)
    : ExternalToolException(toolName, $"External tool '{toolName}' did not finish within {timeout.TotalSeconds:0.#} s.")
{
    public TimeSpan Timeout { get; } = timeout;

    public string StandardErrorTail { get; } = standardErrorTail;
}

/// <summary>The process finished with a non-zero exit code (raised by <see cref="ExternalToolResult.EnsureSuccess"/>).</summary>
public sealed class ExternalToolFailedException(string toolName, int exitCode, string standardErrorTail)
    : ExternalToolException(toolName, $"External tool '{toolName}' failed with exit code {exitCode}.")
{
    public int ExitCode { get; } = exitCode;

    public string StandardErrorTail { get; } = standardErrorTail;
}
