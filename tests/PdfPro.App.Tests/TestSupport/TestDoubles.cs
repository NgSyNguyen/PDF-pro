using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.Tests.TestSupport;

/// <summary>Logger that records formatted messages so tests can assert on what gets logged.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue((logLevel, formatter(state, exception), exception));
}

/// <summary>In-memory settings store; can be told to fail the next write.</summary>
internal sealed class FakeSettingsService(AppSettings initial) : ISettingsService
{
    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current { get; private set; } = initial;

    public int UpdateCount { get; private set; }

    public Exception? NextWriteError { get; set; }

    public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default)
    {
        UpdateCount++;
        Current = update(Current);
        Changed?.Invoke(this, Current);

        if (NextWriteError is { } error)
        {
            NextWriteError = null;
            return Task.FromException(error);
        }

        return Task.CompletedTask;
    }
}
