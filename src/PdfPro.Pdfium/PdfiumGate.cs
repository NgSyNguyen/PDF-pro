using Microsoft.Extensions.Logging;
using PDFiumCore;

namespace PdfPro.Pdfium;

/// <summary>Scheduling priority on the PDFium thread. Lower values run first; equal values run in FIFO order.</summary>
public enum PdfiumPriority
{
    /// <summary>Reserved for releasing native handles.</summary>
    Release = 0,

    /// <summary>Work the user is waiting for: opening documents, rendering visible pages.</summary>
    High = 1,

    Normal = 2,

    /// <summary>Background work such as thumbnails.</summary>
    Low = 3,
}

/// <summary>
/// PDFium is not thread-safe: every call into it runs on this single dedicated thread. The gate owns the library
/// lifetime (initialised in the constructor, destroyed in <see cref="Dispose"/>) and only one gate may own the
/// library per process.
/// </summary>
public sealed class PdfiumGate : IDisposable
{
    private static int _libraryOwners;

    private readonly object _sync = new();
    private readonly PriorityQueue<IWorkItem, (int Priority, long Sequence)> _queue = new();
    private readonly ILogger<PdfiumGate> _logger;
    private readonly bool _ownsLibrary;
    private readonly Thread _thread;
    private long _sequence;
    private bool _stopping;
    private bool _acceptingReleases = true;
    private int _disposed;

    /// <summary>Starts the PDFium thread and initialises the library on it.</summary>
    /// <exception cref="InvalidOperationException">Another gate already owns the library.</exception>
    public PdfiumGate(ILogger<PdfiumGate> logger)
        : this(logger, initializeLibrary: true)
    {
    }

    /// <summary>Test seam: a gate that never touches PDFium, used to test scheduling in isolation.</summary>
    internal PdfiumGate(ILogger<PdfiumGate> logger, bool initializeLibrary)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _ownsLibrary = initializeLibrary;

        if (initializeLibrary && Interlocked.CompareExchange(ref _libraryOwners, 1, 0) != 0)
        {
            throw new InvalidOperationException("Only one PdfiumGate may initialise PDFium per process.");
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => ThreadMain(started)) { IsBackground = true, Name = "PdfiumGate" };
        _thread.Start();

