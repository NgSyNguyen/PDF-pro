using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Operations;
using PdfPro.Operations.Forms;

namespace PdfPro.App.ViewModels.Forms;

/// <summary>What the mouse places in the form designer (Select picks, moves and resizes fields).</summary>
public enum FormDesignTool
{
    Select,
    Text,
    CheckBox,
    RadioButton,
    ComboBox,
    ListBox,
    Button,
    Signature,
}

/// <summary>Which corner of a field's box is dragged to resize it, as seen on the screen.</summary>
public enum FormDesignCorner
{
    None,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>The pointer the viewer shows over a place of the designer.</summary>
public enum FormDesignCursor
{
    Default,
    Place,

    /// <summary>Over a proposal: a click takes or drops it.</summary>
    Toggle,
    Move,
    ResizeDiagonal,
    ResizeAntiDiagonal,
}

/// <summary>A widget of a field shown on a page of the viewer: the field as read and the widget's place among its widgets.</summary>
public sealed record DesignWidget(Guid PageKey, FormField Field, int Index)
{
    public FormWidget Widget => Field.Widgets[Index];

    /// <summary>The same widget of the same field (by name), also once the form has been read again.</summary>
    public bool IsSame(DesignWidget? other) => other is not null && other.PageKey == PageKey && other.Field.Name == Field.Name && other.Index == Index;
}

/// <summary>
/// "Thiết kế biểu mẫu": the fields of the document's form as frames over the pages. A tool places a new field by
/// dragging its box (a click places one of the usual size; a radio button joins the radio group selected); Select
/// picks a field, drags it or one of its corners, Delete removes it; the properties panel changes it. Every change is
/// written by the window as the next revision (forms.design), one undoable step: fields keep what other editors wrote
/// in them, as they are changed in place. Must be used on the UI thread.
/// </summary>
public sealed partial class FormDesignerViewModel : ObservableObject
{
    /// <summary>Half the size of a corner handle, in screen pixels.</summary>
    public const double HandlePixels = 5;

    /// <summary>A drag shorter than this (points) places a field of the usual size, or does not move one.</summary>
    public const double DragThreshold = 3;

    /// <summary>The smallest field box, in points.</summary>
    public const double MinSize = 6;

    private readonly DocumentViewerViewModel _viewer;
    private readonly FormFillingViewModel _forms;
    private readonly Func<Guid, PageGeometry?> _geometryOf;
    private readonly Func<ContentChangeRequest, bool> _requestContentChange;
    private readonly OperationCatalog _operations;
    private Drag? _drag;
    private (string Name, int Index)? _reselect;

    /// <param name="viewer">The viewer whose pages show the fields.</param>
    /// <param name="forms">The form as read (fields, values, the pages of its file).</param>
    /// <param name="geometryOf">The page space of a page as it will be written, when known.</param>
    /// <param name="requestContentChange">Asks the window to write the next revision; false when the document cannot change now.</param>
    /// <param name="operations">forms.design.</param>
    public FormDesignerViewModel(
        DocumentViewerViewModel viewer,
        FormFillingViewModel forms,
        Func<Guid, PageGeometry?> geometryOf,
        Func<ContentChangeRequest, bool> requestContentChange,
        OperationCatalog operations)
    {
        _viewer = viewer;
        _forms = forms;
        _geometryOf = geometryOf;
        _requestContentChange = requestContentChange;
        _operations = operations;
        _forms.Changed += (_, _) => OnFormRead();
    }

    /// <summary>Fields, the selection or a drag changed: pages redraw their frames.</summary>
    public event EventHandler? Changed;

    /// <summary>The mode is on (the viewer's mouse goes to it).</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>What the mouse places next.</summary>
    [ObservableProperty]
    public partial FormDesignTool Tool { get; set; }

    /// <summary>The selected widget, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial DesignWidget? Selected { get; private set; }

    /// <summary>The selected field's properties, or null.</summary>
    [ObservableProperty]
    public partial FormFieldPropertiesViewModel? Properties { get; private set; }

    public bool HasSelection => Selected is not null;

    /// <summary>A field is being placed, moved or resized (the mouse is captured for it).</summary>
    public bool IsDragging => _drag is not null;

