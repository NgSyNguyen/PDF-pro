using Microsoft.Extensions.Logging;

namespace PdfPro.Infrastructure.Threading;

/// <summary>
/// Runs a piece of background work (e.g. writing a session manifest) whenever it is requested, never twice at the
/// same time: requests made while it runs collapse into exactly one more run, so the last state always gets written
/// without queuing one write per change. Failures are logged and do not stop later runs.
/// </summary>
public sealed class CoalescingWorker : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _work;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposal = new();
    private readonly Lock _gate = new();
    private Task _running = Task.CompletedTask;
    private bool _isRunning;
    private bool _pending;
    private bool _disposed;

    public CoalescingWorker(Func<CancellationToken, Task> work, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(logger);
        _work = work;
        _logger = logger;
    }

    /// <summary>Asks for a run; returns at once.</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_isRunning)
            {
                _pending = true;
                return;
            }

            _isRunning = true;
            _running = Task.Run(RunAsync);
        }
    }

    /// <summary>Completes once every run requested so far has finished.</summary>
    public async Task FlushAsync()
    {
        while (true)
        {
            Task running;
            lock (_gate)
            {
                if (!_isRunning)
                {
                    return;
                }

                running = _running;
            }

            await running.ConfigureAwait(false);
        }
    }

    /// <summary>Stops accepting requests and waits for the current run (a pending one still runs once).</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await FlushAsync().ConfigureAwait(false);
        _disposal.Dispose();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            try
            {
                await _work(_disposal.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Background work failed; it will run again on the next request");
            }

            lock (_gate)
            {
                if (!_pending)
                {
                    _isRunning = false;
                    return;
                }

                _pending = false;
            }
        }
    }
}
