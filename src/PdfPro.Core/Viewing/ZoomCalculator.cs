using PdfPro.Core.Geometry;

namespace PdfPro.Core.Viewing;

/// <summary>How the zoom follows the viewport.</summary>
public enum ZoomMode
{
    /// <summary>A fixed zoom factor chosen by the user.</summary>
    Custom,

    /// <summary>Re-fit the widest row to the viewport width on every resize.</summary>
    FitWidth,

    /// <summary>Re-fit the current row (width and height) on every resize.</summary>
    FitPage,
}

/// <summary>Zoom presets, steps and fit computations. Zoom factors: 1.0 = 100 %.</summary>
public static class ZoomCalculator
{
    public const double MinZoom = 0.10;
    public const double MaxZoom = 8.00;

    /// <summary>Factor applied per mouse-wheel notch (Ctrl + wheel).</summary>
    public const double WheelStep = 1.1;

    private const double Epsilon = 0.0005;

    public static IReadOnlyList<double> Presets { get; } = [0.10, 0.25, 0.50, 0.75, 1.00, 1.25, 1.50, 2.00, 3.00, 4.00, 6.00, 8.00];

    public static double Clamp(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, MinZoom, MaxZoom) : 1.0;

    /// <summary>The next larger preset (Ctrl + =).</summary>
    public static double ZoomIn(double current)
        => Presets.FirstOrDefault(p => p > current + Epsilon, MaxZoom);

    /// <summary>The next smaller preset (Ctrl + −).</summary>
    public static double ZoomOut(double current)
        => Presets.LastOrDefault(p => p < current - Epsilon, MinZoom);

    /// <summary>Zoom after <paramref name="notches"/> wheel notches (positive = zoom in).</summary>
    public static double ZoomByWheel(double current, double notches) => Clamp(current * Math.Pow(WheelStep, notches));

    /// <summary>The zoom at which the widest row exactly fills <paramref name="viewportWidth"/>.</summary>
    public static double FitWidth(IReadOnlyList<PdfSize> pageSizes, PageLayoutMode mode, double viewportWidth, LayoutSpacing? spacing = null)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        if (pageSizes.Count == 0)
        {
            return 1.0;
        }

        spacing ??= LayoutSpacing.Default;
        var widest = pageSizes.Max(s => s.Width);
        var (points, fixedUnits) = mode == PageLayoutMode.Single
            ? (widest, 2 * spacing.Margin)
            : (2 * widest, (2 * spacing.Margin) + spacing.ColumnGap);
        return FitAxis(points, fixedUnits, viewportWidth);
    }

    /// <summary>The zoom at which the row of <paramref name="pageIndex"/> fits entirely in the viewport.</summary>
    public static double FitPage(IReadOnlyList<PdfSize> pageSizes, PageLayoutMode mode, int pageIndex, ViewSize viewport, LayoutSpacing? spacing = null)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        if (pageSizes.Count == 0)
        {
            return 1.0;
        }

        spacing ??= LayoutSpacing.Default;
        pageIndex = Math.Clamp(pageIndex, 0, pageSizes.Count - 1);
        var (first, count) = RowOf(pageIndex, pageSizes.Count, mode);
        var rowSizes = Enumerable.Range(first, count).Select(i => pageSizes[i]).ToArray();
        var height = rowSizes.Max(s => s.Height);
        double widthPoints;
        double fixedWidth;
        if (mode == PageLayoutMode.Single)
        {
            widthPoints = rowSizes[0].Width;
            fixedWidth = 2 * spacing.Margin;
        }
        else
        {
            // The spine stays centred, so a row needs twice its widest half.
            widthPoints = 2 * rowSizes.Max(s => s.Width);
            fixedWidth = (2 * spacing.Margin) + spacing.ColumnGap;
        }

        var byWidth = FitAxis(widthPoints, fixedWidth, viewport.Width);
        var byHeight = FitAxis(height, spacing.RowGap, viewport.Height);
        return Math.Min(byWidth, byHeight);
    }

    /// <summary>First page and page count of the row containing <paramref name="pageIndex"/>.</summary>
    public static (int First, int Count) RowOf(int pageIndex, int pageCount, PageLayoutMode mode)
    {
        switch (mode)
        {
            case PageLayoutMode.Facing:
                {
                    var first = pageIndex - (pageIndex % 2);
                    return (first, Math.Min(2, pageCount - first));
                }

            case PageLayoutMode.FacingWithCover when pageIndex == 0:
                return (0, 1);

            case PageLayoutMode.FacingWithCover:
                {
                    var first = pageIndex - ((pageIndex - 1) % 2);
                    return (first, Math.Min(2, pageCount - first));
                }

            default:
                return (pageIndex, 1);
        }
    }

    private static double FitAxis(double points, double fixedUnits, double available)
    {
        if (points <= 0 || !double.IsFinite(available))
        {
            return 1.0;
        }

        var room = Math.Max(1, available - fixedUnits);
        return Clamp(room / (points * DocumentLayout.UnitsPerPointAt100Percent));
    }
}
