using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.Resources;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Operations.Forms;

namespace PdfPro.App.ViewModels.Forms;

/// <summary>A value offered in a list of the properties panel, with what is shown for it.</summary>
public sealed record FieldPropertyChoice<T>(T Value, string Label);

/// <summary>
/// The properties panel of the field selected in the form designer: name, tool tip, required, read-only, text look
/// (font, size, alignment), length, number or date format, choices, starting value, a button's caption. "Áp dụng"
/// writes them in place (forms.design Update, the next revision); mistakes are explained before that.
/// </summary>
public sealed partial class FormFieldPropertiesViewModel : ObservableObject
{
    private readonly FormField _field;
    private readonly string? _value;
    private readonly IReadOnlyCollection<string> _otherNames;
    private readonly Func<FormFieldUpdate, bool> _apply;
    private bool _loading;

    /// <param name="field">The field as read.</param>
    /// <param name="value">Its value as shown now (given in the session, else the file's).</param>
    /// <param name="otherNames">The names of the other fields.</param>
    /// <param name="apply">Writes the change (false when the document cannot change now).</param>
    public FormFieldPropertiesViewModel(FormField field, string? value, IReadOnlyCollection<string> otherNames, Func<FormFieldUpdate, bool> apply)
    {
        ArgumentNullException.ThrowIfNull(field);
        _field = field;
        _value = value;
        _otherNames = otherNames;
        _apply = apply;
        Load();
    }

    public FormFieldKind Kind => _field.Kind;

    /// <summary>"Ô văn bản", "Nhóm nút chọn"…</summary>
    public string KindText => Kind switch
    {
        FormFieldKind.Text => Strings.FieldProps_Kind_Text,
        FormFieldKind.CheckBox => Strings.FieldProps_Kind_CheckBox,
        FormFieldKind.RadioGroup => Strings.FieldProps_Kind_RadioGroup,
        FormFieldKind.ComboBox => Strings.FieldProps_Kind_ComboBox,
        FormFieldKind.ListBox => Strings.FieldProps_Kind_ListBox,
        FormFieldKind.PushButton => Strings.FieldProps_Kind_PushButton,
        _ => Strings.FieldProps_Kind_Signature,
    };

    public bool IsText => Kind == FormFieldKind.Text;

    public bool IsCheckBox => Kind == FormFieldKind.CheckBox;

    public bool IsRadio => Kind == FormFieldKind.RadioGroup;

    public bool IsComboBox => Kind == FormFieldKind.ComboBox;

    public bool IsListBox => Kind == FormFieldKind.ListBox;

    public bool HasOptions => Kind is FormFieldKind.ComboBox or FormFieldKind.ListBox;

    public bool IsButton => Kind == FormFieldKind.PushButton;

    /// <summary>The field shows text (font, size, alignment apply).</summary>
    public bool HasTextLook => Kind is FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox or FormFieldKind.PushButton;

    /// <summary>The field has a value someone fills (required, starting value apply).</summary>
    public bool HasValue => Kind is not (FormFieldKind.PushButton or FormFieldKind.Signature);

    /// <summary>The starting value is picked from a list (radio buttons, combo and list boxes).</summary>
    public bool HasDefaultChoice => Kind is FormFieldKind.RadioGroup or FormFieldKind.ComboBox or FormFieldKind.ListBox;

    public IReadOnlyList<string> FontFamilies { get; } = Core.Text.FontFamilies.Bundled;

    public IReadOnlyList<FieldPropertyChoice<FormTextAlignment>> Alignments { get; } =
    [
        new(FormTextAlignment.Left, Strings.FieldProps_Align_Left),
        new(FormTextAlignment.Center, Strings.FieldProps_Align_Center),
        new(FormTextAlignment.Right, Strings.FieldProps_Align_Right),
    ];

    public IReadOnlyList<FieldPropertyChoice<FormFormatKind>> FormatKinds { get; } =
    [
        new(FormFormatKind.None, Strings.FieldProps_Format_None),
        new(FormFormatKind.Number, Strings.FieldProps_Format_Number),
        new(FormFormatKind.Date, Strings.FieldProps_Format_Date),
    ];

