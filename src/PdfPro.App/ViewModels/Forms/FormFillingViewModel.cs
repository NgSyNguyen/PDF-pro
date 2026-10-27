using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfPro.App.Resources;
using PdfPro.App.Services.Forms;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Text;
using PdfPro.Operations;
using PdfPro.Operations.Forms;

namespace PdfPro.App.ViewModels.Forms;

/// <summary>
/// One place a field is filled on a page of the viewer: its box and its value, as shown now (the value given in the
/// session, else the file's). Setting a value gives the field that value (forms.fill, one undoable step).
/// </summary>
public sealed partial class FormWidgetViewModel : ObservableObject
{
    private readonly FormFillingViewModel _owner;

    internal FormWidgetViewModel(FormFillingViewModel owner, Guid pageKey, FormField field, FormWidget widget, int tabIndex)
    {
        _owner = owner;
        PageKey = pageKey;
        Field = field;
        Widget = widget;
        TabIndex = tabIndex;
    }

    public Guid PageKey { get; }

    public FormField Field { get; }

    public FormWidget Widget { get; }

    /// <summary>The order of the field in the form (Tab goes through fields in this order).</summary>
    public int TabIndex { get; }

    public FormFieldKind Kind => Field.Kind;

    public bool IsMultiline => Field.Flags.HasFlag(FormFieldFlags.Multiline);

    public bool IsPassword => Field.Flags.HasFlag(FormFieldFlags.Password);

    public bool IsEditableList => Field.Flags.HasFlag(FormFieldFlags.Edit);

    public bool IsMultiSelect => Field.Flags.HasFlag(FormFieldFlags.MultiSelect);

    /// <summary>The tool tip: /TU, else the field's name; "(bắt buộc)" for a required one.</summary>
    public string ToolTip => Field.IsRequired
        ? string.Format(CultureInfo.CurrentCulture, Strings.Forms_RequiredToolTip, Field.ToolTip ?? Field.Name)
        : Field.ToolTip ?? Field.Name;

    /// <summary>Text of a text field or combo box (an export value for a combo without typing).</summary>
    public string Text
    {
        get => _owner.ValueOf(Field).Value ?? string.Empty;
        set
        {
            var text = TextNormalizer.ToNfc(value ?? string.Empty);
            if (Field.MaxLength > 0 && text.Length > Field.MaxLength)
            {
                text = text[..Field.MaxLength];
            }

            if (text != Text)
            {
                _owner.Fill(new FormValue(Field.Name, text));
            }
        }
    }

    /// <summary>A check box is on, or this radio button is the chosen one.</summary>
    public bool IsChecked
    {
        get => Widget.OnState is { } on && _owner.ValueOf(Field).Value == on;
        set
        {
            if (value == IsChecked || Widget.OnState is not { } on)
            {
                return;
            }

            if (Kind == FormFieldKind.RadioGroup && !value && Field.Flags.HasFlag(FormFieldFlags.NoToggleToOff))
            {
                return;
            }

            _owner.Fill(new FormValue(Field.Name, value ? on : FormField.Off));
        }
    }

    /// <summary>The choices of a list box.</summary>
    public IReadOnlyList<string> Selected => _owner.ValueOf(Field).Selected ?? [];

    /// <summary>Chooses the items of a list box.</summary>
    public void Select(IReadOnlyList<string> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (!choices.SequenceEqual(Selected))
        {
            _owner.Fill(new FormValue(Field.Name, choices.FirstOrDefault() ?? string.Empty, [.. choices]));
        }
    }

    /// <summary>The value changed (filled here, on another widget of the field, undone…).</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(Selected));
    }
}

/// <summary>
/// Filling the form of the document in the viewer: the fields of the base document are read (in the background) and
/// shown over their widgets on every page drawn from it; a value typed or chosen becomes forms.fill (one undoable
/// step), written into the file when it is saved. Required fields still empty are listed before saving. XFA forms are
/// detected and explained. Must be used on the UI thread.
/// </summary>
public sealed partial class FormFillingViewModel : ObservableObject
{
    private readonly DocumentViewerViewModel _viewer;
    private readonly Func<string> _baseSourceId;
    private readonly Func<string?> _basePath;
    private readonly Func<PagePlan> _plan;
    private readonly Func<IPlanOperation, object, bool> _apply;
    private readonly OperationCatalog _operations;
    private readonly IFormSource _source;
    private readonly Func<Guid, PageGeometry?> _geometryOf;
    private readonly Dictionary<Guid, IReadOnlyList<FormWidgetViewModel>> _widgets = [];
    private FormSnapshot _form = FormSnapshot.None;
    private string? _readPath;
    private CancellationTokenSource? _reading;

