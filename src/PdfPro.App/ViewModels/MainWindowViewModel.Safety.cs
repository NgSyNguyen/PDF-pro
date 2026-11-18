using System.Collections.Specialized;
using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure.Instances;
using PdfPro.Operations.Robustness;

namespace PdfPro.App.ViewModels;

/// <summary>
/// "Chế độ an toàn" (SPEC Phase 13): the files open when the app last stopped without closing them are looked at in the
/// worker process before they are opened again — a file that crashes PDFium would otherwise bring the window down at
/// every attempt.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly FileProbe Probe = new(gate: null);

    private readonly Dictionary<DocumentTabViewModel, string> _guarded = [];

    /// <summary>Records the documents as they open and close (the record a crash leaves behind).</summary>
    private void GuardOpenDocuments(IOpenDocumentGuard guard)
    {
        Tabs.CollectionChanged += (_, e) =>
        {
            foreach (var removed in e.OldItems?.OfType<DocumentTabViewModel>() ?? [])
            {
                if (_guarded.Remove(removed, out var path))
                {
                    guard.Closed(path);
                }
            }

            foreach (var added in e.NewItems?.OfType<DocumentTabViewModel>() ?? [])
            {
                _guarded[added] = added.FilePath;
                guard.Opened(added.FilePath);
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var (_, path) in _guarded)
                {
                    guard.Closed(path);
                }

                _guarded.Clear();
            }
        };
    }

    /// <summary>
    /// Whether a file open when the app last stopped may be opened: looked at in the worker first (pages drawn, text
    /// read) — when that goes well, or the file is merely damaged or protected, yes; when the worker stops or overruns on
    /// it, or the worker is off, the user decides.
    /// </summary>
    private async Task<bool> MayOpenSuspectAsync(IOpenDocumentGuard guard, string path)
    {
        var name = Path.GetFileName(path);
        var text = Format(Strings.Safety_Probing, name);
        var task = StatusBar.StartTask(text);
        var progress = new Progress<OperationProgress>(p => task.Report(p.Fraction, text));
        string? warning;
        try
        {
            var probed = await Exports.MeasureAsync<FileProbeResult?>(Probe, path, new FileProbeParams(), (_, _) => Task.FromResult<FileProbeResult?>(null), progress, task.Token);
            task.Complete(string.Empty);
            if (probed is not null)
            {
                guard.Cleared(path);
                return true;
            }

            warning = Format(Strings.Safety_NotChecked, name);
        }
        catch (OperationCanceledException)
        {
            task.Complete(Strings.Safety_Cancelled);
            return false;
        }
        catch (WorkerException ex) when (ex.Failure != WorkerFailure.NotStarted)
        {
            _logger.LogWarning("A file open at the last crash stopped the worker too: {Failure}", ex.Failure);
            task.Complete(string.Empty);
            warning = Format(Strings.Safety_Crashed, name);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Damaged or unreadable, not a crash: opening it explains it as usual.
            task.Complete(string.Empty);
            guard.Cleared(path);
            return true;
        }

        if (await _messages.AskAsync(Strings.Safety_Title, warning, Strings.Safety_OpenAnyway, null, Strings.Safety_DontOpen) != DialogChoice.Primary)
        {
            return false;
        }

        guard.Cleared(path);
        return true;
    }
}
