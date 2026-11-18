using System.Text;

namespace PdfPro.Core.ExternalTools;

/// <summary>
/// How to run an external program (qpdf, tesseract, soffice…). Arguments are passed one by one through
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, never through a shell.
/// </summary>
public sealed record ExternalToolRequest
{
    public ExternalToolRequest(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        FileName = fileName;
        Arguments = arguments;
    }

    /// <summary>Executable path, or a bare name resolved through PATH.</summary>
    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; init; }

    /// <summary>The process tree is killed when this elapses. Default: 5 minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Variables to set (or remove, when the value is null) for the child process.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Arguments are not logged by default because they may contain passwords or personal file names.
    /// Set to true only when every argument is safe to write to the log.
    /// </summary>
    public bool LogArguments { get; init; }

    /// <summary>Maximum characters kept from each of stdout and stderr; the rest is drained and dropped.</summary>
    public int MaxOutputChars { get; init; } = 4 * 1024 * 1024;

    public Encoding OutputEncoding { get; init; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
