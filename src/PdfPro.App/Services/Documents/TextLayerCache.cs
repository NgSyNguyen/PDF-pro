using PdfPro.Core.Text;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Documents;

/// <summary>
/// The text layers (characters with their boxes) of a document's pages, read through PDFium once and kept for the most
/// recently used pages — what selecting, copying and searching read. Keyed by the opened source document and page, so a
/// new revision is a new entry. A failed read is not kept (the next request tries again).
/// </summary>
public sealed class TextLayerCache(int capacity = 256)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid Document, int Page), LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recent = [];

    /// <summary>The layer when it has been read already; null otherwise (reading it is up to the caller).</summary>
    public PageText? TryGet(VirtualPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Source is not { } source)
        {
            return PageText.Empty;
        }

        lock (_lock)
        {
            return _entries.TryGetValue((source.Id, page.SourcePageIndex), out var node) && node.Value.Task.IsCompletedSuccessfully
                ? Touch(node).Task.Result
                : null;
        }
    }

    /// <summary>The layer of a page as displayed (a blank page has none); read at <paramref name="priority"/> when not kept.</summary>
    /// <exception cref="PdfDocumentException">The page cannot be read.</exception>
    public Task<PageText> GetAsync(VirtualPage page, PdfiumPriority priority = PdfiumPriority.Normal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Source is not { } source)
        {
            return Task.FromResult(PageText.Empty);
        }

        Task<PageText> task;
        lock (_lock)
        {
            var key = (source.Id, page.SourcePageIndex);
            if (_entries.TryGetValue(key, out var node) && !node.Value.Task.IsFaulted && !node.Value.Task.IsCanceled)
            {
                task = Touch(node).Task;
            }
            else
            {
                if (node is not null)
                {
                    _recent.Remove(node);
                }

                // Other requests may share the read: it is not cancelled with this caller.
                task = source.GetPageTextLayerAsync(page.SourcePageIndex, priority, CancellationToken.None);
                _entries[key] = _recent.AddFirst(new Entry(key, task));
                while (_entries.Count > capacity && _recent.Last is { } oldest)
                {
                    _recent.RemoveLast();
                    _entries.Remove(oldest.Value.Key);
                }
            }
        }

        return task.WaitAsync(cancellationToken);
    }

    private Entry Touch(LinkedListNode<Entry> node)
    {
        _recent.Remove(node);
        _recent.AddFirst(node);
        return node.Value;
    }

    private sealed record Entry((Guid Document, int Page) Key, Task<PageText> Task);
}
