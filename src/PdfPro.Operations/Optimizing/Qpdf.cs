using PdfPro.Core.ExternalTools;

namespace PdfPro.Operations.Optimizing;

/// <summary>Where qpdf is: the copy fetched into tools/qpdf (Windows), or a name found on PATH.</summary>
public sealed record QpdfOptions(string ExecutablePath)
{
    /// <summary>The qpdf of a tools folder when it is there, else "qpdf" from PATH.</summary>
    public static QpdfOptions In(string toolsDirectory)
    {
        var bundled = Path.Combine(toolsDirectory, "qpdf", "bin", OperatingSystem.IsWindows() ? "qpdf.exe" : "qpdf");
        return new QpdfOptions(File.Exists(bundled) ? bundled : "qpdf");
    }
}

/// <summary>qpdf failed on a file (it found errors it could not get past).</summary>
public sealed class QpdfException(int exitCode, Exception? inner = null)
    : Exception($"qpdf failed with exit code {exitCode}.", inner)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// qpdf (Apache-2.0) run through <see cref="IExternalToolRunner"/> — never a shell, arguments one by one, a timeout,
/// cancellable. Exit code 3 (warnings, for instance a damaged file it recovered) still wrote the output.
/// </summary>
public sealed class Qpdf(IExternalToolRunner runner, QpdfOptions options)
{
    public const string ToolName = "qpdf";

    /// <summary>How long one file may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>Fast Web View: the file reordered so the first page shows before the rest arrives (and object streams).</summary>
    /// <exception cref="ExternalToolNotFoundException">qpdf is not installed.</exception>
    /// <exception cref="QpdfException">qpdf could not read or write the file.</exception>
    public Task<bool> LinearizeAsync(string input, string output, CancellationToken cancellationToken)
        => RunAsync(["--linearize", "--object-streams=generate", input, output], cancellationToken);

    /// <summary>The file written anew: compact (object streams, a cross-reference stream, streams compressed).</summary>
    public Task<bool> CompactAsync(string input, string output, CancellationToken cancellationToken)
        => RunAsync(["--object-streams=generate", "--compress-streams=y", "--recompress-flate", "--compression-level=9", input, output], cancellationToken);

    /// <summary>The file read as well as qpdf can (it rebuilds a damaged cross-reference table) and written anew.</summary>
    public Task<bool> RewriteAsync(string input, string output, CancellationToken cancellationToken)
        => RunAsync([input, output], cancellationToken);

    /// <returns>True when qpdf had warnings (the output was still written).</returns>
    private async Task<bool> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ExternalToolRequest(options.ExecutablePath, arguments) { Timeout = Timeout }, cancellationToken).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => false,
            3 => true,
            _ => throw new QpdfException(result.ExitCode),
        };
    }
}