        try
        {
            started.Task.GetAwaiter().GetResult();
        }
        catch
        {
            if (initializeLibrary)
            {
                Interlocked.Exchange(ref _libraryOwners, 0);
            }

            throw;
        }
    }

    public bool IsOnGateThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>Throws unless called on the PDFium thread (i.e. from inside <see cref="RunAsync{T}"/>).</summary>
    public void VerifyAccess()
    {
        if (!IsOnGateThread)
        {
            throw new InvalidOperationException("PDFium objects can only be used on the PdfiumGate thread; use PdfiumGate.RunAsync.");
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the PDFium thread. Cancellation only prevents work that has not started;
    /// once it runs, its result is always delivered (so the caller can dispose what it created).
    /// Called from the PDFium thread itself, the work runs inline.
    /// </summary>
    public Task<T> RunAsync<T>(Func<T> work, PdfiumPriority priority = PdfiumPriority.Normal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (priority == PdfiumPriority.Release)
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "The Release priority is reserved for handle disposal.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        if (IsOnGateThread)
        {
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var item = new WorkItem<T>(work, cancellationToken);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _queue.Enqueue(item, ((int)priority, _sequence++));
            Monitor.Pulse(_sync);
        }

        item.RegisterCancellation();
        return item.Task;
    }

    /// <inheritdoc cref="RunAsync{T}(Func{T}, PdfiumPriority, CancellationToken)"/>
    public Task RunAsync(Action work, PdfiumPriority priority = PdfiumPriority.Normal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(
            () =>
            {
                work();
                return true;
            },
            priority,
            cancellationToken);
    }

    /// <summary>
    /// Schedules a native release ahead of normal work. Runs inline on the PDFium thread. After the library has been
    /// destroyed releases are dropped: nothing can safely be freed any more and the process is shutting down.
    /// Never throws (called from SafeHandle.ReleaseHandle, possibly on the finalizer thread).
    /// </summary>
    internal void Post(Action release)
    {
        if (IsOnGateThread)
        {
            ReleaseItem.Run(release, _logger);
            return;
        }

        lock (_sync)
        {
            if (!_acceptingReleases)
            {
                return;
            }

            _queue.Enqueue(new ReleaseItem(release, _logger), ((int)PdfiumPriority.Release, _sequence++));
            Monitor.Pulse(_sync);
        }
    }

    /// <summary>Cancels queued work, runs pending releases, destroys the library and stops the thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<IWorkItem> cancelled = [];
        lock (_sync)
        {
            _stopping = true;
            List<(IWorkItem Item, (int, long) Priority)> releases = [];
            while (_queue.TryDequeue(out var item, out var priority))
            {
                if (item is ReleaseItem)
                {
                    releases.Add((item, priority));
                }
                else
                {
                    cancelled.Add(item);
                }
            }

            foreach (var (item, priority) in releases)
            {
                _queue.Enqueue(item, priority);
            }

            Monitor.PulseAll(_sync);
        }

        foreach (var item in cancelled)
        {
            item.Cancel();
        }

        if (!IsOnGateThread && !_thread.Join(TimeSpan.FromSeconds(10)))
        {
            _logger.LogWarning("The PDFium thread did not stop within 10 seconds");
        }

        if (_ownsLibrary)
        {
            Interlocked.Exchange(ref _libraryOwners, 0);
        }
    }

    private void ThreadMain(TaskCompletionSource started)
    {
        try
        {
            if (_ownsLibrary)
            {
                fpdfview.FPDF_InitLibrary();
                _logger.LogInformation("PDFium initialised");
            }

            started.SetResult();
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _acceptingReleases = false;
            }

            started.SetException(ex);
            return;
        }

        while (true)
        {
            IWorkItem item;
            lock (_sync)
            {
                while (_queue.Count == 0 && !_stopping)
                {
                    Monitor.Wait(_sync);
                }

                if (_queue.Count == 0)
                {
                    _acceptingReleases = false;
                    break;
                }

                item = _queue.Dequeue();
            }

            item.Execute();
        }

        if (_ownsLibrary)
        {
            fpdfview.FPDF_DestroyLibrary();
            _logger.LogInformation("PDFium destroyed");
        }
    }

    private interface IWorkItem
    {
        void Execute();

        void Cancel();
    }

    private sealed class WorkItem<T>(Func<T> work, CancellationToken cancellationToken) : IWorkItem
    {
        private const int Queued = 0;
        private const int Running = 1;
        private const int Finished = 2;

        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private int _state;

        public Task<T> Task => _completion.Task;

        public void RegisterCancellation()
        {
            if (cancellationToken.CanBeCanceled)
            {
                _registration = cancellationToken.Register(static state => ((WorkItem<T>)state!).Cancel(), this);
            }
        }

        public void Execute()
        {
            _registration.Dispose();
            if (Interlocked.CompareExchange(ref _state, Running, Queued) != Queued)
            {
                return;
            }

            try
            {
                _completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
            finally
            {
                Volatile.Write(ref _state, Finished);
            }
        }

        public void Cancel()
        {
            // Only work that has not started can be cancelled.
            if (Interlocked.CompareExchange(ref _state, Finished, Queued) == Queued)
            {
                _completion.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : CancellationToken.None);
            }
        }
    }

    private sealed class ReleaseItem(Action release, ILogger logger) : IWorkItem
    {
        public static void Run(Action release, ILogger logger)
        {
            try
            {
                release();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Releasing a PDFium handle failed");
            }
        }

        public void Execute() => Run(release, logger);

        public void Cancel()
        {
            // Releases are never cancelled; Dispose keeps them in the queue.
        }
    }
}