    public IReadOnlyList<string> DatePatterns { get; } = FormTextFormat.DatePatterns;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ToolTip { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRequired { get; set; }

    [ObservableProperty]
    public partial bool IsReadOnly { get; set; }

    [ObservableProperty]
    public partial bool IsMultiline { get; set; }

    [ObservableProperty]
    public partial bool IsPassword { get; set; }

    [ObservableProperty]
    public partial int MaxLength { get; set; }

    [ObservableProperty]
    public partial string FontFamily { get; set; } = Core.Text.FontFamilies.Default;

    /// <summary>Text size in points; 0 fits the box.</summary>
    [ObservableProperty]
    public partial double FontSize { get; set; }

    [ObservableProperty]
    public partial FormTextAlignment Alignment { get; set; }

    /// <summary>The starting text of a text field.</summary>
    [ObservableProperty]
    public partial string DefaultText { get; set; } = string.Empty;

    /// <summary>A check box starts ticked.</summary>
    [ObservableProperty]
    public partial bool IsCheckedByDefault { get; set; }

    /// <summary>The starting choice of radio buttons, a combo or list box ("" for none).</summary>
    [ObservableProperty]
    public partial string DefaultChoice { get; set; } = string.Empty;

    /// <summary>The choices of a combo or list box, one per line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Choices))]
    public partial string OptionsText { get; set; } = string.Empty;

    /// <summary>A combo box takes typed values too.</summary>
    [ObservableProperty]
    public partial bool IsEditable { get; set; }

    /// <summary>Several items of a list box can be chosen.</summary>
    [ObservableProperty]
    public partial bool IsMultiSelect { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNumber), nameof(IsDate))]
    public partial FormFormatKind FormatKind { get; set; }

    public bool IsNumber => FormatKind == FormFormatKind.Number;

    public bool IsDate => FormatKind == FormFormatKind.Date;

    [ObservableProperty]
    public partial int Decimals { get; set; }

    /// <summary>Numbers are written 1.234,56 (else 1,234.56).</summary>
    [ObservableProperty]
    public partial bool UsesDotComma { get; set; } = true;

    [ObservableProperty]
    public partial string DatePattern { get; set; } = FormTextFormat.DefaultDatePattern;

    /// <summary>The words of a push button.</summary>
    [ObservableProperty]
    public partial string Caption { get; set; } = string.Empty;

    /// <summary>Something was changed since the field was read.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(ResetCommand))]
    public partial bool IsDirty { get; private set; }

    /// <summary>What is wrong with the properties (in the user's language), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>The choices a starting value is picked from (the first one is "none").</summary>
    public IReadOnlyList<FieldPropertyChoice<string>> Choices
    {
        get
        {
            var values = Kind == FormFieldKind.RadioGroup
                ? _field.Widgets.Select(w => w.OnState).OfType<string>().Distinct(StringComparer.Ordinal).Select(v => new FieldPropertyChoice<string>(v, v))
                : ParseOptions().Select(o => new FieldPropertyChoice<string>(o.Export, o.Display));
            return [new FieldPropertyChoice<string>(string.Empty, Strings.FieldProps_NoChoice), .. values];
        }
    }

    /// <summary>Writes the properties into the field (the next revision).</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (Build() is { } update)
        {
            _apply(update);
        }
    }

    private bool CanApply() => IsDirty && Error is null;

    /// <summary>Back to the field as read.</summary>
    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Reset() => Load();

    /// <summary>The change to write, or null while something is wrong.</summary>
    public FormFieldUpdate? Build()
    {
        if (Check() is not null)
        {
            return null;
        }

        var flags = _field.Flags;
        flags = Set(flags, FormFieldFlags.Required, IsRequired && HasValue);
        flags = Set(flags, FormFieldFlags.ReadOnly, IsReadOnly);
        if (IsText)
        {
            flags = Set(flags, FormFieldFlags.Multiline, IsMultiline);
            flags = Set(flags, FormFieldFlags.Password, IsPassword);
        }

        flags = IsComboBox ? Set(flags, FormFieldFlags.Edit, IsEditable) : flags;
        flags = IsListBox ? Set(flags, FormFieldFlags.MultiSelect, IsMultiSelect) : flags;
        var format = IsText && FormatKind != FormFormatKind.None
            ? new FormTextFormat(FormatKind, Decimals, UsesDotComma ? NumberSeparators.DotComma : NumberSeparators.CommaDot, DatePattern)
            : null;
        string? value = Kind switch
        {
            FormFieldKind.Text => Normalized(format),
            FormFieldKind.CheckBox => IsCheckedByDefault ? _field.Widgets.Select(w => w.OnState).OfType<string>().FirstOrDefault() ?? FormFieldSpec.DefaultOnState : FormField.Off,
            FormFieldKind.RadioGroup => DefaultChoice.Length > 0 ? DefaultChoice : FormField.Off,
            FormFieldKind.ComboBox or FormFieldKind.ListBox => DefaultChoice.Length > 0 ? DefaultChoice : null,
            _ => null,
        };
        var spec = new FormFieldSpec(
            TextNormalizer.ToNfc(Name.Trim()),
            Kind,
            0,
            _field.Widgets.FirstOrDefault()?.Rect is { IsEmpty: false } rect ? rect : new PdfRect(0, 0, 10, 10),
            flags,
            value,
            HasOptions ? ParseOptions() : null,
            string.IsNullOrWhiteSpace(ToolTip) ? null : TextNormalizer.ToNfc(ToolTip.Trim()),
            FontSize,
            Alignment,
            IsText ? MaxLength : 0,
            Caption: IsButton ? TextNormalizer.ToNfc(Caption) : null,
            Format: format,
            FontFamily: FontFamily);
        return new FormFieldUpdate(_field.Name, spec);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(IsDirty) or nameof(Error) or nameof(HasError) or nameof(Choices) or nameof(IsNumber) or nameof(IsDate))
        {
            return;
        }

        IsDirty = true;
        Error = Check();
    }

    private static FormFieldFlags Set(FormFieldFlags flags, FormFieldFlags flag, bool on) => on ? flags | flag : flags & ~flag;

    private void Load()
    {
        _loading = true;
        Name = _field.Name;
        ToolTip = _field.ToolTip ?? string.Empty;
        IsRequired = _field.IsRequired;
        IsReadOnly = _field.IsReadOnly;
        IsMultiline = _field.Flags.HasFlag(FormFieldFlags.Multiline);
        IsPassword = _field.Flags.HasFlag(FormFieldFlags.Password);
        MaxLength = _field.MaxLength;
        FontFamily = _field.FontFamily ?? Core.Text.FontFamilies.Default;
        FontSize = _field.FontSize;
        Alignment = _field.Alignment;
        IsEditable = _field.Flags.HasFlag(FormFieldFlags.Edit);
        IsMultiSelect = _field.Flags.HasFlag(FormFieldFlags.MultiSelect);
        OptionsText = string.Join(Environment.NewLine, _field.Options.Select(o => o.Display));
        FormatKind = _field.Format?.Kind ?? FormFormatKind.None;
        Decimals = _field.Format?.Decimals ?? 0;
        UsesDotComma = _field.Format?.Separators != NumberSeparators.CommaDot;
        DatePattern = _field.Format is { Kind: FormFormatKind.Date } date && FormTextFormat.DatePatterns.Contains(date.DatePattern) ? date.DatePattern : FormTextFormat.DefaultDatePattern;
        Caption = IsButton ? _field.Value ?? string.Empty : string.Empty;
        DefaultText = IsText ? _field.Format?.Display(_value) ?? _value ?? string.Empty : string.Empty;
        IsCheckedByDefault = IsCheckBox && _value is { } on && on != FormField.Off && on.Length > 0;
        DefaultChoice = HasDefaultChoice && _value is { } choice && choice != FormField.Off ? choice : string.Empty;
        _loading = false;
        IsDirty = false;
        Error = null;
    }

    /// <summary>The starting text as kept: a number plain, a date in its pattern (null when it does not fit).</summary>
    private string? Normalized(FormTextFormat? format)
    {
        var text = TextNormalizer.ToNfc(DefaultText);
        if (format is null)
        {
            return text;
        }

        return format.TryNormalize(text, out var value) ? value : null;
    }

    /// <summary>The choices typed, one per line; a line still showing an existing choice keeps its export value.</summary>
    private List<FormOption> ParseOptions()
    {
        var options = new List<FormOption>();
        foreach (var line in OptionsText.Split('\n').Select(l => TextNormalizer.ToNfc(l.Trim())).Where(l => l.Length > 0))
        {
            options.Add(_field.Options.FirstOrDefault(o => o.Display == line) ?? new FormOption(line, line));
        }

        return options;
    }

    private string? Check()
    {
        var name = Name.Trim();
        if (name.Length == 0 || name.Length > 255 || name.Split('.').Any(string.IsNullOrWhiteSpace))
        {
            return Strings.FieldProps_Error_Name;
        }

        if (_otherNames.Any(other => other == name || IsAncestor(other, name) || IsAncestor(name, other)))
        {
            return Strings.FieldProps_Error_NameTaken;
        }

        if (FontSize is < 0 or > 200 || MaxLength < 0 || Decimals is < 0 or > FormTextFormat.MaxDecimals)
        {
            return Strings.FieldProps_Error_Size;
        }

        if (HasOptions && ParseOptions() is var options && (options.Count == 0 || options.DistinctBy(o => o.Export).Count() != options.Count))
        {
            return Strings.FieldProps_Error_Options;
        }

        if (IsText && FormatKind != FormFormatKind.None && Normalized(new FormTextFormat(FormatKind, Decimals, UsesDotComma ? NumberSeparators.DotComma : NumberSeparators.CommaDot, DatePattern)) is null)
        {
            return Strings.FieldProps_Error_Default;
        }

        return null;
    }

    private static bool IsAncestor(string name, string of)
        => of.Length > name.Length && of[name.Length] == '.' && of.StartsWith(name, StringComparison.Ordinal);
}
