using PDFiumCore;
using PdfPro.Core.Geometry;

namespace PdfPro.Pdfium;

/// <summary>Lines and shapes crossing redacted areas (vector mode).</summary>
public sealed partial class PdfiumDocument
{
    private const int LineSegment = 0; // FPDF_SEGMENT_LINETO
    private const int BezierSegment = 1; // FPDF_SEGMENT_BEZIERTO
    private const int MoveSegment = 2; // FPDF_SEGMENT_MOVETO

    /// <summary>
    /// One subpath in the path's own space: a move, then lines and curves (a curve is three points in a row: two
    /// control points and its end).
    /// </summary>
    private sealed record Subpath(List<(int Kind, PdfPoint Point)> Points, bool Closed);

    /// <summary>
    /// A path crossing areas keeps what lies outside them: subpaths clear of every area as they are; when the path is
    /// drawn upright, stroked lines are cut at the areas and filled rectangles lose the part inside; any other subpath
    /// under an area goes whole (when it lay mostly under areas, its outline is not compared afterwards — the check
    /// still sees anything bigger that went missing). The path is rewritten as a new object with the same style.
    /// </summary>
    private static void RedactPath(FpdfPageT page, FpdfPageobjectT item, IReadOnlyList<PdfRect> rects, List<PdfRect> excluded)
    {
        var matrix = PdfiumPage.Matrix(item);
        int fillMode = 0, stroke = 0;
        if (fpdf_edit.FPDFPathGetDrawMode(item, ref fillMode, ref stroke) == 0)
        {
            throw new InvalidOperationException("PDFium could not read how a path is drawn.");
        }

        var upright = Math.Abs(matrix.B) < 1e-9 && Math.Abs(matrix.C) < 1e-9 && Math.Abs(matrix.Determinant) > 1e-12;
        IReadOnlyList<PdfRect> local = upright ? [.. rects.Select(r => matrix.Invert().TransformBounds(r))] : [];
        var subpaths = ReadSubpaths(item);
        var kept = new List<Subpath>();

        // A path that is one filled rectangle is drawn by PDFium with its edges on whole pixels: its pieces stay
        // rectangle objects so that they are drawn the same way.
        var rectangles = new List<PdfRect>();
        var changed = false;
        foreach (var subpath in subpaths)
        {
            var onPage = matrix.TransformBounds(BoundsOf(subpath));
            if (!rects.Any(r => RectClipping.Overlaps(onPage, r)))
            {
                kept.Add(subpath);
                continue;
            }

            changed = true;
            if (upright && stroke != 0 && fillMode == 0 && subpath.Points.All(p => p.Kind != BezierSegment))
            {
                foreach (var piece in RectClipping.Outside([.. subpath.Points.Select(p => p.Point)], subpath.Closed, local))
                {
                    kept.Add(new Subpath([.. piece.Points.Select((p, k) => (k == 0 ? MoveSegment : LineSegment, p))], piece.Closed));
                }

                continue;
            }

            if (upright && stroke == 0 && fillMode != 0 && AsRectangle(subpath) is { } rectangle)
            {
                foreach (var piece in RectClipping.Subtract(rectangle, local))
                {
                    if (subpaths.Count == 1)
                    {
                        rectangles.Add(piece);
                    }
                    else
                    {
                        kept.Add(new Subpath(
                            [
                                (MoveSegment, new PdfPoint(piece.Left, piece.Bottom)),
                                (LineSegment, new PdfPoint(piece.Right, piece.Bottom)),
                                (LineSegment, new PdfPoint(piece.Right, piece.Top)),
                                (LineSegment, new PdfPoint(piece.Left, piece.Top)),
                            ],
                            Closed: true));
                    }
                }

                continue;
            }

            if (Coverage(onPage, rects) >= 0.5)
            {
                excluded.Add(onPage);
            }
        }

        if (!changed)
        {
            return;
        }

        var at = IndexOf(page, item) + 1;
        var created = new List<FpdfPageobjectT>();
        if (kept.Count > 0)
        {
            created.Add(NewPath(kept));
        }

        foreach (var rectangle in rectangles)
        {
            created.Add(fpdf_edit.FPDFPageObjCreateNewRect((float)rectangle.Left, (float)rectangle.Bottom, (float)rectangle.Width, (float)rectangle.Height)
                ?? throw new InvalidOperationException("PDFium could not create a rectangle."));
        }

        foreach (var path in created)
        {
            try
            {
                if (path.__Instance == IntPtr.Zero)
                {
                    throw new InvalidOperationException("PDFium could not create a path.");
                }

                CopyPathStyle(item, path, fillMode, stroke);
                using var placed = new FS_MATRIX_
                {
                    A = (float)matrix.A,
                    B = (float)matrix.B,
                    C = (float)matrix.C,
                    D = (float)matrix.D,
                    E = (float)matrix.E,
                    F = (float)matrix.F,
                };
                if (fpdf_edit.FPDFPageObjSetMatrix(path, placed) == 0 || fpdf_edit.FPDFPageInsertObjectAtIndex(page, path, (ulong)at++) == 0)
                {
                    throw new InvalidOperationException("PDFium could not place a path.");
                }
            }
            catch
            {
                foreach (var other in created.Skip(created.IndexOf(path)))
                {
                    if (other.__Instance != IntPtr.Zero)
                    {
                        fpdf_edit.FPDFPageObjDestroy(other);
                    }
                }

                throw;
            }
        }

        Remove(page, item);
    }

