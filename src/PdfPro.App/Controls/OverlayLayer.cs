using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfPro.App.Services.Editing;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;

namespace PdfPro.App.Controls;

/// <summary>
/// The page editor's layer over one page of the viewer: draws the page's unapplied objects as they will be written,
/// the selection with its grips and the alignment guides, and turns the mouse into editor actions — draw a new object
/// with the current tool, select (Ctrl adds), move (snapping to the page and other objects; Alt turns snapping off),
/// resize with eight grips (Shift keeps proportions, the default for pictures), turn with the top grip (Shift: 15°
/// steps), drag line ends. Only active while the editor is; otherwise it draws nothing and lets the mouse through.
/// All page coordinates go through <see cref="PageCoordinateMapper"/>.
/// </summary>
public sealed class OverlayLayer : FrameworkElement
{
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor), typeof(PageEditorViewModel), typeof(OverlayLayer), new PropertyMetadata(null, OnEditorChanged));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(Guid), typeof(OverlayLayer), new PropertyMetadata(Guid.Empty, (d, _) => ((OverlayLayer)d).InvalidateVisual()));

    private const double GripSize = 8;
    private const double GripHit = 7;
    private const double RotateOffset = 24;

    private static readonly Brush SelectionBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)));
    private static readonly Pen SelectionPen = Freeze(new Pen(SelectionBrush, 1));
    private static readonly Pen GuidePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x00, 0x8A))), 1));
    private static readonly Pen EmptyTextPen = Freeze(new Pen(Brushes.Gray, 1) { DashStyle = DashStyles.Dash });

    /// <summary>A bare text box (the app theme's would paint it as a dark input field over the page).</summary>
    private static readonly ControlTemplate PlainTextBoxTemplate = CreatePlainTextBoxTemplate();

    private Gesture _gesture;
    private Point _start;
    private bool _recorded;
    private BoxHandle _handle;
    private bool _lineStart;
    private RotatedBox _startBox;
    private OverlayObjectViewModel? _target;
    private List<(OverlayObjectViewModel Object, OverlayItem Item)> _startItems = [];
    private OverlayItem? _preview;
    private IReadOnlyList<SnapGuide> _guides = [];
    private TextBox? _textBox;
    private OverlayObjectViewModel? _textTarget;

    public OverlayLayer()
    {
        Focusable = false;
        Unloaded += (_, _) => Detach(Editor);
        Loaded += (_, _) => Attach(Editor);
    }

    protected override int VisualChildrenCount => _textBox is null ? 0 : 1;

    protected override Visual GetVisualChild(int index)
        => index == 0 && _textBox is not null ? _textBox : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size availableSize)
    {
        _textBox?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_textBox is not null && _textTarget?.Item is TextOverlay text && Mapper() is { } mapper)
        {
            var box = text.Box.ToView(mapper);
            _textBox.Width = box.Width;
            _textBox.MinHeight = box.Height;
            _textBox.RenderTransform = new RotateTransform(box.Angle, box.Width / 2, box.Height / 2);
            _textBox.Arrange(new Rect(box.Center.X - (box.Width / 2), box.Center.Y - (box.Height / 2), box.Width, Math.Max(box.Height, _textBox.DesiredSize.Height)));
        }

        return finalSize;
    }

    private enum Gesture
    {
        None,
        Creating,
        Moving,
        Resizing,
        Rotating,
        LineEnd,
    }

    public PageEditorViewModel? Editor
    {
        get => (PageEditorViewModel?)GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>The plan's key of the page under the layer.</summary>
    public Guid PageKey
    {
        get => (Guid)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Editor is not { IsActive: true } editor || Mapper() is not { } mapper)
        {
            return;
        }

        // Catches the mouse over the whole page while the editor is open.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var target in editor.ObjectsOn(PageKey))
        {
            if (target == editor.EditingText && target.Item is TextOverlay typing)
            {
                // The text box shows the text while it is typed.
                DrawPolygon(drawingContext, typing.Box.ToView(mapper).Corners(), EmptyTextPen);
                continue;
            }

            DrawItem(drawingContext, editor, target.Item, mapper, pixelsPerDip);
        }

        if (_gesture == Gesture.Creating && _preview is not null)
        {
            DrawItem(drawingContext, editor, _preview, mapper, pixelsPerDip);
        }

        foreach (var target in editor.ObjectsOn(PageKey).Where(o => o.IsSelected))
        {
            DrawSelection(drawingContext, target.Item, mapper);
        }

        foreach (var guide in _guides)
        {
            var (from, to) = guide.IsVertical
                ? (new Point(guide.Position, guide.From), new Point(guide.Position, guide.To))
                : (new Point(guide.From, guide.Position), new Point(guide.To, guide.Position));
            drawingContext.DrawLine(GuidePen, from, to);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (Editor is not { IsActive: true } editor || Mapper() is not { } mapper)
        {
            return;
        }

        _start = e.GetPosition(this);
        _recorded = false;
        _guides = [];
        if (editor.EditingText is not null)
        {
            // A click outside the text being typed ends typing (and does nothing else).
            editor.EndTextEdit();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2 && editor.Tool == OverlayTool.Select && ObjectAt(editor, mapper, _start) is { Item: TextOverlay } text)
        {
            editor.BeginTextEdit(text);
            e.Handled = true;
            return;
        }

        if (editor.Tool is OverlayTool.Text or OverlayTool.Rectangle or OverlayTool.Ellipse or OverlayTool.Line or OverlayTool.Arrow or OverlayTool.Whiteout)
        {
            _gesture = Gesture.Creating;
            _preview = NewItem(editor, mapper, _start);
        }
        else if (!StartOnSelection(editor, mapper, _start))
        {
            StartOnObject(editor, mapper, _start, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }

        if (_gesture != Gesture.None)
        {
            CaptureMouse();
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (Editor is not { IsActive: true } editor || Mapper() is not { } mapper)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (_gesture == Gesture.None)
        {
            Cursor = CursorAt(editor, mapper, point);
            return;
        }

        if (_gesture != Gesture.Creating && !_recorded)
        {
            if (Math.Abs(point.X - _start.X) < 0.5 && Math.Abs(point.Y - _start.Y) < 0.5)
            {
                return;
            }

            editor.BeginGesture();
            _recorded = true;
        }

        var snap = !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_gesture)
        {
            case Gesture.Creating:
                _preview = NewItem(editor, mapper, point);
                InvalidateVisual();
                break;
            case Gesture.Moving:
                Move(editor, mapper, point, snap);
                break;
            case Gesture.Resizing when _target is not null:
                var keepAspect = shift ^ (_target.Item is ImageOverlay);
                var resized = _startBox.Resize(_handle, SnapPointer(editor, mapper, point, snap), keepAspect, minSize: 4);
                editor.Update(_target, WithBox(_target.Item, OverlayBox.FromView(resized, mapper)));
                break;
            case Gesture.Rotating when _target is not null:
                var turned = _startBox.RotateTowards(new ViewPoint(point.X, point.Y), snap: shift);
                editor.Update(_target, WithBox(_target.Item, OverlayBox.FromView(turned, mapper)));
                break;
            case Gesture.LineEnd when _target?.Item is LineOverlay line:
                var end = mapper.ScreenToPdf(new ViewPoint(point.X, point.Y));
                editor.Update(_target, _lineStart ? line with { Start = end } : line with { End = end });
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_gesture == Gesture.None)
        {
            return;
        }

        if (_gesture == Gesture.Creating && Editor is { } editor && Mapper() is { } mapper)
        {
            if (NewItem(editor, mapper, e.GetPosition(this)) is { } item)
            {
                var added = editor.Add(PageKey, item);
                if (item is TextOverlay)
                {
                    // Typing starts at once; adding and typing are one undoable step.
                    editor.BeginTextEdit(added, record: false);
                }
            }

            // Back to selecting, so the new object can be adjusted at once.
            editor.Tool = OverlayTool.Select;
        }

        _gesture = Gesture.None;
        _preview = null;
        _target = null;
        _guides = [];
        ReleaseMouseCapture();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _gesture = Gesture.None;
        _preview = null;
        _guides = [];
        InvalidateVisual();
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static ControlTemplate CreatePlainTextBoxTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        border.AppendChild(host);
        var template = new ControlTemplate(typeof(TextBox)) { VisualTree = border };
        template.Seal();
        return template;
    }

    private static void OnEditorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (OverlayLayer)d;
        layer.Detach(e.OldValue as PageEditorViewModel);
        if (layer.IsLoaded)
        {
            layer.Attach(e.NewValue as PageEditorViewModel);
        }
    }

    private static OverlayBox? BoxOf(OverlayItem item) => item switch
    {
        TextOverlay t => t.Box,
        ImageOverlay i => i.Box,
        ShapeOverlay s => s.Box,
        _ => null,
    };

    private static OverlayItem WithBox(OverlayItem item, OverlayBox box) => item switch
    {
        TextOverlay t => t with { Box = box },
        ImageOverlay i => i with { Box = box },
        ShapeOverlay s => s with { Box = box },
        _ => item,
    };

    private static double DistanceToSegment(Point p, ViewPoint a, ViewPoint b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var lengthSquared = (dx * dx) + (dy * dy);
        var t = lengthSquared == 0 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        var (x, y) = (a.X + (t * dx), a.Y + (t * dy));
        return Math.Sqrt(((p.X - x) * (p.X - x)) + ((p.Y - y) * (p.Y - y)));
    }

    private static Cursor HandleCursor(BoxHandle handle, double angle)
    {
        if (handle == BoxHandle.Rotate)
        {
            return Cursors.Hand;
        }

        // The grip's direction as seen on screen, in eighths of a turn.
        var direction = handle switch
        {
            BoxHandle.Top or BoxHandle.Bottom => 0,
            BoxHandle.TopRight or BoxHandle.BottomLeft => 1,
            BoxHandle.Right or BoxHandle.Left => 2,
            _ => 3,
        };
        var eighth = (direction + (int)Math.Round(angle / 45)) % 4;
        return eighth switch { 0 => Cursors.SizeNS, 1 => Cursors.SizeNESW, 2 => Cursors.SizeWE, _ => Cursors.SizeNWSE };
    }

    private void Attach(PageEditorViewModel? editor)
    {
        if (editor is not null)
        {
            editor.Invalidated -= OnInvalidated;
            editor.Invalidated += OnInvalidated;
        }

        SyncTextBox();
        InvalidateVisual();
    }

    private void Detach(PageEditorViewModel? editor)
    {
        if (editor is not null)
        {
            editor.Invalidated -= OnInvalidated;
        }

        RemoveTextBox();
    }

    private void OnInvalidated(object? sender, EventArgs e)
    {
        SyncTextBox();
        InvalidateVisual();
    }

    /// <summary>Shows a text box over the object being typed in when it is on this page, styled like its text.</summary>
    private void SyncTextBox()
    {
        if (Editor is not { EditingText: { PageKey: var key, Item: TextOverlay text } target } editor || key != PageKey || Mapper() is not { } mapper)
        {
            RemoveTextBox();
            return;
        }

        if (_textBox is null)
        {
            _textBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Freeze(new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF))),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Template = PlainTextBoxTemplate,
                CaretBrush = Brushes.Black,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_textBox, "EditorTextBox");
            _textBox.TextChanged += (_, _) =>
            {
                if (_textTarget is not null && Editor?.EditingText == _textTarget)
                {
                    Editor.UpdateEditingText(_textBox.Text);
                }
            };
            _textBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_textBox is not null && !_textBox.IsKeyboardFocusWithin && Editor?.EditingText == _textTarget)
                {
                    Editor?.EndTextEdit();
                }
            });
            _textBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Editor?.EndTextEdit();
                    e.Handled = true;
                }
            };
            AddVisualChild(_textBox);
            AddLogicalChild(_textBox);
        }

        var style = text.Style;
        _textBox.FontSize = style.FontSize * mapper.Scale;
        _textBox.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
        _textBox.FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal;
        _textBox.Foreground = new SolidColorBrush(OverlayDrawing.ToColor(style.Color));
        _textBox.Padding = new Thickness(Math.Max(0, (text.Padding * mapper.Scale) - 2));
        _textBox.TextAlignment = style.Alignment switch
        {
            Core.Overlay.TextAlignment.Center => System.Windows.TextAlignment.Center,
            Core.Overlay.TextAlignment.Right => System.Windows.TextAlignment.Right,
            Core.Overlay.TextAlignment.Justify => System.Windows.TextAlignment.Justify,
            _ => System.Windows.TextAlignment.Left,
        };
        if (editor.Fonts?.Face(style) is { } face)
        {
            var file = face.Typeface.FontUri;
            var family = face.Typeface.FamilyNames.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-US"), out var name) ? name : face.Typeface.FamilyNames.Values.FirstOrDefault() ?? style.FontFamily;
            _textBox.FontFamily = new FontFamily(new Uri(file, "."), "./" + System.IO.Path.GetFileName(file.LocalPath) + "#" + family);
        }

        if (_textTarget != target)
        {
            _textTarget = target;
            _textBox.Text = text.Text;
            _textBox.CaretIndex = _textBox.Text.Length;
            Dispatcher.BeginInvoke(() => _textBox?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }

        InvalidateMeasure();
        InvalidateArrange();
    }

    private void RemoveTextBox()
    {
        if (_textBox is null)
        {
            return;
        }

        var textBox = _textBox;
        _textBox = null;
        _textTarget = null;
        RemoveLogicalChild(textBox);
        RemoveVisualChild(textBox);
        InvalidateMeasure();
    }

    /// <summary>The page's mapper at the size it is shown, or null while its geometry is unknown.</summary>
    private PageCoordinateMapper? Mapper()
    {
        if (Editor?.GeometryOf(PageKey) is not { } geometry || ActualWidth <= 0)
        {
            return null;
        }

        return new PageCoordinateMapper(geometry, ActualWidth / geometry.DisplaySize.Width);
    }

    private OverlayItem? NewItem(PageEditorViewModel editor, PageCoordinateMapper mapper, Point end)
        => editor.GeometryOf(PageKey) is { } geometry
            ? editor.NewItem(editor.Tool, geometry, new ViewPoint(_start.X / mapper.Scale, _start.Y / mapper.Scale), new ViewPoint(end.X / mapper.Scale, end.Y / mapper.Scale))
            : null;

    /// <summary>A press on a grip of a selected object starts resizing, turning or moving a line end.</summary>
    private bool StartOnSelection(PageEditorViewModel editor, PageCoordinateMapper mapper, Point point)
    {
        foreach (var target in editor.ObjectsOn(PageKey).Where(o => o.IsSelected).Reverse())
        {
            if (target.Item is LineOverlay line)
            {
                foreach (var (end, isStart) in new[] { (line.Start, true), (line.End, false) })
                {
                    var shown = mapper.PdfToScreen(end);
                    if (Math.Abs(shown.X - point.X) <= GripHit && Math.Abs(shown.Y - point.Y) <= GripHit)
                    {
                        (_gesture, _target, _lineStart) = (Gesture.LineEnd, target, isStart);
                        return true;
                    }
                }

                continue;
            }

            if (BoxOf(target.Item) is { } box && box.ToView(mapper).HitHandle(new ViewPoint(point.X, point.Y), GripHit, RotateOffset) is { } handle)
            {
                (_target, _handle, _startBox) = (target, handle, box.ToView(mapper));
                _gesture = handle == BoxHandle.Rotate ? Gesture.Rotating : Gesture.Resizing;
                return true;
            }
        }

        return false;
    }

    private void StartOnObject(PageEditorViewModel editor, PageCoordinateMapper mapper, Point point, bool add)
    {
        var hit = ObjectAt(editor, mapper, point);
        if (hit is null)
        {
            if (!add)
            {
                editor.ClearSelection();
            }

            return;
        }

        if (add)
        {
            editor.Select(hit, add: true);
            return;
        }

        if (!hit.IsSelected)
        {
            editor.Select(hit);
        }

        _gesture = Gesture.Moving;
        _startItems = [.. editor.ObjectsOn(PageKey).Where(o => o.IsSelected).Select(o => (o, o.Item))];
    }

    private OverlayObjectViewModel? ObjectAt(PageEditorViewModel editor, PageCoordinateMapper mapper, Point point)
    {
        foreach (var target in editor.ObjectsOn(PageKey).Reverse())
        {
            var hit = target.Item switch
            {
                LineOverlay line => DistanceToSegment(point, mapper.PdfToScreen(line.Start), mapper.PdfToScreen(line.End)) <= Math.Max(5, (line.Stroke.Width * mapper.Scale / 2) + 3),
                _ => BoxOf(target.Item) is { } box && box.ToView(mapper).Contains(new ViewPoint(point.X, point.Y), 2),
            };
            if (hit)
            {
                return target;
            }
        }

        return null;
    }

    private void Move(PageEditorViewModel editor, PageCoordinateMapper mapper, Point point, bool snap)
    {
        if (editor.GeometryOf(PageKey) is not { } geometry || _startItems.Count == 0)
        {
            return;
        }

        var (dx, dy) = (point.X - _start.X, point.Y - _start.Y);
        _guides = [];
        if (snap)
        {
            var bounds = _startItems.Select(s => ViewBounds(s.Item, mapper)).Aggregate(Union);
            var moved = new ViewRect(bounds.X + dx, bounds.Y + dy, bounds.Width, bounds.Height);
            var others = editor.ObjectsOn(PageKey).Where(o => !o.IsSelected).Select(o => ViewBounds(o.Item, mapper)).ToArray();
            var result = OverlaySnapping.SnapMove(moved, new ViewRect(0, 0, ActualWidth, ActualHeight), others);
            (dx, dy, _guides) = (dx + result.DeltaX, dy + result.DeltaY, result.Guides);
        }

        var offset = new ViewPoint(dx / mapper.Scale, dy / mapper.Scale);
        foreach (var (target, item) in _startItems)
        {
            editor.Update(target, item.MoveToPage(geometry, geometry, item.PageIndex, offset));
        }
    }

    /// <summary>Snaps the dragged grip of an upright box to page and object edges.</summary>
    private ViewPoint SnapPointer(PageEditorViewModel editor, PageCoordinateMapper mapper, Point point, bool snap)
    {
        _guides = [];
        if (!snap || _startBox.Angle != 0 || _target is null)
        {
            return new ViewPoint(point.X, point.Y);
        }

        var page = new ViewRect(0, 0, ActualWidth, ActualHeight);
        var others = editor.ObjectsOn(PageKey).Where(o => o != _target).Select(o => ViewBounds(o.Item, mapper)).ToArray();
        var guides = new List<SnapGuide>();
        var (x, y) = (point.X, point.Y);
        if (_handle is not (BoxHandle.Top or BoxHandle.Bottom))
        {
            var (value, guide) = OverlaySnapping.SnapEdge(x, isVertical: true, page, others);
            x = value;
            if (guide is not null)
            {
                guides.Add(guide);
            }
        }

        if (_handle is not (BoxHandle.Left or BoxHandle.Right))
        {
            var (value, guide) = OverlaySnapping.SnapEdge(y, isVertical: false, page, others);
            y = value;
            if (guide is not null)
            {
                guides.Add(guide);
            }
        }

        _guides = guides;
        return new ViewPoint(x, y);
    }

    private static ViewRect ViewBounds(OverlayItem item, PageCoordinateMapper mapper)
    {
        if (item is LineOverlay line)
        {
            return ViewRect.FromCorners(mapper.PdfToScreen(line.Start), mapper.PdfToScreen(line.End));
        }

        return BoxOf(item)!.Value.ToView(mapper).Bounds;
    }

    private static ViewRect Union(ViewRect a, ViewRect b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        return new ViewRect(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }

    private Cursor CursorAt(PageEditorViewModel editor, PageCoordinateMapper mapper, Point point)
    {
        if (editor.Tool != OverlayTool.Select)
        {
            return Cursors.Cross;
        }

        foreach (var target in editor.ObjectsOn(PageKey).Where(o => o.IsSelected).Reverse())
        {
            if (BoxOf(target.Item) is { } box && box.ToView(mapper) is var view && view.HitHandle(new ViewPoint(point.X, point.Y), GripHit, RotateOffset) is { } handle)
            {
                return HandleCursor(handle, view.Angle);
            }

            if (target.Item is LineOverlay line && new[] { line.Start, line.End }.Select(mapper.PdfToScreen).Any(p => Math.Abs(p.X - point.X) <= GripHit && Math.Abs(p.Y - point.Y) <= GripHit))
            {
                return Cursors.Cross;
            }
        }

        return ObjectAt(editor, mapper, point) is not null ? Cursors.SizeAll : Cursors.Arrow;
    }

    private void DrawItem(DrawingContext context, PageEditorViewModel editor, OverlayItem item, PageCoordinateMapper mapper, double pixelsPerDip)
    {
        if (item is TextOverlay { Text: var text } textItem && string.IsNullOrWhiteSpace(text))
        {
            // An empty text box shows where typing will go.
            var view = textItem.Box.ToView(mapper);
            DrawPolygon(context, view.Corners(), EmptyTextPen);
            return;
        }

        if (editor.Fonts is null && item is TextOverlay)
        {
            return;
        }

        try
        {
            OverlayDrawing.Draw(context, item, mapper, editor.Fonts!, editor.Images, pixelsPerDip);
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or KeyNotFoundException or UnauthorizedAccessException)
        {
            // A picture that cannot be read (or a font gone missing) shows as an outlined box.
            if (BoxOf(item) is { } box)
            {
                DrawPolygon(context, box.ToView(mapper).Corners(), EmptyTextPen);
            }
        }
    }

    private static void DrawSelection(DrawingContext context, OverlayItem item, PageCoordinateMapper mapper)
    {
        if (item is LineOverlay line)
        {
            foreach (var end in new[] { line.Start, line.End })
            {
                var p = mapper.PdfToScreen(end);
                context.DrawRectangle(Brushes.White, SelectionPen, new Rect(p.X - (GripSize / 2), p.Y - (GripSize / 2), GripSize, GripSize));
            }

            return;
        }

        if (BoxOf(item) is not { } box)
        {
            return;
        }

        var view = box.ToView(mapper);
        DrawPolygon(context, view.Corners(), SelectionPen);
        var top = view.HandlePosition(BoxHandle.Top);
        var rotate = view.HandlePosition(BoxHandle.Rotate, RotateOffset);
        context.DrawLine(SelectionPen, new Point(top.X, top.Y), new Point(rotate.X, rotate.Y));
        context.DrawEllipse(Brushes.White, SelectionPen, new Point(rotate.X, rotate.Y), GripSize / 2, GripSize / 2);
        foreach (var handle in Enum.GetValues<BoxHandle>().Where(h => h != BoxHandle.Rotate))
        {
            var p = view.HandlePosition(handle);
            context.DrawRectangle(Brushes.White, SelectionPen, new Rect(p.X - (GripSize / 2), p.Y - (GripSize / 2), GripSize, GripSize));
        }
    }

    private static void DrawPolygon(DrawingContext context, IReadOnlyList<ViewPoint> corners, Pen pen)
    {
        for (var i = 0; i < corners.Count; i++)
        {
            var (a, b) = (corners[i], corners[(i + 1) % corners.Count]);
            context.DrawLine(pen, new Point(a.X, a.Y), new Point(b.X, b.Y));
        }
    }
}
