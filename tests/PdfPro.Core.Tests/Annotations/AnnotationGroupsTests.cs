using PdfPro.Core.Annotations;

namespace PdfPro.Core.Tests.Annotations;

public sealed class AnnotationGroupsTests
{
    [Theory]
    [InlineData("/Highlight", FileAnnotationKind.Highlight)]
    [InlineData("StrikeOut", FileAnnotationKind.StrikeOut)]
    [InlineData("/3D", FileAnnotationKind.ThreeD)]
    [InlineData("/XFAWidget", FileAnnotationKind.XfaWidget)]
    [InlineData("/highlight", FileAnnotationKind.Unknown)]
    [InlineData("/1", FileAnnotationKind.Unknown)]
    [InlineData("/Bogus", FileAnnotationKind.Unknown)]
    [InlineData("", FileAnnotationKind.Unknown)]
    [InlineData(null, FileAnnotationKind.Unknown)]
    public void KindOf_ReadsTheSubtypeName(string? subtype, FileAnnotationKind expected)
        => AnnotationGroups.KindOf(subtype).ShouldBe(expected);

    [Theory]
    [InlineData(FileAnnotationKind.Text, AnnotationGroup.Notes)]
    [InlineData(FileAnnotationKind.FreeText, AnnotationGroup.Notes)]
    [InlineData(FileAnnotationKind.Caret, AnnotationGroup.Notes)]
    [InlineData(FileAnnotationKind.Squiggly, AnnotationGroup.Markup)]
    [InlineData(FileAnnotationKind.PolyLine, AnnotationGroup.Drawings)]
    [InlineData(FileAnnotationKind.Ink, AnnotationGroup.Drawings)]
    [InlineData(FileAnnotationKind.Stamp, AnnotationGroup.Stamps)]
    public void Comments_BelongToAFamily(FileAnnotationKind kind, AnnotationGroup expected)
        => AnnotationGroups.Of(kind).ShouldBe(expected);

    [Theory]
    [InlineData(FileAnnotationKind.Link)]
    [InlineData(FileAnnotationKind.Widget)]
    [InlineData(FileAnnotationKind.Popup)]
    [InlineData(FileAnnotationKind.Redact)]
    [InlineData(FileAnnotationKind.Unknown)]
    public void OtherAnnotations_BelongToNone(FileAnnotationKind kind)
        => AnnotationGroups.Of(kind).ShouldBeNull();
}
