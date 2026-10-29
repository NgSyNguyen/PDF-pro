using System.IO.Compression;
using System.Runtime.CompilerServices;
using PdfPro.Core.Operations;
using PdfPro.Operations.Materializing;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Security;

/// <summary>What "Làm sạch" removes; the file is always written anew (old revisions and data after %%EOF go).</summary>
/// <param name="Metadata">Document information (author, dates, programs…) and every XMP metadata stream.</param>
/// <param name="Scripts">JavaScript: document scripts, actions run on opening, on pages, fields and links, XFA forms.</param>
/// <param name="Attachments">Embedded files, file attachment comments, associated files, portfolios.</param>
/// <param name="RiskyActions">Actions that send form data, import data or start programs.</param>
/// <param name="ExternalLinks">Links to web addresses and other files (off by default: they are usually wanted).</param>
/// <param name="HiddenLayers">Layers hidden by default, with what they draw.</param>
/// <param name="OutsideVisibleArea">Objects drawn entirely outside the visible part of a page (its crop box).</param>
/// <param name="Thumbnails">Page thumbnails embedded in the file.</param>
/// <param name="PrivateData">Private data of the programs that made the file (/PieceInfo, /SpiderInfo).</param>
public sealed record SanitizeParams(
    bool Metadata = true,
    bool Scripts = true,
    bool Attachments = true,
    bool RiskyActions = true,
    bool ExternalLinks = false,
    bool HiddenLayers = true,
    bool OutsideVisibleArea = true,
    bool Thumbnails = true,
    bool PrivateData = true);

/// <summary>What "Làm sạch" removed.</summary>
/// <param name="Metadata">Document information entries removed ("Author", "CreationDate"…).</param>
/// <param name="MetadataStreams">XMP metadata streams removed.</param>
/// <param name="Scripts">Scripts and script actions removed.</param>
/// <param name="Attachments">Embedded files, attachment comments and associated files removed.</param>
/// <param name="RiskyActions">Submit, import and launch actions removed.</param>
/// <param name="ExternalLinks">Links to web addresses or other files removed.</param>
/// <param name="HiddenLayers">Hidden layers removed.</param>
/// <param name="HiddenContent">Pieces of content drawn in hidden layers removed (sections, pictures, comments).</param>
/// <param name="OutsideObjects">Objects outside the visible part of pages removed.</param>
/// <param name="Thumbnails">Page thumbnails removed.</param>
/// <param name="PrivateData">Private data dictionaries removed.</param>
/// <param name="OldRevisions">Earlier versions kept in the file by incremental saves, dropped by writing it anew.</param>
/// <param name="TrailingBytes">Bytes after the end of the file (%%EOF), dropped.</param>
/// <param name="PagesLeftAsTheyWere">Pages (1-based) whose hidden or outside content was kept: removing it changed how they look.</param>
public sealed record SanitizeReport(
    IReadOnlyList<string> Metadata,
    int MetadataStreams,
    int Scripts,
    int Attachments,
    int RiskyActions,
    int ExternalLinks,
    int HiddenLayers,
    int HiddenContent,
    int OutsideObjects,
    int Thumbnails,
    int PrivateData,
    int OldRevisions,
    long TrailingBytes,
    IReadOnlyList<int> PagesLeftAsTheyWere)
{
    /// <summary>Nothing was found to remove.</summary>
    public bool IsEmpty => Metadata.Count == 0 && MetadataStreams + Scripts + Attachments + RiskyActions + ExternalLinks + HiddenLayers + HiddenContent
        + OutsideObjects + Thumbnails + PrivateData + OldRevisions == 0 && TrailingBytes == 0;
}

/// <summary>
/// "security.sanitize" — "Làm sạch tài liệu" (SPEC P8 C), a content change: removes what a document carries beyond
/// what it shows — metadata, scripts, attachments, risky actions, optionally external links, hidden layers, objects
/// outside the visible area, thumbnails, private data — and writes the file anew, which drops earlier revisions and
/// anything after %%EOF. Pages whose look would change by removing hidden or outside content are left as they were and
/// reported. <see cref="ReportOf"/> tells what went.
/// </summary>
public sealed class SanitizeOperation(PdfiumGate gate) : FileContentOperation<SanitizeParams>
{
    public const string OperationId = "security.sanitize";

    private static readonly ConditionalWeakTable<SanitizeParams, SanitizeReport> Reports = new();

    public override string Id => OperationId;

    public override string Stage => "Stage_Sanitize";

    /// <summary>The report of the last cleaning made with these very parameters, or null.</summary>
    public static SanitizeReport? ReportOf(SanitizeParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Reports.TryGetValue(parameters, out var report) ? report : null;
    }

