namespace PdfPro.Core.Annotations;

/// <summary>The families of comments readers tell apart: filtered in the comments list, chosen when flattening.</summary>
public enum AnnotationGroup
{
    /// <summary>Notes, text boxes, insertion marks, attachments, sounds.</summary>
    Notes,

    /// <summary>Highlight, underline, strike-out, squiggly.</summary>
    Markup,

    /// <summary>Ink, rectangles, ellipses, lines, arrows, polygons, polylines.</summary>
    Drawings,

    /// <summary>Rubber stamps (words or pictures).</summary>
    Stamps,
}

/// <summary>Which family a kind of annotation belongs to.</summary>
public static class AnnotationGroups
{
    /// <summary>The family of a comment kind, or null for kinds that are not comments (links, fields, redactions…).</summary>
    public static AnnotationGroup? Of(FileAnnotationKind kind) => kind switch
    {
        FileAnnotationKind.Text or FileAnnotationKind.FreeText or FileAnnotationKind.Caret
            or FileAnnotationKind.FileAttachment or FileAnnotationKind.Sound => AnnotationGroup.Notes,
        FileAnnotationKind.Highlight or FileAnnotationKind.Underline or FileAnnotationKind.Squiggly
            or FileAnnotationKind.StrikeOut => AnnotationGroup.Markup,
        FileAnnotationKind.Ink or FileAnnotationKind.Line or FileAnnotationKind.Square or FileAnnotationKind.Circle
            or FileAnnotationKind.Polygon or FileAnnotationKind.PolyLine => AnnotationGroup.Drawings,
        FileAnnotationKind.Stamp => AnnotationGroup.Stamps,
        _ => null,
    };

    /// <summary>
    /// The kind named by a PDF /Subtype ("/Highlight" or "Highlight"), or <see cref="FileAnnotationKind.Unknown"/>.
    /// </summary>
    public static FileAnnotationKind KindOf(string? subtype)
    {
        var name = subtype is ['/', .. var rest] ? rest : subtype;
        return name switch
        {
            null or "" => FileAnnotationKind.Unknown,
            "3D" => FileAnnotationKind.ThreeD,
            "XFAWidget" => FileAnnotationKind.XfaWidget,
            _ when name.All(char.IsAsciiLetter) && Enum.TryParse<FileAnnotationKind>(name, ignoreCase: false, out var kind) => kind,
            _ => FileAnnotationKind.Unknown,
        };
    }
}
