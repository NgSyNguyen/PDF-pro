using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Comparing;

/// <summary>How pages are compared as pictures.</summary>
/// <param name="Dpi">The resolution both pages are drawn at.</param>
/// <param name="Threshold">A pixel differs when a colour channel differs by more than this (0–255): antialiasing and JPEG noise stay below.</param>
/// <param name="CellSize">Pixels are counted in square cells of this side; a cell with <paramref name="MinPixels"/> differing pixels or more differs.</param>
/// <param name="MinPixels">Differing pixels a cell needs (specks of noise do not count).</param>
public sealed record VisualCompareOptions(double Dpi = 100, int Threshold = 48, int CellSize = 8, int MinPixels = 3);

/// <summary>Where two paired pages look different: boxes in each page's PDF space (displayed page order).</summary>
/// <param name="Pair">The pages.</param>
/// <param name="OldRegions">Regions of the old page that differ.</param>
/// <param name="NewRegions">The same regions on the new page.</param>
/// <param name="ChangedShare">The share of the page (0–1) that differs.</param>
public sealed record VisualDifference(PagePair Pair, IReadOnlyList<PdfRect> OldRegions, IReadOnlyList<PdfRect> NewRegions, double ChangedShare)
{
    public bool IsSame => OldRegions.Count == 0 && NewRegions.Count == 0;
}

/// <summary>
/// compare.visual: paired pages drawn by PDFium at the same resolution (as displayed, with comments) and compared pixel
/// by pixel; differing pixels are gathered in cells, neighbouring differing cells in regions, and each region is given
/// back as a box of both pages. Pages of different sizes are compared over their common top-left part, the rest
/// differs; a page on one side only differs whole.
/// </summary>
public static class VisualComparer
{
    public const string Stage = "Stage_CompareVisual";

    public static async Task<IReadOnlyList<VisualDifference>> CompareAsync(
        PdfiumGate gate,
        string oldPath,
        string? oldPassword,
        string newPath,
        string? newPassword,
        IReadOnlyList<PagePair> pairs,
        VisualCompareOptions options,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);
        PdfiumDocument? oldDocument = null;
        PdfiumDocument? newDocument = null;
        try
        {
            await gate.RunAsync(
                () =>
                {
                    oldDocument = PdfiumDocument.Open(gate, oldPath, oldPassword);
                    newDocument = PdfiumDocument.Open(gate, newPath, newPassword);
                },
                PdfiumPriority.Normal,
                cancellationToken).ConfigureAwait(false);
            var results = new List<VisualDifference>(pairs.Count);
            for (var i = 0; i < pairs.Count; i++)
            {
                progress.Report(new OperationProgress(i, pairs.Count, Stage));
                var pair = pairs[i];
                (Picture Picture, PageGeometry Geometry)? old = pair.OldPage is { } o ? await RenderAsync(gate, oldDocument!, o, options.Dpi, cancellationToken).ConfigureAwait(false) : null;
                (Picture Picture, PageGeometry Geometry)? @new = pair.NewPage is { } n ? await RenderAsync(gate, newDocument!, n, options.Dpi, cancellationToken).ConfigureAwait(false) : null;
                results.Add(await Task.Run(() => Compare(pair, old?.Picture, old?.Geometry, @new?.Picture, @new?.Geometry, options), cancellationToken).ConfigureAwait(false));
            }

            progress.Report(new OperationProgress(pairs.Count, pairs.Count, Stage));
            return results;
        }
        finally
        {
            await gate.RunAsync(
                () =>
                {
                    oldDocument?.Dispose();
                    newDocument?.Dispose();
                },
                PdfiumPriority.High,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>A page drawn in BGRA (with comments and fields, as seen), and its geometry.</summary>
    private static Task<(Picture Picture, PageGeometry Geometry)> RenderAsync(PdfiumGate gate, PdfiumDocument document, int page, double dpi, CancellationToken cancellationToken)
        => gate.RunAsync(
            () =>
            {
                var geometry = document.GetPageGeometry(page);
                using var bitmap = document.RenderPage(page, new PageRenderOptions { Scale = dpi / 72, Flags = PdfRenderFlags.Annotations });
                return (new Picture(bitmap.Width, bitmap.Height, bitmap.Pixels.ToArray(), bitmap.Stride), geometry);
            },
            PdfiumPriority.Normal,
            cancellationToken);

    internal static VisualDifference Compare(PagePair pair, Picture? oldPicture, PageGeometry? oldGeometry, Picture? newPicture, PageGeometry? newGeometry, VisualCompareOptions options)
    {
        if (oldPicture is null || newPicture is null)
        {
            // A page on one side only: all of it.
            return new VisualDifference(
                pair,
                oldGeometry is { } og ? [og.Box] : [],
                newGeometry is { } ng ? [ng.Box] : [],
                1);
        }

        var cell = Math.Max(1, options.CellSize);
        var (width, height) = (Math.Max(oldPicture.Width, newPicture.Width), Math.Max(oldPicture.Height, newPicture.Height));
        var (columns, rows) = ((width + cell - 1) / cell, (height + cell - 1) / cell);
        var counts = new int[columns * rows];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (Differs(oldPicture, newPicture, x, y, options.Threshold))
                {
                    counts[((y / cell) * columns) + (x / cell)]++;
                }
            }
        }

        var changed = counts.Select(c => c >= options.MinPixels).ToArray();
        var regions = Regions(changed, columns, rows);
        var share = (double)changed.Count(c => c) / changed.Length;
        var scale = options.Dpi / 72;
        List<PdfRect> Map(PageGeometry geometry)
        {
            var mapper = new PageCoordinateMapper(geometry, scale);
            return [.. regions.Select(r => mapper.ScreenRectToPdf(new ViewRect(r.X * cell, r.Y * cell, r.Width * cell, r.Height * cell)))];
        }

        return new VisualDifference(pair, Map(oldGeometry!.Value), Map(newGeometry!.Value), share);
    }