    public override async Task ApplyToFileAsync(string inputPath, Stream output, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        if (parameters is not SanitizeParams request)
        {
            throw new ArgumentException($"Expected {nameof(SanitizeParams)}.", nameof(parameters));
        }

        progress.Report(new OperationProgress(0, 3, Stage));
        var (revisions, trailing) = History(await File.ReadAllBytesAsync(inputPath, cancellationToken).ConfigureAwait(false));

        // Hidden and outside content goes only where the page still looks the same; one more pass leaves the others.
        var kept = new HashSet<int>();
        byte[] bytes;
        SanitizeCounts counts;
        while (true)
        {
            var outside = request.OutsideVisibleArea
                ? await gate.RunAsync(() => RemoveOutside(inputPath, kept, cancellationToken), PdfiumPriority.Normal, cancellationToken).ConfigureAwait(false)
                : (Bytes: null, Pages: new Dictionary<int, int>());
            progress.Report(new OperationProgress(1, 3, Stage));
            (bytes, counts) = await Task.Run(() => Clean(inputPath, outside.Bytes, request, kept, cancellationToken), cancellationToken).ConfigureAwait(false);
            counts = counts with { OutsideObjects = outside.Pages.Values.Sum() };
            var touched = outside.Pages.Keys.Concat(counts.LayerPages).Distinct().Where(p => !kept.Contains(p)).ToList();
            var changed = touched.Count == 0
                ? []
                : await gate.RunAsync(() => Changed(inputPath, bytes, touched), PdfiumPriority.Normal, cancellationToken).ConfigureAwait(false);
            progress.Report(new OperationProgress(2, 3, Stage));
            if (changed.Count == 0 || kept.IsSupersetOf(changed))
            {
                break;
            }

            kept.UnionWith(changed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        Reports.AddOrUpdate(request, new SanitizeReport(
            counts.Metadata,
            counts.MetadataStreams,
            counts.Scripts,
            counts.Attachments,
            counts.RiskyActions,
            counts.ExternalLinks,
            counts.HiddenLayers,
            counts.HiddenContent,
            counts.OutsideObjects,
            counts.Thumbnails,
            counts.PrivateData,
            revisions,
            trailing,
            [.. kept.Order().Select(p => p + 1)]));
        progress.Report(new OperationProgress(3, 3, Stage));
    }

    /// <summary>
    /// How many earlier revisions the file keeps (one %%EOF per save; a linearized file has one more of its own) and how
    /// many bytes follow the last %%EOF (white space aside).
    /// </summary>
    internal static (int Revisions, long Trailing) History(byte[] file)
    {
        var marker = "%%EOF"u8;
        var count = 0;
        var last = -1;
        var at = 0;
        while (file.AsSpan(at).IndexOf(marker) is var found and >= 0)
        {
            last = at + found;
            at = last + marker.Length;
            count++;
        }

        if (count == 0)
        {
            return (0, 0);
        }

        if (count > 1 && file.AsSpan(0, Math.Min(file.Length, 1024)).IndexOf("/Linearized"u8) >= 0)
        {
            count--;
        }

        var trailing = file.AsSpan(last + marker.Length).ToArray().Count(b => b is not (0 or 9 or 10 or 12 or 13 or 32));
        return (count - 1, trailing);
    }

    /// <summary>On the PDFium thread: objects outside the visible area removed (pages in <paramref name="kept"/> aside).</summary>
    private static (byte[]? Bytes, Dictionary<int, int> Pages) RemoveOutsideCore(PdfiumDocument document, ISet<int> kept, CancellationToken cancellationToken)
    {
        var pages = new Dictionary<int, int>();
        for (var index = 0; index < document.PageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!kept.Contains(index) && document.RemoveObjectsOutsideVisibleBox(index) is var removed and > 0)
            {
                pages[index] = removed;
            }
        }

        if (pages.Count == 0)
        {
            return (null, pages);
        }

        using var stream = new MemoryStream();
        document.SaveCopy(stream);
        return (stream.ToArray(), pages);
    }

    private (byte[]? Bytes, Dictionary<int, int> Pages) RemoveOutside(string inputPath, ISet<int> kept, CancellationToken cancellationToken)
    {
        using var document = Open(() => PdfiumDocument.Open(gate, inputPath));
        return RemoveOutsideCore(document, kept, cancellationToken);
    }

    /// <summary>On the PDFium thread: the pages (0-based) that no longer look as they did.</summary>
    private List<int> Changed(string inputPath, byte[] cleaned, IReadOnlyList<int> pages)
    {
        using var before = Open(() => PdfiumDocument.Open(gate, inputPath));
        using var after = Open(() => PdfiumDocument.Open(gate, cleaned));
        return [.. pages.Where(p => p < after.PageCount && !PdfiumDocument.PagesLookAlike(before, after, p))];
    }

    private static PdfiumDocument Open(Func<PdfiumDocument> open)
    {
        try
        {
            return open();
        }
        catch (PdfDocumentException ex)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }
    }

    /// <summary>PDFsharp: everything else, then the file written anew.</summary>
    private static (byte[] Bytes, SanitizeCounts Counts) Clean(string inputPath, byte[]? rewritten, SanitizeParams request, ISet<int> kept, CancellationToken cancellationToken)
    {
        using var document = rewritten is null ? Stamping.DocumentContentOperation<SanitizeParams>.OpenForChange(inputPath) : OpenForChange(rewritten);
        var counts = DocumentSanitizer.Apply(document, request, kept, cancellationToken);
        using var stream = new MemoryStream();
        PdfWriting.Save(document, stream);
        return (stream.ToArray(), counts);
    }

    private static PdfDocument OpenForChange(byte[] bytes)
    {
        try
        {
            return PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
        {
            throw new InvalidDataException("The rewritten document cannot be read.", ex);
        }
    }

    internal static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }
}