    /// <summary>The widgets of every field on a page (in field order), when the page is one of the form's file.</summary>
    public IReadOnlyList<DesignWidget> WidgetsOn(Guid pageKey)
    {
        if (!IsActive || _forms.SourcePageOf(pageKey) is not { } sourcePage)
        {
            return [];
        }

        var list = new List<DesignWidget>();
        foreach (var field in _forms.Fields)
        {
            for (var i = 0; i < field.Widgets.Count; i++)
            {
                if (field.Widgets[i] is { Rect.IsEmpty: false } widget && widget.Page == sourcePage)
                {
                    list.Add(new DesignWidget(pageKey, field, i));
                }
            }
        }

        return list;
    }

    /// <summary>A widget's box as shown now: moved or resized by the drag in progress.</summary>
    public PdfRect Shown(DesignWidget widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        return _drag is { Kind: not DragKind.Place } drag && drag.Widget!.IsSame(widget) ? Dragged(drag) : widget.Widget.Rect;
    }

    /// <summary>The box of the field being placed on a page, or null.</summary>
    public PdfRect? Preview(Guid pageKey)
        => _drag is { Kind: DragKind.Place } drag && drag.PageKey == pageKey ? PlacedBox(drag) : null;

    /// <summary>The page space of a page as it will be written, when known.</summary>
    public PageGeometry? GeometryOf(Guid pageKey) => _geometryOf(pageKey);

    /// <summary>The pointer to show at a viewport point.</summary>
    public FormDesignCursor CursorAt(ViewPoint viewport)
    {
        if (!IsActive)
        {
            return FormDesignCursor.Default;
        }

        if (Tool != FormDesignTool.Select)
        {
            return FormDesignCursor.Place;
        }

        if (Locate(viewport) is not { } found)
        {
            return FormDesignCursor.Default;
        }

        if (SuggestionAt(found.Page.Key, found.Point) is not null)
        {
            return FormDesignCursor.Toggle;
        }

        return CornerAt(found.Page.Key, viewport) switch
        {
            FormDesignCorner.TopLeft or FormDesignCorner.BottomRight => FormDesignCursor.ResizeDiagonal,
            FormDesignCorner.TopRight or FormDesignCorner.BottomLeft => FormDesignCursor.ResizeAntiDiagonal,
            _ => Pick(found.Page.Key, found.Point) is null ? FormDesignCursor.Default : FormDesignCursor.Move,
        };
    }

