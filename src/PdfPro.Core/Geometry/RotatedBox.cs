namespace PdfPro.Core.Geometry;

/// <summary>The grips of a selected object: eight to resize, one above the top edge to rotate.</summary>
public enum BoxHandle
{
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    Rotate,
}

/// <summary>
/// A rectangle turned about its centre, in view space (y down): what the user moves, resizes and rotates on a page.
/// <see cref="Angle"/> is in degrees, clockwise as seen on screen, in [0, 360). "Local" coordinates are relative to the
/// centre along the box's own axes (x to its right, y to its bottom).
/// </summary>
public readonly record struct RotatedBox(ViewPoint Center, double Width, double Height, double Angle)
{
    /// <summary>Default step for rotating with Shift held.</summary>
    public const double RotationStep = 15;

    /// <summary>The corners: top-left, top-right, bottom-right, bottom-left (of the box's own axes).</summary>
    public IReadOnlyList<ViewPoint> Corners()
    {
        var (w, h) = (Width / 2, Height / 2);
        return [ToView(-w, -h), ToView(w, -h), ToView(w, h), ToView(-w, h)];
    }

    /// <summary>The axis-aligned rectangle around the turned box.</summary>
    public ViewRect Bounds
    {
        get
        {
            var corners = Corners();
            var left = corners.Min(c => c.X);
            var top = corners.Min(c => c.Y);
            return new ViewRect(left, top, corners.Max(c => c.X) - left, corners.Max(c => c.Y) - top);
        }
    }

    public ViewPoint ToView(double localX, double localY)
    {
        var (sin, cos) = SinCos(Angle);
        return new ViewPoint(Center.X + (localX * cos) - (localY * sin), Center.Y + (localX * sin) + (localY * cos));
    }

    public ViewPoint ToLocal(ViewPoint point)
    {
        var (sin, cos) = SinCos(Angle);
        var (dx, dy) = (point.X - Center.X, point.Y - Center.Y);
        return new ViewPoint((dx * cos) + (dy * sin), (-dx * sin) + (dy * cos));
    }

    /// <summary>True when <paramref name="point"/> is inside the box or within <paramref name="tolerance"/> of it.</summary>
    public bool Contains(ViewPoint point, double tolerance = 0)
    {
        var local = ToLocal(point);
        return Math.Abs(local.X) <= (Width / 2) + tolerance && Math.Abs(local.Y) <= (Height / 2) + tolerance;
    }

    /// <summary>Where a grip is drawn; the rotation grip sits <paramref name="rotateOffset"/> above the top edge.</summary>
    public ViewPoint HandlePosition(BoxHandle handle, double rotateOffset = 24)
    {
        if (handle == BoxHandle.Rotate)
        {
            return ToView(0, -(Height / 2) - rotateOffset);
        }

        var (sx, sy) = Signs(handle);
        return ToView(sx * Width / 2, sy * Height / 2);
    }

    /// <summary>The grip under <paramref name="point"/> (within <paramref name="radius"/>), or null.</summary>
    public BoxHandle? HitHandle(ViewPoint point, double radius, double rotateOffset = 24)
    {
        foreach (var handle in Enum.GetValues<BoxHandle>())
        {
            var position = HandlePosition(handle, rotateOffset);
            if (Math.Abs(position.X - point.X) <= radius && Math.Abs(position.Y - point.Y) <= radius)
            {
                return handle;
            }
        }

        return null;
    }

    /// <summary>
    /// The box after dragging a resize grip to <paramref name="pointer"/>: the opposite grip stays where it is. With
    /// <paramref name="keepAspect"/> the proportions are kept. Neither side goes below <paramref name="minSize"/>.
    /// </summary>
    public RotatedBox Resize(BoxHandle handle, ViewPoint pointer, bool keepAspect, double minSize = 1)
    {
        if (handle == BoxHandle.Rotate)
        {
            throw new ArgumentException("The rotation grip does not resize.", nameof(handle));
        }

        var (sx, sy) = Signs(handle);
        var local = ToLocal(pointer);
        var anchorX = -sx * Width / 2;
        var anchorY = -sy * Height / 2;
        var width = sx == 0 ? Width : Math.Max(minSize, (local.X - anchorX) * sx);
        var height = sy == 0 ? Height : Math.Max(minSize, (local.Y - anchorY) * sy);

        if (keepAspect && Width > 0 && Height > 0)
        {
            var factor = (sx, sy) switch
            {
                (0, _) => height / Height,
                (_, 0) => width / Width,
                _ => Math.Max(width / Width, height / Height),
            };
            factor = Math.Max(factor, minSize / Math.Min(Width, Height));
            (width, height) = (Width * factor, Height * factor);
        }

        // The centre moves along the dragged axes only; an edge grip keeps the other axis centred.
        var center = ToView(anchorX + (sx * width / 2), anchorY + (sy * height / 2));
        if (sx == 0)
        {
            center = ToView(0, anchorY + (sy * height / 2));
        }
        else if (sy == 0)
        {
            center = ToView(anchorX + (sx * width / 2), 0);
        }

        return new RotatedBox(center, width, height, Angle);
    }

    /// <summary>The box turned so that its rotation grip points at <paramref name="pointer"/>; snapped to steps when asked.</summary>
    public RotatedBox RotateTowards(ViewPoint pointer, bool snap, double step = RotationStep)
    {
        var (dx, dy) = (pointer.X - Center.X, pointer.Y - Center.Y);
        if (dx == 0 && dy == 0)
        {
            return this;
        }

        var angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
        if (snap && step > 0)
        {
            angle = Math.Round(angle / step) * step;
        }

        return this with { Angle = NormalizeAngle(angle) };
    }

    public RotatedBox Offset(double dx, double dy) => this with { Center = new ViewPoint(Center.X + dx, Center.Y + dy) };

    /// <summary>Any angle in degrees brought into [0, 360).</summary>
    public static double NormalizeAngle(double degrees)
    {
        var angle = degrees % 360;
        if (angle < 0)
        {
            angle += 360;
        }

        return Math.Abs(angle - 360) < 1e-9 ? 0 : angle;
    }

    private static (int X, int Y) Signs(BoxHandle handle) => handle switch
    {
        BoxHandle.TopLeft => (-1, -1),
        BoxHandle.Top => (0, -1),
        BoxHandle.TopRight => (1, -1),
        BoxHandle.Right => (1, 0),
        BoxHandle.BottomRight => (1, 1),
        BoxHandle.Bottom => (0, 1),
        BoxHandle.BottomLeft => (-1, 1),
        BoxHandle.Left => (-1, 0),
        _ => (0, 0),
    };

    private static (double Sin, double Cos) SinCos(double degrees)
    {
        if (degrees % 90 == 0)
        {
            var quarter = (int)(NormalizeAngle(degrees) / 90);
            return quarter switch { 0 => (0, 1), 1 => (1, 0), 2 => (0, -1), _ => (-1, 0) };
        }

        return Math.SinCos(degrees * Math.PI / 180);
    }
}
