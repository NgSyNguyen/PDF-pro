using System.IO;
using System.Printing;
using System.Windows.Documents;
using System.Windows.Documents.Serialization;
using System.Windows.Threading;
using System.Windows.Xps;

namespace PdfPro.App.Services.Printing;

/// <summary>What the print thread writes to: a printer queue in the app, an XPS file in tests.</summary>
/// <param name="Resources">Released on the print thread when the job ends (queue, print server, XPS package).</param>
internal sealed record XpsTarget(XpsDocumentWriter Writer, PrintTicket? Ticket, IDisposable? Resources);

/// <summary>
/// Runs <see cref="XpsDocumentWriter.WriteAsync(DocumentPaginator, PrintTicket)"/> on a dedicated STA thread with
/// its own dispatcher: page rendering and spooling never block the UI thread, progress is reported per page and
/// cancellation goes through <see cref="XpsDocumentWriter.CancelAsync"/> so the spooler drops the job cleanly.
/// </summary>
internal static class XpsPrintRunner
{
    /// <param name="openTarget">Called on the print thread: print queues belong to the thread that opened them.</param>
    /// <param name="createPaginator">Called on the print thread with the job's cancellation token.</param>
    /// <param name="progress">Pages written so far.</param>
    /// <returns>True when every page was written, false when cancelled.</returns>
    public static Task<bool> RunAsync(
        Func<XpsTarget> openTarget,
        Func<CancellationToken, DocumentPaginator> createPaginator,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openTarget);
        ArgumentNullException.ThrowIfNull(createPaginator);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Run(openTarget, createPaginator, progress, cancellationToken, completion))
        {
            IsBackground = true,
            Name = "PdfPro print",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Run(
        Func<XpsTarget> openTarget,
        Func<CancellationToken, DocumentPaginator> createPaginator,
        IProgress<int>? progress,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool> completion)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        Exception? error = null;
        bool? cancelled = null;

        // The first outcome wins; the dispatcher loop then ends and the result is published after clean-up.
        void Finish(Exception? failure, bool wasCancelled)
        {
            if (cancelled is null)
            {
                error = failure;
                cancelled = wasCancelled || failure is OperationCanceledException;
            }

            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }

        dispatcher.UnhandledException += (_, e) =>
        {
            // Anything thrown while the writer works ends the job instead of the process.
            e.Handled = true;
            Finish(e.Exception, wasCancelled: false);
        };

        XpsTarget? target = null;
        var registration = default(CancellationTokenRegistration);
        try
        {
            target = openTarget();
            var writer = target.Writer;
            writer.WritingProgressChanged += (_, e) =>
            {
                if (e.WritingLevel == WritingProgressChangeLevel.FixedPageWritingProgress)
                {
                    progress?.Report(e.Number);
                }
            };
            writer.WritingCompleted += (_, e) => Finish(e.Error, e.Cancelled);
            writer.WritingCancelled += (_, e) => Finish(e.Error, wasCancelled: true);

            var paginator = createPaginator(cancellationToken);
            registration = cancellationToken.Register(() => dispatcher.BeginInvoke(writer.CancelAsync));
            if (target.Ticket is { } ticket)
            {
                writer.WriteAsync(paginator, ticket);
            }
            else
            {
                writer.WriteAsync(paginator);
            }

            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            Finish(ex, wasCancelled: false);
        }
        finally
        {
            registration.Dispose();
            try
            {
                // An XPS package or a print job is only complete once closed: release before reporting.
                target?.Resources?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                if (error is null && cancelled is not true)
                {
                    error = ex;
                }
            }

            if (error is not null and not OperationCanceledException)
            {
                completion.TrySetException(error);
            }
            else if (cancelled is { } wasCancelled)
            {
                completion.TrySetResult(!wasCancelled);
            }
            else
            {
                completion.TrySetException(new InvalidOperationException("The print thread ended without a result."));
            }
        }
    }
}
