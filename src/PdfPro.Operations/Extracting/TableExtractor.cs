using System.Text.RegularExpressions;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Text;
using PdfPro.Operations.Security;
using Tabula;
using Tabula.Detectors;
using Tabula.Extractors;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using PigRectangle = UglyToad.PdfPig.Core.PdfRectangle;

namespace PdfPro.Operations.Extracting;

/// <summary>How a table's cells are found.</summary>
public enum TableMethod
{
    /// <summary>By its rules when it has them, else by how its text lines up.</summary>
    Auto,

    /// <summary>By the lines drawn around its cells ("lattice").</summary>
    Lattice,

    /// <summary>By how its text lines up in columns ("stream").</summary>
    Stream,
}

/// <summary>Where to read a table: a page (0-based) and an area of it in PDF space (null: find the tables of the page).</summary>
public sealed record TableRegion(int Page, PdfRect? Area = null, TableMethod Method = TableMethod.Auto);

/// <summary>A table read from a page: where it is (PDF space), how it was read, and its rows of cell texts (NFC).</summary>
public sealed record ExtractedTable(int Page, PdfRect Box, TableMethod Method, IReadOnlyList<IReadOnlyList<string>> Rows)
{
    public int RowCount => Rows.Count;

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
}

/// <summary>
/// Tables of PDF pages, by tabula-sharp on PdfPig. On a whole page, ruled tables are read by their lines first
/// (<see cref="SpreadsheetExtractionAlgorithm"/>); then the regions that look like tables (Nurminen's detection) and are
/// not one of them are read by how their text lines up (<see cref="BasicExtractionAlgorithm"/>); only tables of at least
/// two rows and two columns count. In an area the user drew, the ruled reading is tried first, the other otherwise.
/// Cell texts are NFC with their spaces tidied; rows and columns left empty are dropped.
/// </summary>
public static partial class TableExtractor
{
    public const string Stage = "Stage_ExtractTables";

    /// <summary>A region found by detection that lies this much inside a ruled table is that table.</summary>
    private const double SameTableShare = 0.5;

