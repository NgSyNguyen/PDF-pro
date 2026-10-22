using PdfPro.Core.Operations;
using PdfPro.Core.Splitting;
using PdfPro.Core.Text;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Splitting;

/// <summary>
/// What the split modes need to know about a document, read once and kept while the user tries settings: the dark
/// share of every page (blank pages), the text of every page, the bookmarks and the page sizes. Not thread-safe:
/// prepare it from one caller at a time.
/// </summary>
public sealed class SplitAnalysis(SplitDocument document)
{
    private const int MaxBookmarkDepth = 64;

    public SplitDocument Document { get; } = document ?? throw new ArgumentNullException(nameof(document));

    /// <summary>Share of dark pixels (%) of every page, once measured.</summary>
    public IReadOnlyList<double>? DarkPercents { get; private set; }

    /// <summary>Text (NFC) of every page, once read.</summary>
    public IReadOnlyList<string>? Texts { get; private set; }

    /// <summary>The base source's outline, flattened in document order, with positions in the plan; once read.</summary>
    public IReadOnlyList<SplitBookmark>? Bookmarks { get; private set; }

    public SizeEstimate? Sizes { get; private set; }

    /// <summary>Deepest bookmark level that points into the document (0 without bookmarks).</summary>
    public int MaxBookmarkLevel => Bookmarks?.Where(b => b.Page >= 0).Select(b => b.Level).DefaultIfEmpty(0).Max() ?? 0;

    /// <summary>False when no page has any text (a scan without OCR).</summary>
    public bool HasText => Texts?.Any(t => !string.IsNullOrWhiteSpace(t)) ?? false;

    /// <summary>Reads what <paramref name="parameters"/> need and has not been read yet.</summary>
    public async Task PrepareAsync(SplitParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(progress);
        if (parameters.NeedsInk && DarkPercents is null)
        {
            DarkPercents = await BlankPageDetector.MeasureAsync(Document, progress, cancellationToken).ConfigureAwait(false);
        }

        switch (parameters.Mode)
        {
            case SplitMode.Text when Texts is null:
                Texts = await PageTextMatcher.ReadAsync(Document, progress, cancellationToken).ConfigureAwait(false);
                break;
            case SplitMode.Bookmarks when Bookmarks is null:
                var outline = await Document.Documents[Document.BaseSourceId].GetBookmarksAsync(cancellationToken).ConfigureAwait(false);
                var flat = new List<SplitBookmark>();
                Flatten(outline, 1, flat);
                Bookmarks = flat;
                break;
            case SplitMode.Size when Sizes is null:
                Sizes = await PageSizeEstimator.EstimateAsync(Document, progress, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private void Flatten(IReadOnlyList<PdfBookmark> items, int level, List<SplitBookmark> flat)
    {
        if (level > MaxBookmarkDepth)
        {
            return;
        }

        foreach (var item in items)
        {
            var page = item.Destination is { } destination ? Document.Plan.FirstIndexOf(Document.BaseSourceId, destination.PageIndex) : -1;
            flat.Add(new SplitBookmark(page, level, TextNormalizer.ToNfc(item.Title).Trim()));
            Flatten(item.Children, level + 1, flat);
        }
    }
}
