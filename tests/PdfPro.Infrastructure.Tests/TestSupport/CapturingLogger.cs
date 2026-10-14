using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PdfPro.Infrastructure.Tests.TestSupport;

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