    /// <summary>Every table of the pages (0-based; null for all), in page order and top to bottom.</summary>
    /// <exception cref="DocumentPasswordException">The document is protected and its password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">The document cannot be read.</exception>
    public static Task<IReadOnlyList<ExtractedTable>> FindAsync(string path, string? password, IReadOnlyList<int>? pages, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run<IReadOnlyList<ExtractedTable>>(
            () =>
            {
                using var document = Open(path, password);
                var targets = pages ?? [.. Enumerable.Range(0, document.NumberOfPages)];
                if (targets.FirstOrDefault(p => p < 0 || p >= document.NumberOfPages, -1) is var wrong and >= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(pages), wrong, $"The document has {document.NumberOfPages} pages.");
                }

                var tables = new List<ExtractedTable>();
                for (var i = 0; i < targets.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new OperationProgress(i, targets.Count, Stage));
                    tables.AddRange(OnPage(document, targets[i]));
                }

                progress?.Report(new OperationProgress(targets.Count, targets.Count, Stage));
                return tables;
            },
            cancellationToken);
    }

    /// <summary>The table of each region (in their order): the tables of the page when it has no area; nothing for an area without one.</summary>
    /// <exception cref="DocumentPasswordException">The document is protected and its password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">The document cannot be read.</exception>
    public static Task<IReadOnlyList<ExtractedTable>> ReadAsync(string path, string? password, IReadOnlyList<TableRegion> regions, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(regions);
        return Task.Run<IReadOnlyList<ExtractedTable>>(
            () =>
            {
                using var document = Open(path, password);
                if (regions.FirstOrDefault(r => r.Page < 0 || r.Page >= document.NumberOfPages) is { } wrong)
                {
                    throw new ArgumentOutOfRangeException(nameof(regions), wrong.Page, $"The document has {document.NumberOfPages} pages.");
                }

                var tables = new List<ExtractedTable>();
                for (var i = 0; i < regions.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new OperationProgress(i, regions.Count, Stage));
                    var region = regions[i];
                    tables.AddRange(region.Area is { } area ? InArea(document, region.Page, area, region.Method) : OnPage(document, region.Page, region.Method));
                }

                progress?.Report(new OperationProgress(regions.Count, regions.Count, Stage));
                return tables;
            },
            cancellationToken);
    }

    /// <summary>The pages of the document.</summary>
    /// <exception cref="DocumentPasswordException">The document is protected and its password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">The document cannot be read.</exception>
    public static int CountPages(string path, string? password)
    {
        using var document = Open(path, password);
        return document.NumberOfPages;
    }

    /// <summary>The tables of a whole page.</summary>
    private static List<ExtractedTable> OnPage(PdfDocument document, int page, TableMethod method = TableMethod.Auto)
    {
        var area = Extract(document, page);
        var found = new List<ExtractedTable>();
        if (method != TableMethod.Stream)
        {
            found.AddRange(new SpreadsheetExtractionAlgorithm().Extract(area).Select(t => Read(t, page, TableMethod.Lattice)).Where(IsTable));
        }

        if (method != TableMethod.Lattice)
        {
            foreach (var region in new SimpleNurminenDetectionAlgorithm().Detect(area))
            {
                var box = Box(region);
                if (found.Any(t => Overlap(box, t.Box) >= SameTableShare * Math.Max(1, box.Width * box.Height)))
                {
                    continue;
                }

                found.AddRange(new BasicExtractionAlgorithm().Extract(area.GetArea(region.BoundingBox)).Select(t => Read(t, page, TableMethod.Stream)).Where(IsTable));
            }
        }

        return [.. found.OrderByDescending(t => Math.Round(t.Box.Top)).ThenBy(t => t.Box.Left)];
    }

    /// <summary>The table in an area: by its rules when they make one (the largest), else by its text.</summary>
    private static List<ExtractedTable> InArea(PdfDocument document, int page, PdfRect area, TableMethod method)
    {
        var whole = Extract(document, page);
        var part = whole.GetArea(new PigRectangle(area.Left, area.Bottom, area.Right, area.Top));
        if (method != TableMethod.Stream
            && new SpreadsheetExtractionAlgorithm().Extract(part).Select(t => Read(t, page, TableMethod.Lattice)).Where(IsTable).MaxBy(t => t.RowCount * t.ColumnCount) is { } ruled)
        {
            return [ruled];
        }

        if (method == TableMethod.Lattice)
        {
            return [];
        }

        var read = new BasicExtractionAlgorithm().Extract(part).Select(t => Read(t, page, TableMethod.Stream) with { Box = area }).Where(t => t.RowCount > 0).ToList();
        return read.Count <= 1 ? read : [Merge(read, area)];
    }

    private static PageArea Extract(PdfDocument document, int page)
    {
        try
        {
            return ObjectExtractor.Extract(document, page + 1);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException or KeyNotFoundException or PdfDocumentFormatException)
        {
            throw new InvalidDataException($"Page {page + 1} cannot be read.", ex);
        }
    }

    /// <summary>A Tabula table as rows of tidy texts, without empty rows and columns.</summary>
    private static ExtractedTable Read(Table table, int page, TableMethod method)
    {
        var rows = table.Rows.Select(r => r.Select(c => Tidy(c.GetText())).ToList()).ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        foreach (var row in rows)
        {
            row.AddRange(Enumerable.Repeat(string.Empty, columns - row.Count));
        }

        var keep = Enumerable.Range(0, columns).Where(c => rows.Any(r => r[c].Length > 0)).ToList();
        IReadOnlyList<IReadOnlyList<string>> cells = [.. rows.Where(r => r.Any(c => c.Length > 0)).Select(r => (IReadOnlyList<string>)[.. keep.Select(c => r[c])])];
        return new ExtractedTable(page, Box(table), method, cells);
    }

    /// <summary>Several tables read from one area, one under the other, as one.</summary>
    private static ExtractedTable Merge(List<ExtractedTable> tables, PdfRect area)
    {
        var columns = tables.Max(t => t.ColumnCount);
        IReadOnlyList<IReadOnlyList<string>> rows = [.. tables.SelectMany(t => t.Rows).Select(r => (IReadOnlyList<string>)[.. r, .. Enumerable.Repeat(string.Empty, columns - r.Count)])];
        return new ExtractedTable(tables[0].Page, area, TableMethod.Stream, rows);
    }

    private static bool IsTable(ExtractedTable table) => table.RowCount >= 2 && table.ColumnCount >= 2;

    private static PdfRect Box(TableRectangle rectangle) => new(rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Top);

    private static double Overlap(PdfRect a, PdfRect b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var height = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        return width > 0 && height > 0 ? width * height : 0;
    }

    private static string Tidy(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : Spaces().Replace(TextNormalizer.ToNfc(text), " ").Trim();

    private static PdfDocument Open(string path, string? password)
    {
        var options = new ParsingOptions { ClipPaths = true, UseLenientParsing = true, SkipMissingFonts = true };
        if (!string.IsNullOrEmpty(password))
        {
            options.Password = password;
        }

        try
        {
            return PdfDocument.Open(path, options);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new DocumentPasswordException(DocumentPasswordProblem.WrongPassword, ex);
        }
        catch (Exception ex) when (ex is PdfDocumentFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException or KeyNotFoundException)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
