using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;

namespace PdfPro.Infrastructure.Worker;

/// <summary>Where the worker is and what it may use.</summary>
/// <param name="Program">PdfPro.Worker.exe (or PdfPro.Worker.dll, run with the dotnet that runs this process).</param>
/// <param name="MemoryLimitBytes">Memory one run may use (Windows job object; 0 = no limit).</param>
/// <param name="RunTimeout">Longest a run may take before the worker is stopped.</param>
public sealed record WorkerOptions(string Program, long MemoryLimitBytes, TimeSpan RunTimeout)
{
    /// <summary>How long the worker has to start and connect.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a cancelled run has to stop before the worker is stopped.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The app's data folder (its settings: where Tesseract is…), or null for the default one.</summary>
    public string? DataRoot { get; init; }
}

/// <summary>One run of an operation in the worker.</summary>
/// <param name="Operation">The operation's id.</param>
/// <param name="Parameters">Its parameters as JSON (the operation's own serialization).</param>
/// <param name="Input">The file it reads.</param>
/// <param name="Output">The file a content operation writes.</param>
public sealed record WorkerRun(string Operation, string Parameters, string Input, string Output)
{
    /// <summary>An export (compress, convert…): its result comes back as the report.</summary>
    public bool Export { get; init; }

    /// <summary>The files an export reads, when it reads several.</summary>
    public IReadOnlyList<string>? Inputs { get; init; }

    /// <summary>The password of an export's protected input.</summary>
    public string? Password { get; init; }

    /// <summary>What an export names its files after.</summary>
    public string? Name { get; init; }
}

/// <summary>Runs operations in the worker process (seam for tests).</summary>
public interface IWorkerClient : IAsyncDisposable
{
    /// <summary>Whether the worker's program is there (a build without it runs everything in the app).</summary>
    bool IsAvailable { get; }

    /// <summary>Runs one operation in the worker; its report as JSON, or null. One run at a time.</summary>
    /// <exception cref="WorkerException">The worker could not start, crashed or went over its time limit; it is started again for the next run.</exception>
    /// <exception cref="OperationValidationException">The parameters do not fit.</exception>
    /// <exception cref="OcrException">OCR failed.</exception>
    /// <exception cref="WorkerOperationException">The operation failed otherwise.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    Task<string?> RunAsync(WorkerRun run, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// The app's side of PdfPro.Worker (SPEC Phase 13): the worker process is started on the first run, on a named pipe only
/// the current user can open, inside a job object limiting its memory (Windows); runs go one at a time. When the worker
/// dies — a file crashed a native library, the memory limit was reached — the run fails with
/// <see cref="WorkerException"/> and the next run starts a new worker. Logs name the operation and the outcome only.
/// </summary>
public sealed class WorkerClient(WorkerOptions options, ILogger<WorkerClient> logger) : IWorkerClient
{
    private readonly SemaphoreSlim _oneRun = new(1, 1);
    private Connection? _connection;
    private int _nextId;
    private bool _disposed;

    public bool IsAvailable => File.Exists(options.Program);

    /// <summary>The worker's process id, or null when none runs (for tests).</summary>
    internal int? ProcessId => _connection is { IsAlive: true } connection ? connection.Process.Id : null;