    /// <summary>
    /// Mouse pressed: with a tool, starts placing a field; otherwise grabs a corner of the selected field, or selects the
    /// field under the mouse and starts moving it. False when the mode is off.
    /// </summary>
    public bool Press(ViewPoint viewport)
    {
        if (!IsActive)
        {
            return false;
        }

        _drag = null;
        if (Locate(viewport) is not { } found)
        {
            Select(null);
            return true;
        }

        if (Tool != FormDesignTool.Select)
        {
            _drag = new Drag(DragKind.Place, found.Page.Key, found.Index, found.Point, found.Point, null, FormDesignCorner.None);
        }
        else if (ToggleSuggestionAt(found.Page.Key, found.Point))
        {
            return true;
        }
        else if (CornerAt(found.Page.Key, viewport) is var corner and not FormDesignCorner.None)
        {
            _drag = new Drag(DragKind.Resize, found.Page.Key, found.Index, found.Point, found.Point, Selected, corner);
        }
        else
        {
            var picked = Pick(found.Page.Key, found.Point);
            Select(picked);
            if (picked is not null)
            {
                _drag = new Drag(DragKind.Move, found.Page.Key, found.Index, found.Point, found.Point, picked, FormDesignCorner.None);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void DragTo(ViewPoint viewport)
    {
        if (_drag is not { } drag || PointOn(drag.PageIndex, viewport) is not { } point)
        {
            return;
        }

        _drag = drag with { Current = point };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The mouse was released: the field is placed, moved or resized (the next revision).</summary>
    public void Release()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        switch (drag.Kind)
        {
            case DragKind.Place:
                Place(drag);
                break;
            case DragKind.Move or DragKind.Resize when drag.Widget is { } widget && HasMoved(drag):
                var box = Dragged(drag);
                if (!box.Equals(widget.Widget.Rect))
                {
                    _reselect = (widget.Field.Name, widget.Index);
                    Request(new DesignFormParams(Widgets: [new FormWidgetChange(widget.Field.Name, widget.Index, Round(box))]));
                }

                break;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Esc: drops the drag, then the tool, then the selection. False when there was nothing to drop.</summary>
    public bool Escape()
    {
        if (_drag is not null)
        {
            _drag = null;
        }
        else if (Tool != FormDesignTool.Select)
        {
            Tool = FormDesignTool.Select;
        }
        else if (Selected is not null)
        {
            Select(null);
        }
        else
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Removes the selected widget: the whole field when it is its only one (the next revision).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void Delete()
    {
        if (Selected is not { } selected)
        {
            return;
        }

        var parameters = selected.Field.Widgets.Count == 1
            ? new DesignFormParams(Remove: [selected.Field.Name])
            : new DesignFormParams(Widgets: [new FormWidgetChange(selected.Field.Name, selected.Index, null)]);
        if (Request(parameters))
        {
            Select(null);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The pages changed (an edit, undo, redo): a selection no longer shown is dropped.</summary>
    public void Refresh()
    {
        if (Selected is { } selected && _forms.SourcePageOf(selected.PageKey) is null)
        {
            Select(null);
        }

        DropSuggestions();
        _drag = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes the properties of the selected field (the next revision).</summary>
    internal bool ApplyProperties(FormFieldUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        _reselect = (update.Properties.Name, Selected?.Index ?? 0);
        return Request(new DesignFormParams(Update: [update]));
    }

    partial void OnIsActiveChanged(bool value)
    {
        if (!value)
        {
            _drag = null;
            Tool = FormDesignTool.Select;
            Select(null);
            DropSuggestions();
        }

        DetectCommand.NotifyCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static PdfRect Round(PdfRect rect)
        => new(Math.Round(rect.Left, 2), Math.Round(rect.Bottom, 2), Math.Round(rect.Right, 2), Math.Round(rect.Top, 2));

    private static bool HasMoved(Drag drag)
        => Math.Abs(drag.Current.X - drag.Start.X) >= DragThreshold || Math.Abs(drag.Current.Y - drag.Start.Y) >= DragThreshold;

    /// <summary>The usual size of a new field (width, height on the screen, in points).</summary>
    private static (double Width, double Height) UsualSize(FormDesignTool tool) => tool switch
    {
        FormDesignTool.CheckBox or FormDesignTool.RadioButton => (14, 14),
        FormDesignTool.ListBox => (150, 60),
        FormDesignTool.Button => (80, 24),
        FormDesignTool.Signature => (180, 50),
        _ => (150, 20),
    };

    private void Select(DesignWidget? widget)
    {
        if (widget is null ? Selected is null : widget.IsSame(Selected) && ReferenceEquals(widget.Field, Selected!.Field))
        {
            return;
        }

        Selected = widget;
        Properties = widget is null
            ? null
            : new FormFieldPropertiesViewModel(widget.Field, _forms.ValueOf(widget.Field).Value, _forms.Fields.Where(f => f.Name != widget.Field.Name).Select(f => f.Name).ToList(), ApplyProperties);
    }

    /// <summary>The fields were read again (a new revision): the selection follows its field by name.</summary>
    private void OnFormRead()
    {
        if (!IsActive)
        {
            return;
        }

        var wanted = _reselect ?? (Selected is { } selected ? (selected.Field.Name, selected.Index) : null);
        var pageKey = Selected?.PageKey;
        DesignWidget? found = null;
        if (wanted is { } target && _forms.Fields.FirstOrDefault(f => f.Name == target.Name) is { } field)
        {
            var index = Math.Clamp(target.Index, 0, Math.Max(0, field.Widgets.Count - 1));
            found = _viewer.Document.Pages.Select(p => p.Key)
                .Where(key => field.Widgets.Count > index && _forms.SourcePageOf(key) == field.Widgets[index].Page)
                .OrderBy(key => key == pageKey ? 0 : 1)
                .Select(key => new DesignWidget(key, field, index))
                .FirstOrDefault();
        }

        // A field changed or placed shows up once the new revision has been read (the form may still be the old one).
        if (found is null && _reselect is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // The same field as read before keeps its panel (and what is being typed in it).
        _reselect = null;
        Select(found);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Place(Drag drag)
    {
        var box = PlacedBox(drag);
        if (_viewer.Document.IndexOf(drag.PageKey) is not (var index and >= 0))
        {
            return;
        }

        var radioGroup = Tool == FormDesignTool.RadioButton && Selected is { Field.Kind: FormFieldKind.RadioGroup } group ? group.Field : null;
        var spec = NewField(Tool, index, Round(box), radioGroup);
        _reselect = (spec.Name, radioGroup?.Widgets.Count ?? 0);
        Tool = FormDesignTool.Select;
        if (!Request(new DesignFormParams(Add: [spec])))
        {
            _reselect = null;
        }
    }

    /// <summary>A new field of a tool: the next free name of its kind, the usual starting properties.</summary>
    private FormFieldSpec NewField(FormDesignTool tool, int page, PdfRect box, FormField? radioGroup)
    {
        if (radioGroup is not null)
        {
            var states = radioGroup.Widgets.Select(w => w.OnState).OfType<string>().ToHashSet(StringComparer.Ordinal);
            return new FormFieldSpec(radioGroup.Name, FormFieldKind.RadioGroup, page, box, OnState: FreeName("Choice", states, separator: string.Empty));
        }

        var names = _forms.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var choices = Enumerable.Range(1, 3).Select(i => string.Format(CultureInfo.CurrentCulture, Strings.FormDesigner_DefaultChoice, i)).Select(c => new FormOption(c, c)).ToList();
        return tool switch
        {
            FormDesignTool.CheckBox => new FormFieldSpec(FreeName(Strings.FormDesigner_Name_CheckBox, names), FormFieldKind.CheckBox, page, box),
            FormDesignTool.RadioButton => new FormFieldSpec(FreeName(Strings.FormDesigner_Name_RadioGroup, names), FormFieldKind.RadioGroup, page, box, OnState: "Choice1"),
            FormDesignTool.ComboBox => new FormFieldSpec(FreeName(Strings.FormDesigner_Name_ComboBox, names), FormFieldKind.ComboBox, page, box, Options: choices),
            FormDesignTool.ListBox => new FormFieldSpec(FreeName(Strings.FormDesigner_Name_ListBox, names), FormFieldKind.ListBox, page, box, Options: choices),
            FormDesignTool.Button => new FormFieldSpec(FreeName(Strings.FormDesigner_Name_Button, names), FormFieldKind.PushButton, page, box, Caption: Strings.FormDesigner_DefaultCaption),
            FormDesignTool.Signature => new FormFieldSpec(FreeName(Strings.FormDesigner_Name_Signature, names), FormFieldKind.Signature, page, box),
            _ => new FormFieldSpec(FreeName(Strings.FormDesigner_Name_Text, names), FormFieldKind.Text, page, box),
        };
    }

    private static string FreeName(string prefix, HashSet<string> taken, string separator = " ")
    {
        for (var i = 1; ; i++)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"{prefix}{separator}{i}");
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }

    private bool Request(DesignFormParams parameters)
    {
        var request = new ContentChangeRequest(_operations.Get<DesignFormOperation>(), parameters, HistoryDescriptions.Describe(parameters), applied =>
        {
            if (!applied)
            {
                _reselect = null;
            }
        });
        return _requestContentChange(request);
    }

    /// <summary>The box being placed: as dragged, or the usual size from the press point when it was a click.</summary>
    private PdfRect PlacedBox(Drag drag)
    {
        if (HasMoved(drag))
        {
            return Normalize(drag.Start, drag.Current, MinSize);
        }

        var geometry = _geometryOf(drag.PageKey) ?? new PageGeometry(new PdfRect(0, 0, 612, 792), 0);
        var mapper = new PageCoordinateMapper(geometry, 1);
        var (width, height) = UsualSize(Tool);
        var at = mapper.PdfToScreen(drag.Start);
        return mapper.ScreenRectToPdf(new ViewRect(at.X, at.Y, width, height));
    }

    /// <summary>The selected widget's box moved or resized by a drag.</summary>
    private static PdfRect Dragged(Drag drag)
    {
        var rect = drag.Widget!.Widget.Rect;
        var dx = drag.Current.X - drag.Start.X;
        var dy = drag.Current.Y - drag.Start.Y;
        if (drag.Kind == DragKind.Move)
        {
            return new PdfRect(rect.Left + dx, rect.Bottom + dy, rect.Right + dx, rect.Top + dy);
        }

        // The grabbed corner follows the mouse; the opposite one stays.
        var (fixedX, fixedY) = OppositeCorner(rect, drag.Start);
        return Normalize(new PdfPoint(fixedX, fixedY), drag.Current, MinSize);
    }

    /// <summary>The corner across from the one grabbed (the press point is on the grabbed one).</summary>
    private static (double X, double Y) OppositeCorner(PdfRect rect, PdfPoint grabbed)
        => (Math.Abs(grabbed.X - rect.Left) < Math.Abs(grabbed.X - rect.Right) ? rect.Right : rect.Left,
            Math.Abs(grabbed.Y - rect.Bottom) < Math.Abs(grabbed.Y - rect.Top) ? rect.Top : rect.Bottom);

    private static PdfRect Normalize(PdfPoint a, PdfPoint b, double minimum)
    {
        var left = Math.Min(a.X, b.X);
        var bottom = Math.Min(a.Y, b.Y);
        return new PdfRect(left, bottom, left + Math.Max(Math.Abs(a.X - b.X), minimum), bottom + Math.Max(Math.Abs(a.Y - b.Y), minimum));
    }

    /// <summary>The topmost widget under a point on a page (the selected one first), or null.</summary>
    private DesignWidget? Pick(Guid pageKey, PdfPoint point)
    {
        var tolerance = 2 / _viewer.Layout.Scale;
        bool Contains(DesignWidget w)
        {
            var box = w.Widget.Rect;
            return point.X >= box.Left - tolerance && point.X <= box.Right + tolerance && point.Y >= box.Bottom - tolerance && point.Y <= box.Top + tolerance;
        }

        if (Selected is { } selected && selected.PageKey == pageKey && Contains(selected))
        {
            return selected;
        }

        return WidgetsOn(pageKey).LastOrDefault(Contains);
    }

    /// <summary>The corner of the selected widget under a viewport point, as seen on the screen.</summary>
    private FormDesignCorner CornerAt(Guid pageKey, ViewPoint viewport)
    {
        if (Selected is not { } selected || selected.PageKey != pageKey || _viewer.Document.IndexOf(pageKey) is not (var index and >= 0)
            || _geometryOf(pageKey) is not { } geometry)
        {
            return FormDesignCorner.None;
        }

        var page = _viewer.Layout.GetPageBounds(index);
        var box = new PageCoordinateMapper(geometry, _viewer.Layout.Scale).PdfRectToScreen(selected.Widget.Rect);
        var x = viewport.X + _viewer.ScrollOffset.X - page.X;
        var y = viewport.Y + _viewer.ScrollOffset.Y - page.Y;
        bool Near(double a, double b) => Math.Abs(a - b) <= HandlePixels;
        return (Near(x, box.X), Near(x, box.X + box.Width), Near(y, box.Y), Near(y, box.Y + box.Height)) switch
        {
            (true, _, true, _) => FormDesignCorner.TopLeft,
            (_, true, true, _) => FormDesignCorner.TopRight,
            (true, _, _, true) => FormDesignCorner.BottomLeft,
            (_, true, _, true) => FormDesignCorner.BottomRight,
            _ => FormDesignCorner.None,
        };
    }

    /// <summary>The page under a viewport point and the point on it in PDF space, when its geometry is known.</summary>
    private (int Index, VirtualPage Page, PdfPoint Point)? Locate(ViewPoint viewport)
    {
        var extent = new ViewPoint(viewport.X + _viewer.ScrollOffset.X, viewport.Y + _viewer.ScrollOffset.Y);
        if (_viewer.Layout.GetPageAt(extent) is not { } index || index < 0 || index >= _viewer.PageCount)
        {
            return null;
        }

        return PointOn(index, viewport) is { } point ? (index, _viewer.Document.Pages[index], point) : null;
    }

    /// <summary>A viewport point on page <paramref name="index"/> (even outside it), in its PDF space.</summary>
    private PdfPoint? PointOn(int index, ViewPoint viewport)
    {
        if (index < 0 || index >= _viewer.PageCount || _geometryOf(_viewer.Document.Pages[index].Key) is not { } geometry)
        {
            return null;
        }

        var bounds = _viewer.Layout.GetPageBounds(index);
        var mapper = new PageCoordinateMapper(geometry, _viewer.Layout.Scale);
        return mapper.ScreenToPdf(new ViewPoint(viewport.X + _viewer.ScrollOffset.X - bounds.X, viewport.Y + _viewer.ScrollOffset.Y - bounds.Y));
    }

    private enum DragKind
    {
        Place,
        Move,
        Resize,
    }

    private sealed record Drag(DragKind Kind, Guid PageKey, int PageIndex, PdfPoint Start, PdfPoint Current, DesignWidget? Widget, FormDesignCorner Corner);
}
