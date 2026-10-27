using System.Text.Json.Serialization;
using PdfPro.Core.Geometry;

namespace PdfPro.Core.Editing;

/// <summary>
/// The page object an edit is about: its place among the page's objects when it was read, recognised by its kind and
/// box — when the page changed meanwhile it is looked for by those, and when it cannot be found the edit is refused.
/// </summary>
/// <param name="Page">1-based page.</param>
/// <param name="Index">Its place among the page's objects when read (<see cref="PageObjectInfo.Index"/>).</param>
/// <param name="Kind">Its kind.</param>
/// <param name="Bounds">Its upright box when read, in PDF user space.</param>
public sealed record PageObjectRef(int Page, int Index, PageObjectKind Kind, PdfRect Bounds)
{
    /// <summary>How far the box may be from the one read to still be the same object, in points.</summary>
    public const double Tolerance = 1;

    public static PageObjectRef Of(int page, PageObjectInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new PageObjectRef(page, info.Index, info.Kind, info.Bounds);
    }

    /// <summary>True when <paramref name="bounds"/> is this object's box (within <see cref="Tolerance"/>).</summary>
    public bool Matches(PageObjectKind kind, PdfRect bounds)
        => kind == Kind
            && Math.Abs(bounds.Left - Bounds.Left) <= Tolerance && Math.Abs(bounds.Bottom - Bounds.Bottom) <= Tolerance
            && Math.Abs(bounds.Right - Bounds.Right) <= Tolerance && Math.Abs(bounds.Top - Bounds.Top) <= Tolerance;
}

/// <summary>What happens to words longer than the text object they replace.</summary>
public enum TextOverflow
{
    /// <summary>One line, running past the old end.</summary>
    Overflow,

    /// <summary>Broken into lines as wide as the old text, going down.</summary>
    Wrap,
}

/// <summary>A change to one drawing of a page's content (edit.content).</summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(ReplaceTextEdit), "text")]
[JsonDerivedType(typeof(DeleteObjectEdit), "delete")]
[JsonDerivedType(typeof(MoveObjectEdit), "move")]
public abstract record ContentEdit(PageObjectRef Target)
{
    /// <summary>The longest text written into one object.</summary>
    public const int MaxTextLength = 20_000;

    /// <summary>The longest move, in points.</summary>
    public const double MaxMove = 14_400;

    /// <summary>What is wrong with the edit (English, for logs and validation), or null.</summary>
    public virtual string? Problem() => Target is null ? "The object is missing." : null;
}

/// <summary>
/// Rewrites the words of a text object, keeping its font, size, colour and transform when the font has every glyph;
/// otherwise the object is replaced by one written with a bundled Unicode font of the same look.
/// </summary>
public sealed record ReplaceTextEdit(PageObjectRef Target, string Text, TextOverflow Overflow = TextOverflow.Overflow) : ContentEdit(Target)
{
    public override string? Problem()
        => base.Problem()
            ?? (Target.Kind != PageObjectKind.Text ? "Only text objects have words."
            : Text is null || Text.Length > MaxTextLength ? "The text is missing or longer than 20000 characters."
            : null);
}

/// <summary>Removes a drawing from the page.</summary>
public sealed record DeleteObjectEdit(PageObjectRef Target) : ContentEdit(Target);

/// <summary>Moves a drawing on its page by (<paramref name="Dx"/>, <paramref name="Dy"/>) points (up is positive).</summary>
public sealed record MoveObjectEdit(PageObjectRef Target, double Dx, double Dy) : ContentEdit(Target)
{
    public override string? Problem()
        => base.Problem()
            ?? (!double.IsFinite(Dx) || !double.IsFinite(Dy) || Math.Abs(Dx) > MaxMove || Math.Abs(Dy) > MaxMove || (Dx == 0 && Dy == 0)
                ? "The move must be a finite distance within the page."
                : null);
}