    public async Task<string?> RunAsync(WorkerRun run, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(progress);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _oneRun.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            var id = Interlocked.Increment(ref _nextId);
            var result = connection.Expect(id, progress);
            var clock = Stopwatch.StartNew();
            try
            {
                var message = new WorkerMessage(WorkerMessageKind.Run, id)
                {
                    Operation = run.Operation,
                    Parameters = run.Parameters,
                    Input = run.Input,
                    Output = run.Output,
                    Export = run.Export,
                    Inputs = run.Inputs,
                    Password = run.Password,
                    Name = run.Name,
                };
                await connection.SendAsync(message, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                await StopAsync(connection).ConfigureAwait(false);
                throw new WorkerException(WorkerFailure.Crashed, ex);
            }

            var reply = await WaitAsync(connection, id, result, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Worker ran {OperationId}: {Outcome} in {Elapsed} ms", run.Operation, reply.Outcome, clock.ElapsedMilliseconds);
            return reply.Outcome switch
            {
                WorkerOutcome.Succeeded => reply.Report,
                WorkerOutcome.Cancelled => throw new OperationCanceledException(cancellationToken),
                _ => throw Rebuild(reply, run.Operation),
            };
        }
        finally
        {
            _oneRun.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_connection is { } connection)
        {
            await StopAsync(connection).ConfigureAwait(false);
        }

        _oneRun.Dispose();
    }

    /// <summary>The run's reply; the worker is stopped when it dies, overruns or ignores a cancel.</summary>
    private async Task<WorkerMessage> WaitAsync(Connection connection, int id, Task<WorkerMessage> result, CancellationToken cancellationToken)
    {
        using var overrun = new CancellationTokenSource(options.RunTimeout);
        var cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
        var timedOut = Task.Delay(Timeout.Infinite, overrun.Token);
        Observe(result);
        var first = await Task.WhenAny(result, cancelled, timedOut).ConfigureAwait(false);
        if (first == cancelled)
        {
            try
            {
                await connection.SendAsync(new WorkerMessage(WorkerMessageKind.Cancel, id), CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Gone already: the result says so.
            }

            if (await Task.WhenAny(result, Task.Delay(options.CancelGrace, CancellationToken.None)).ConfigureAwait(false) != result)
            {
                logger.LogWarning("Worker did not stop a cancelled run; stopping it");
                await StopAsync(connection).ConfigureAwait(false);
                throw new OperationCanceledException(cancellationToken);
            }
        }
        else if (first == timedOut)
        {
            logger.LogWarning("Worker went over the time limit of a run; stopping it");
            await StopAsync(connection).ConfigureAwait(false);
            throw new WorkerException(WorkerFailure.TimedOut);
        }

        try
        {
            return await result.ConfigureAwait(false);
        }
        catch (WorkerException)
        {
            logger.LogWarning("Worker stopped during a run (exit code {ExitCode})", connection.ExitCode);
            await StopAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A reply nobody waits for any more (the worker was stopped) does not go unobserved.</summary>
    private static void Observe(Task<WorkerMessage> result)
        => _ = result.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private async Task<Connection> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsAlive: true } alive)
        {
            return alive;
        }

        if (_connection is { } dead)
        {
            await StopAsync(dead).ConfigureAwait(false);
        }

        if (!File.Exists(options.Program))
        {
            throw new WorkerException(WorkerFailure.NotStarted, new FileNotFoundException("The worker program is missing."));
        }

        var name = $"pdfpro-worker-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var job = JobObject.Create(options.MemoryLimitBytes);
        Process? process = null;
        try
        {
            process = Start(name);
            LowerPriority(process);
            if (job is not null && !job.Add(process))
            {
                logger.LogWarning("Worker runs without its memory limit (the job object refused it)");
            }

            using var waited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waited.CancelAfter(options.StartTimeout);
            var exited = process.WaitForExitAsync(waited.Token);
            var connected = pipe.WaitForConnectionAsync(waited.Token);
            try
            {
                await Task.WhenAny(connected, exited).ConfigureAwait(false);
                await connected.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WorkerException(WorkerFailure.NotStarted);
            }

            var connection = new Connection(pipe, process, job);
            connection.StartReading();
            _connection = connection;
            logger.LogInformation("Worker started (process {ProcessId})", process.Id);
            return connection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not WorkerException)
        {
            Kill(process);
            await pipe.DisposeAsync().ConfigureAwait(false);
            job?.Dispose();
            throw new WorkerException(WorkerFailure.NotStarted, ex);
        }
        catch
        {
            Kill(process);
            await pipe.DisposeAsync().ConfigureAwait(false);
            job?.Dispose();
            throw;
        }
    }

    private Process Start(string pipeName)
    {
        var program = options.Program;
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(program)!,
        };
        var host = Path.ChangeExtension(program, OperatingSystem.IsWindows() ? ".exe" : null);
        if (!program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = program;
        }
        else if (File.Exists(host))
        {
            start.FileName = host; // The worker's own launcher, next to it.
        }
        else
        {
            // This process may itself be a launcher (an app host), not dotnet: ask for the dotnet that runs .NET.
            start.FileName = Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? self : "dotnet";
            start.ArgumentList.Add(program);
        }

        start.ArgumentList.Add(pipeName);
        if (options.DataRoot is { Length: > 0 } dataRoot)
        {
            start.ArgumentList.Add(dataRoot);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The worker did not start.");
    }

    /// <summary>
    /// Heavy work runs below normal priority, so the app's window (and anything else the user does) keeps the
    /// processor first: OCR of a long document must not make scrolling stutter.
    /// </summary>
    private void LowerPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            logger.LogDebug("Worker priority left as it is: {ErrorType}", ex.GetType().Name);
        }
    }

