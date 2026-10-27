using PdfPro.Core.Geometry;

namespace PdfPro.Core.Forms;

/// <summary>What a form field is (12.7.5).</summary>
public enum FormFieldKind
{
    /// <summary>Typed text, one line or several.</summary>
    Text,

    /// <summary>On or off.</summary>
    CheckBox,

    /// <summary>One choice of a group of buttons.</summary>
    RadioGroup,

    /// <summary>A drop-down list, possibly with typed text.</summary>
    ComboBox,

    /// <summary>A list, one or several choices.</summary>
    ListBox,

    /// <summary>A button that does something; it has no value.</summary>
    PushButton,

    /// <summary>A place for a digital signature (P8).</summary>
    Signature,
}

/// <summary>The field flags of the PDF standard (/Ff, 12.7.4.1 and 12.7.5), by their bit.</summary>
[Flags]
public enum FormFieldFlags
{
    None = 0,
    ReadOnly = 1 << 0,
    Required = 1 << 1,
    NoExport = 1 << 2,
    Multiline = 1 << 12,
    Password = 1 << 13,
    NoToggleToOff = 1 << 14,
    Radio = 1 << 15,
    PushButton = 1 << 16,
    Combo = 1 << 17,
    Edit = 1 << 18,
    Sort = 1 << 19,
    FileSelect = 1 << 20,
    MultiSelect = 1 << 21,
    DoNotSpellCheck = 1 << 22,
    DoNotScroll = 1 << 23,
    Comb = 1 << 24,
    RadiosInUnison = 1 << 25,
    CommitOnSelectionChange = 1 << 26,
}

/// <summary>How the text of a field is placed in its box (/Q).</summary>
public enum FormTextAlignment
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>One place a field is shown: a widget annotation on a page.</summary>
/// <param name="Page">0-based page of the document the field was read from.</param>
/// <param name="Rect">Its box, in the page's PDF user space.</param>
/// <param name="OnState">For check boxes and radio buttons: the appearance state (export value) that means "on".</param>
public sealed record FormWidget(int Page, PdfRect Rect, string? OnState = null);

/// <summary>A choice of a combo box or list box: what is written as the value, and what is shown.</summary>
public sealed record FormOption(string Export, string Display);

/// <summary>
/// A field of a document's form as read: its fully qualified name, kind, value and how it may be filled. Text is not
/// normalized (callers normalize to NFC before showing or comparing).
/// </summary>
/// <param name="Name">Fully qualified name ("parent.child").</param>
/// <param name="Kind">What it is.</param>
/// <param name="Value">
/// Text of a text field or combo box; the on state of a check box or the chosen button of a radio group, "Off" when
/// none; the first choice of a list box. Null when it has no value.
/// </param>
/// <param name="Selected">The choices of a list box (several when it allows it); empty otherwise.</param>
/// <param name="Flags">/Ff.</param>
/// <param name="Options">The choices of a combo or list box.</param>
/// <param name="Widgets">Where it is shown (in /Kids order).</param>
/// <param name="ToolTip">/TU, or null.</param>
/// <param name="MaxLength">/MaxLen of a text field, or 0.</param>
/// <param name="FontSize">The size of /DA (0 means "fit the box").</param>
/// <param name="Alignment">/Q.</param>
/// <param name="DefaultValue">/DV, or null.</param>
public sealed record FormField(
    string Name,
    FormFieldKind Kind,
    string? Value,
    IReadOnlyList<string> Selected,
    FormFieldFlags Flags,
    IReadOnlyList<FormOption> Options,
    IReadOnlyList<FormWidget> Widgets,
    string? ToolTip = null,
    int MaxLength = 0,
    double FontSize = 0,
    FormTextAlignment Alignment = FormTextAlignment.Left,
    string? DefaultValue = null)
{
    /// <summary>The value of check boxes and radio groups that are not on.</summary>
    public const string Off = "Off";

    public bool IsReadOnly => Flags.HasFlag(FormFieldFlags.ReadOnly);

    public bool IsRequired => Flags.HasFlag(FormFieldFlags.Required);

    /// <summary>The user can type or choose a value (not a button, a signature or read-only).</summary>
    public bool IsFillable => !IsReadOnly && Kind is not (FormFieldKind.PushButton or FormFieldKind.Signature);

    /// <summary>A required field still empty ("Off" counts as empty).</summary>
    public bool IsMissing => IsRequired && IsFillable && (Kind == FormFieldKind.ListBox ? Selected.Count == 0 : string.IsNullOrEmpty(Value) || Value == Off);
}

/// <summary>A value given to a field (forms.fill): text, an on state, or the choices of a list box.</summary>
/// <param name="Name">The field's fully qualified name.</param>
/// <param name="Value">Text, an on state, "Off", a choice.</param>
/// <param name="Selected">The choices of a list box (null for other kinds).</param>
public sealed record FormValue(string Name, string Value, IReadOnlyList<string>? Selected = null)
{
    public bool Equals(FormValue? other)
        => other is not null && Name == other.Name && Value == other.Value
            && (Selected is null ? other.Selected is null : other.Selected is not null && Selected.SequenceEqual(other.Selected));

    public override int GetHashCode() => HashCode.Combine(Name, Value, Selected?.Count);
}

/// <summary>What kind of form a document has (FPDF_GetFormType).</summary>
public enum FormType
{
    None = 0,
    AcroForm = 1,

    /// <summary>XFA only: PDF_Pro cannot fill it.</summary>
    XfaFull = 2,

    /// <summary>XFA with an AcroForm fallback: the AcroForm fields are filled; the XFA layout is not shown.</summary>
    XfaForeground = 3,
}