    private static List<Subpath> ReadSubpaths(FpdfPageobjectT item)
    {
        var subpaths = new List<Subpath>();
        List<(int Kind, PdfPoint Point)>? current = null;
        var closed = false;
        var count = fpdf_edit.FPDFPathCountSegments(item);
        for (var i = 0; i < count; i++)
        {
            var segment = fpdf_edit.FPDFPathGetPathSegment(item, i);
            if (segment is null || segment.__Instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("PDFium could not read a path.");
            }

            var kind = fpdf_edit.FPDFPathSegmentGetType(segment);
            float x = 0, y = 0;
            if (kind is not (LineSegment or BezierSegment or MoveSegment) || fpdf_edit.FPDFPathSegmentGetPoint(segment, ref x, ref y) == 0)
            {
                throw new InvalidOperationException("PDFium could not read a path.");
            }

            var point = new PdfPoint(x, y);
            if (kind == MoveSegment || current is null || closed)
            {
                // After a close, drawing goes on from the start of the closed subpath.
                var start = closed && kind != MoveSegment ? current![0].Point : point;
                if (current is not null)
                {
                    subpaths.Add(new Subpath(current, closed));
                }

                current = [(MoveSegment, start)];
                closed = false;
                if (kind != MoveSegment)
                {
                    current.Add((kind, point));
                }
            }
            else
            {
                current.Add((kind, point));
            }

            closed |= fpdf_edit.FPDFPathSegmentGetClose(segment) != 0;
        }

        if (current is not null)
        {
            subpaths.Add(new Subpath(current, closed));
        }

        return subpaths;
    }

    private static PdfRect BoundsOf(Subpath subpath)
        => new(subpath.Points.Min(p => p.Point.X), subpath.Points.Min(p => p.Point.Y), subpath.Points.Max(p => p.Point.X), subpath.Points.Max(p => p.Point.Y));

