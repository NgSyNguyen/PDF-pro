using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PdfPro.Core.ExternalTools;

namespace PdfPro.Infrastructure.ExternalTools;

/// <summary>
/// <see cref="IExternalToolRunner"/> on top of <see cref="Process"/>: no shell, arguments through ArgumentList,
/// stdin closed immediately, stdout/stderr captured with a size cap, and the whole process tree killed on
/// timeout or cancellation.
/// </summary>
public sealed class ExternalToolRunner(ILogger<ExternalToolRunner> logger) : IExternalToolRunner
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    // How long to wait for a killed process (and its output pipes) before giving up on it.
    private static readonly TimeSpan KillGracePeriod = TimeSpan.FromSeconds(5);

    public async Task<ExternalToolResult> RunAsync(ExternalToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Timeout, TimeSpan.Zero, nameof(request));
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxOutputChars, 0, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        var toolName = Path.GetFileName(request.FileName);
        using var process = new Process { StartInfo = CreateStartInfo(request) };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            logger.LogWarning("External tool {Tool} was not found", toolName);
            throw new ExternalToolNotFoundException(toolName, ex);
        }

        if (request.LogArguments)
        {
            logger.LogDebug("Started {Tool} (pid {Pid}) with arguments {Arguments}", toolName, process.Id, request.Arguments);
        }
        else
        {
            logger.LogDebug("Started {Tool} (pid {Pid}) with {ArgumentCount} arguments", toolName, process.Id, request.Arguments.Count);
        }

        // Tools must never wait for input.
        process.StandardInput.Close();

        var stdout = new CappedTextCollector(request.MaxOutputChars);
        var stderr = new CappedTextCollector(request.MaxOutputChars);
        var readStdout = PumpAsync(process.StandardOutput, stdout);
        var readStderr = PumpAsync(process.StandardError, stderr);

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

            // A grandchild that inherited the pipes can keep them open after the tool exits.
            await Task.WhenAll(readStdout, readStderr).WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            await KillTreeAsync(process, toolName, readStdout, readStderr).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("{Tool} was cancelled after {Elapsed} ms", toolName, stopwatch.ElapsedMilliseconds);
                throw new OperationCanceledException($"External tool '{toolName}' was cancelled.", cancellationToken);
            }

            var stderrTail = ExternalToolResult.Tail(stderr.ToString());
            logger.LogWarning("{Tool} timed out after {Timeout}; stderr: {StdErr}", toolName, request.Timeout, stderrTail);
            throw new ExternalToolTimeoutException(toolName, request.Timeout, stderrTail);
        }

        stopwatch.Stop();
        var result = new ExternalToolResult(
            toolName,
            process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            stopwatch.Elapsed,
            stdout.Truncated || stderr.Truncated);

        if (result.Succeeded)
        {
            logger.LogDebug("{Tool} finished in {Elapsed} ms", toolName, stopwatch.ElapsedMilliseconds);
        }
        else
        {
            logger.LogWarning(
                "{Tool} exited with code {ExitCode} after {Elapsed} ms; stderr: {StdErr}",
                toolName,
                result.ExitCode,
                stopwatch.ElapsedMilliseconds,
                ExternalToolResult.Tail(result.StandardError));
        }

        return result;
    }

    private static ProcessStartInfo CreateStartInfo(ExternalToolRequest request)
    {
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = request.OutputEncoding,
            StandardErrorEncoding = request.OutputEncoding,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in request.EnvironmentVariables)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    private static async Task PumpAsync(StreamReader reader, CappedTextCollector collector)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            collector.Append(buffer.AsSpan(0, read));
        }
    }

    private async Task KillTreeAsync(Process process, string toolName, Task readStdout, Task readStderr)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "Could not kill the process tree of {Tool}", toolName);
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(KillGracePeriod).ConfigureAwait(false);
            await Task.WhenAll(readStdout, readStderr).WaitAsync(KillGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("{Tool} or one of its child processes did not exit after being killed", toolName);
        }
    }
}