    private async Task StopAsync(Connection connection)
    {
        if (ReferenceEquals(_connection, connection))
        {
            _connection = null;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private static void Kill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone.
        }

        process.Dispose();
    }

    /// <summary>The operation's error as the type the app knows it by.</summary>
    private static Exception Rebuild(WorkerMessage reply, string operation)
    {
        var type = reply.ErrorType ?? string.Empty;
        var message = reply.ErrorMessage ?? "The operation failed.";
        try
        {
            if (type == typeof(OperationValidationException).FullName && reply.ErrorDetail is { } errors)
            {
                return new OperationValidationException(operation, JsonSerializer.Deserialize<List<OperationValidationError>>(errors) ?? []);
            }

            if (type == typeof(OcrException).FullName && reply.ErrorDetail?.Split('\n', 2) is [var kind, .. var detail] && Enum.TryParse<OcrFailure>(kind, out var failure))
            {
                return new OcrException(failure, detail.FirstOrDefault());
            }
        }
        catch (JsonException)
        {
            // Told as it is below.
        }

        if (type == typeof(ExternalToolNotFoundException).FullName)
        {
            return new ExternalToolNotFoundException(reply.ErrorDetail ?? "qpdf");
        }

        return type switch
        {
            "System.IO.InvalidDataException" => new InvalidDataException(message),
            "System.IO.FileNotFoundException" => new FileNotFoundException(message),
            "System.UnauthorizedAccessException" => new UnauthorizedAccessException(message),
            "System.OutOfMemoryException" => new WorkerException(WorkerFailure.Crashed, new InsufficientMemoryException(message)),
            _ => new WorkerOperationException(type, message, reply.ErrorDetail),
        };
    }

    /// <summary>A running worker: its process, its pipe and the reader of its messages.</summary>
    private sealed class Connection(NamedPipeServerStream pipe, Process process, JobObject? job) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sending = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private (int Id, IProgress<OperationProgress> Progress, TaskCompletionSource<WorkerMessage> Result)? _pending;
        private Task? _reading;

        public Process Process { get; } = process;

        public bool IsAlive => !_stop.IsCancellationRequested && pipe.IsConnected && !HasExited;

        public int? ExitCode => HasExited ? Process.ExitCode : null;

        private bool HasExited
        {
            get
            {
                try
                {
                    return Process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public Task<WorkerMessage> Expect(int id, IProgress<OperationProgress> progress)
        {
            var result = new TaskCompletionSource<WorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = (id, progress, result);
            return result.Task;
        }

        public async Task SendAsync(WorkerMessage message, CancellationToken cancellationToken)
        {
            await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WorkerFraming.WriteAsync(pipe, message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sending.Release();
            }
        }

        public void StartReading() => _reading = Task.Run(ReadAsync);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            Kill(Process);
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (_reading is { } reading)
            {
                await reading.ConfigureAwait(false);
            }

            job?.Dispose();
            Fail(new WorkerException(WorkerFailure.Crashed));
            _sending.Dispose();
            _stop.Dispose();
        }

        private async Task ReadAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var message = await WorkerFraming.ReadAsync(pipe, _stop.Token).ConfigureAwait(false);
                    if (message is null)
                    {
                        break;
                    }

                    if (_pending is not { } pending || pending.Id != message.Id)
                    {
                        continue;
                    }

                    if (message.Kind == WorkerMessageKind.Progress)
                    {
                        pending.Progress.Report(new OperationProgress(message.Done, message.Total, message.Stage));
                    }
                    else if (message.Kind == WorkerMessageKind.Result)
                    {
                        _pending = null;
                        pending.Result.TrySetResult(message);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
            {
                // The worker is gone (or is being stopped).
            }

            Fail(new WorkerException(WorkerFailure.Crashed));
        }

        private void Fail(Exception error)
        {
            if (_pending is { } pending)
            {
                _pending = null;
                pending.Result.TrySetException(error);
            }
        }
    }
}