    /// <param name="viewer">The viewer whose pages show the fields.</param>
    /// <param name="baseSourceId">The source the fields belong to (the opened file or its latest revision).</param>
    /// <param name="basePath">That source's file.</param>
    /// <param name="plan">The pages and the values given so far.</param>
    /// <param name="apply">Applies a page operation as one undoable step (false when refused).</param>
    /// <param name="operations">forms.fill.</param>
    /// <param name="source">Reads the form of a file.</param>
    /// <param name="geometryOf">The page space of a page as it will be written, when known.</param>
    public FormFillingViewModel(
        DocumentViewerViewModel viewer,
        Func<string> baseSourceId,
        Func<string?> basePath,
        Func<PagePlan> plan,
        Func<IPlanOperation, object, bool> apply,
        OperationCatalog operations,
        IFormSource source,
        Func<Guid, PageGeometry?> geometryOf)
    {
        _viewer = viewer;
        _baseSourceId = baseSourceId;
        _basePath = basePath;
        _plan = plan;
        _apply = apply;
        _operations = operations;
        _source = source;
        _geometryOf = geometryOf;
    }

    /// <summary>The fields were read or the pages changed: layers rebuild their editors.</summary>
    public event EventHandler? Changed;

    /// <summary>The editors take the mouse (another mode is not using the viewer).</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; } = true;

    /// <summary>The document has fields that can be filled.</summary>
    public bool HasFields => _form.Fields.Any(f => f.IsFillable);

    /// <summary>The fields of the form, as read from the file.</summary>
    public IReadOnlyList<FormField> Fields => _form.Fields;

    /// <summary>An explanation for XFA forms, or null.</summary>
    public string? XfaNotice => _form.Type switch
    {
        FormType.XfaFull => Strings.Forms_XfaFull,
        FormType.XfaForeground => Strings.Forms_XfaForeground,
        _ => null,
    };

    /// <summary>The document's form is XFA (<see cref="XfaNotice"/> explains it).</summary>
    public bool HasXfaNotice => XfaNotice is not null;

    /// <summary>Required fields still empty, by the name the user knows them by (tool tip, else name).</summary>
    public IReadOnlyList<string> MissingRequired
        => [.. _form.Fields.Where(f => f.IsRequired && f.IsFillable && IsEmpty(f)).Select(f => f.ToolTip ?? f.Name)];

    /// <summary>The editors of a page, in field order; empty for pages of other sources or before the form is read.</summary>
    public IReadOnlyList<FormWidgetViewModel> WidgetsOn(Guid pageKey)
    {
        if (_widgets.TryGetValue(pageKey, out var widgets))
        {
            return widgets;
        }

        var plan = _plan();
        if (plan.IndexOf(pageKey) is not (var index and >= 0) || plan[index] is not { IsBlank: false } page || page.SourceId != _baseSourceId())
        {
            return [];
        }

        var list = new List<FormWidgetViewModel>();
        for (var order = 0; order < _form.Fields.Count; order++)
        {
            var field = _form.Fields[order];
            if (!field.IsFillable)
            {
                continue;
            }

            list.AddRange(field.Widgets.Where(w => w.Page == page.SourcePageIndex && !w.Rect.IsEmpty).Select(w => new FormWidgetViewModel(this, pageKey, field, w, order)));
        }

        _widgets[pageKey] = list;
        return list;
    }

    /// <summary>The page space of a page as it will be written, when known.</summary>
    public PageGeometry? GeometryOf(Guid pageKey) => _geometryOf(pageKey);

    /// <summary>The value of a field as shown now: given in the session, else the file's.</summary>
    public FormValue ValueOf(FormField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return _plan().FormValues.TryGetValue(field.Name, out var value)
            ? value
            : new FormValue(field.Name, field.Value ?? string.Empty, field.Kind == FormFieldKind.ListBox ? field.Selected : null);
    }

    /// <summary>Gives a field a value (one undoable step).</summary>
    public bool Fill(FormValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _apply(_operations.Get<FillFormOperation>(), new FillFormParams([value]));
    }

    /// <summary>
    /// The pages or the values changed: the form is read again when the base file changed (a new revision), editors are
    /// rebuilt for changed pages and every value is shown again.
    /// </summary>
    public void Refresh()
    {
        if (_basePath() is { } path && path != _readPath)
        {
            _ = ReadAsync(path);
        }

        _widgets.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the form of the base file.</summary>
    public async Task ReadAsync(string path)
    {
        _reading?.Cancel();
        var reading = _reading = new CancellationTokenSource();
        _readPath = path;
        try
        {
            var form = await _source.ReadAsync(path, reading.Token);
            if (_reading != reading)
            {
                return;
            }

            _form = form;
            _widgets.Clear();
            OnPropertyChanged(nameof(HasFields));
            OnPropertyChanged(nameof(Fields));
            OnPropertyChanged(nameof(XfaNotice));
            OnPropertyChanged(nameof(HasXfaNotice));
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // A newer read took over.
        }
    }

    /// <summary>The values may have changed (undo, redo, a fill): every editor shows the current value.</summary>
    public void RefreshValues()
    {
        foreach (var widget in _widgets.Values.SelectMany(w => w))
        {
            widget.Refresh();
        }
    }

    private bool IsEmpty(FormField field)
    {
        var value = ValueOf(field);
        return field.Kind == FormFieldKind.ListBox
            ? (value.Selected ?? []).Count == 0
            : string.IsNullOrEmpty(value.Value) || (field.Kind is FormFieldKind.CheckBox or FormFieldKind.RadioGroup && value.Value == FormField.Off);
    }

    partial void OnIsActiveChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
}
