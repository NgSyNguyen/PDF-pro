using System.Windows;
using System.Windows.Threading;

namespace PdfPro.App.Services;

/// <summary>Marshals work to the UI thread (seam for view-model tests, which run without a dispatcher).</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);
}

/// <summary>Runs an action once after a quiet period; scheduling again restarts the wait.</summary>
public interface IDebouncer : IDisposable
{
    void Schedule(TimeSpan delay, Action action);

    void Cancel();
}

public interface IDebouncerFactory
{
    IDebouncer Create();
}

internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private static Dispatcher Dispatcher => Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    public bool CheckAccess() => Dispatcher.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = Dispatcher;
        if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
    }
}

internal sealed class DispatcherDebouncerFactory : IDebouncerFactory
{
    public IDebouncer Create() => new DispatcherDebouncer();

    private sealed class DispatcherDebouncer : IDebouncer
    {
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
        private Action? _action;

        public DispatcherDebouncer() => _timer.Tick += OnTick;

        public void Schedule(TimeSpan delay, Action action)
        {
            _action = action;
            _timer.Stop();
            _timer.Interval = delay;
            _timer.Start();
        }

        public void Cancel()
        {
            _timer.Stop();
            _action = null;
        }

        public void Dispose()
        {
            Cancel();
            _timer.Tick -= OnTick;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _timer.Stop();
            var action = _action;
            _action = null;
            action?.Invoke();
        }
    }
}
