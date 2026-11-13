using System.IO;
using PdfPro.Core.Text;

namespace PdfPro.App.Services;

/// <summary>
/// "Gộp bằng PDF_Pro" in Explorer's right-click menu starts PdfPro.exe once per selected file: the files reach the
/// running window one launch at a time. They are gathered until none has come for <see cref="Quiet"/>, then merged
/// together — each once, in natural order of name ("trang 2" before "trang 10"), as Explorer starts them in no
/// particular order. Must be used on the UI thread.
/// </summary>
public sealed class MergeRequestCollector : IDisposable
{
    /// <summary>How long after the last file the gathered files are merged.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(800);

    private readonly IDebouncer _debouncer;
    private readonly Func<IReadOnlyList<string>, Task> _merge;
    private readonly List<string> _files = [];

    /// <param name="debouncers">Waits for the quiet moment.</param>
    /// <param name="merge">Merges the gathered files (the merge dialog with them).</param>
    public MergeRequestCollector(IDebouncerFactory debouncers, Func<IReadOnlyList<string>, Task> merge)
    {
        ArgumentNullException.ThrowIfNull(debouncers);
        ArgumentNullException.ThrowIfNull(merge);
        _debouncer = debouncers.Create();
        _merge = merge;
    }

    /// <summary>Files of one launch: kept, and the merge waits for more.</summary>
    public void Add(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (var file in files.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            if (!_files.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                _files.Add(file);
            }
        }

        if (_files.Count > 0)
        {
            _debouncer.Schedule(Quiet, Flush);
        }
    }

    public void Dispose() => _debouncer.Dispose();

    private async void Flush()
    {
        var files = _files
            .OrderBy(f => Path.GetFileName(f), NaturalComparer.Instance)
            .ThenBy(f => f, NaturalComparer.Instance)
            .ToList();
        _files.Clear();
        if (files.Count > 0)
        {
            await _merge(files);
        }
    }
}