    private static bool Differs(Picture a, Picture b, int x, int y, int threshold)
    {
        var inA = x < a.Width && y < a.Height;
        var inB = x < b.Width && y < b.Height;
        if (!inA || !inB)
        {
            return inA != inB;
        }

        var (pa, pb) = ((y * a.Stride) + (x * 4), (y * b.Stride) + (x * 4));
        return Math.Abs(a.Pixels[pa] - b.Pixels[pb]) > threshold
            || Math.Abs(a.Pixels[pa + 1] - b.Pixels[pb + 1]) > threshold
            || Math.Abs(a.Pixels[pa + 2] - b.Pixels[pb + 2]) > threshold;
    }

    /// <summary>The bounding boxes (in cells) of the groups of differing cells that touch (8 neighbours, one cell of gap bridged).</summary>
    internal static List<PixelRect> Regions(bool[] changed, int columns, int rows)
    {
        var seen = new bool[changed.Length];
        var regions = new List<PixelRect>();
        var stack = new Stack<int>();
        for (var start = 0; start < changed.Length; start++)
        {
            if (!changed[start] || seen[start])
            {
                continue;
            }

            var (minX, minY, maxX, maxY) = (int.MaxValue, int.MaxValue, -1, -1);
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var at = stack.Pop();
                var (cx, cy) = (at % columns, at / columns);
                (minX, minY, maxX, maxY) = (Math.Min(minX, cx), Math.Min(minY, cy), Math.Max(maxX, cx), Math.Max(maxY, cy));
                for (var ny = Math.Max(0, cy - 2); ny <= Math.Min(rows - 1, cy + 2); ny++)
                {
                    for (var nx = Math.Max(0, cx - 2); nx <= Math.Min(columns - 1, cx + 2); nx++)
                    {
                        var next = (ny * columns) + nx;
                        if (changed[next] && !seen[next])
                        {
                            seen[next] = true;
                            stack.Push(next);
                        }
                    }
                }
            }

            regions.Add(new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        }

        return regions;
    }

    /// <summary>A rendered page: BGRA rows.</summary>
    internal sealed record Picture(int Width, int Height, byte[] Pixels, int Stride);
}