    /// <summary>An upright rectangle drawn as four lines (as "re" is), or null.</summary>
    private static PdfRect? AsRectangle(Subpath subpath)
    {
        if (subpath.Points.Any(p => p.Kind == BezierSegment))
        {
            return null;
        }

        var points = subpath.Points.Select(p => p.Point).ToList();
        if (points.Count == 5 && points[4] == points[0])
        {
            points.RemoveAt(4);
        }

        if (points.Count != 4)
        {
            return null;
        }

        for (var i = 0; i < 4; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % 4]);
            if (a.X != b.X && a.Y != b.Y)
            {
                return null;
            }
        }

        var rect = new PdfRect(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        return rect.IsEmpty || points.Distinct().Count() != 4 ? null : rect;
    }

    /// <summary>How much of a box lies under the areas (0–1; a line counts by the part of it under them).</summary>
    private static double Coverage(PdfRect box, IReadOnlyList<PdfRect> rects)
    {
        var grown = new PdfRect(box.Left, box.Bottom, Math.Max(box.Right, box.Left + 0.01), Math.Max(box.Top, box.Bottom + 0.01));
        var area = grown.Width * grown.Height;
        var covered = rects.Sum(r => PdfRect.Intersect(grown, r) is { IsEmpty: false } overlap ? overlap.Width * overlap.Height : 0);
        return Math.Min(1, covered / area);
    }

    private static FpdfPageobjectT NewPath(IReadOnlyList<Subpath> subpaths)
    {
        var start = subpaths[0].Points[0].Point;
        var created = fpdf_edit.FPDFPageObjCreateNewPath((float)start.X, (float)start.Y);
        if (created is null || created.__Instance == IntPtr.Zero)
        {
            throw new InvalidOperationException("PDFium could not create a path.");
        }

        try
        {
            for (var s = 0; s < subpaths.Count; s++)
            {
                var points = subpaths[s].Points;
                if (s > 0 && fpdf_edit.FPDFPathMoveTo(created, (float)points[0].Point.X, (float)points[0].Point.Y) == 0)
                {
                    throw new InvalidOperationException("PDFium could not write a path.");
                }

                for (var i = 1; i < points.Count; i++)
                {
                    var ok = points[i].Kind == BezierSegment && i + 2 < points.Count
                        ? fpdf_edit.FPDFPathBezierTo(
                            created,
                            (float)points[i].Point.X,
                            (float)points[i].Point.Y,
                            (float)points[i + 1].Point.X,
                            (float)points[i + 1].Point.Y,
                            (float)points[i + 2].Point.X,
                            (float)points[i + 2].Point.Y)
                        : fpdf_edit.FPDFPathLineTo(created, (float)points[i].Point.X, (float)points[i].Point.Y);
                    if (ok == 0)
                    {
                        throw new InvalidOperationException("PDFium could not write a path.");
                    }

                    if (points[i].Kind == BezierSegment)
                    {
                        i += 2;
                    }
                }

                if (subpaths[s].Closed && fpdf_edit.FPDFPathClose(created) == 0)
                {
                    throw new InvalidOperationException("PDFium could not write a path.");
                }
            }
        }
        catch
        {
            fpdf_edit.FPDFPageObjDestroy(created);
            throw;
        }

        return created;
    }

    /// <summary>Colours, line width, joins, caps and dashes of a path, onto its rewritten copy.</summary>
    private static void CopyPathStyle(FpdfPageobjectT from, FpdfPageobjectT to, int fillMode, int stroke)
    {
        if (fpdf_edit.FPDFPathSetDrawMode(to, fillMode, stroke) == 0)
        {
            throw new InvalidOperationException("PDFium could not set how a path is drawn.");
        }

        uint red = 0, green = 0, blue = 0, alpha = 255;
        if (fpdf_edit.FPDFPageObjGetFillColor(from, ref red, ref green, ref blue, ref alpha) != 0)
        {
            fpdf_edit.FPDFPageObjSetFillColor(to, red, green, blue, alpha);
        }

        if (fpdf_edit.FPDFPageObjGetStrokeColor(from, ref red, ref green, ref blue, ref alpha) != 0)
        {
            fpdf_edit.FPDFPageObjSetStrokeColor(to, red, green, blue, alpha);
        }

        float width = 0;
        if (fpdf_edit.FPDFPageObjGetStrokeWidth(from, ref width) != 0)
        {
            fpdf_edit.FPDFPageObjSetStrokeWidth(to, width);
        }

        if (fpdf_edit.FPDFPageObjGetLineJoin(from) is var join and >= 0)
        {
            fpdf_edit.FPDFPageObjSetLineJoin(to, join);
        }

        if (fpdf_edit.FPDFPageObjGetLineCap(from) is var cap and >= 0)
        {
            fpdf_edit.FPDFPageObjSetLineCap(to, cap);
        }

        var dashes = fpdf_edit.FPDFPageObjGetDashCount(from);
        if (dashes > 0)
        {
            var array = new float[dashes];
            float phase = 0;
            if (fpdf_edit.FPDFPageObjGetDashArray(from, ref array[0], (ulong)dashes) != 0 && fpdf_edit.FPDFPageObjGetDashPhase(from, ref phase) != 0)
            {
                fpdf_edit.FPDFPageObjSetDashArray(to, ref array[0], (ulong)dashes, phase);
            }
        }
    }
}
