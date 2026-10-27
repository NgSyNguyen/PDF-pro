using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Overlay;

namespace PdfPro.Core.Tests.Organizing;

/// <summary>Annotations made in the session ride on their page through the plan's edits.</summary>
public sealed class PageAnnotationsTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(7));

    private readonly PagePlan _plan = Plans.Identity(4);

    [Fact]
    public void APage_StartsWithoutAnnotations()
    {
        _plan.ShouldAllBe(p => p.Annotations.Count == 0);
        _plan.HasAnnotations.ShouldBeFalse();
        PageRef.Blank(new PdfSize(100, 100), SequentialKeys.Key(9)).Annotations.ShouldBeEmpty();
    }

    [Fact]
    public void Annotations_StayWithTheirPage_WhenPagesAreMovedTurnedCroppedOrMarked()
    {
        var plan = _plan.WithAnnotations(1, [Note("a"), Note("b")]);
        var annotations = plan[1].Annotations;

        var edited = plan.Move([1], 4).Rotate([3], 90).Crop([3], new PageCrop(0, 0, 50, 50)).MarkSplit([3], marked: true);

        edited[3].PageKey.ShouldBe(SequentialKeys.Key(2));
        edited[3].Annotations.ShouldBeSameAs(annotations);
        edited.HasAnnotations.ShouldBeTrue();
        edited.Take(3).ShouldAllBe(p => p.Annotations.Count == 0);
    }

    [Fact]
    public void ADuplicatedPage_KeepsItsAnnotations()
    {
        var plan = _plan.WithAnnotations(0, [Note("a")]);

        var duplicated = plan.Duplicate([0], new SequentialKeys(100));

        duplicated[1].PageKey.ShouldBe(SequentialKeys.Key(100));
        duplicated[1].Annotations.Select(a => a.Id).ShouldBe(["a"]);
    }

    [Fact]
    public void Annotations_AreContent()
    {
        var annotated = _plan.WithAnnotations(2, [Note("a")]);

        annotated.HasSameContent(_plan).ShouldBeFalse();
        annotated.IsIdentity("s0", 4).ShouldBeFalse("the file must be written to hold them");
        annotated.WithAnnotations(2, []).HasSameContent(_plan).ShouldBeTrue();
        annotated.WithAnnotations(2, annotated[2].Annotations).ShouldBeSameAs(annotated);
        _plan.WithAnnotations(0, []).ShouldBeSameAs(_plan);
    }

    [Fact]
    public void Names_AreUniqueOnAPage_AndFindTheirAnnotation()
    {
        Should.Throw<ArgumentException>(() => _plan.WithAnnotations(0, [Note("a"), Note("a")]));
        Should.Throw<ArgumentException>(() => _plan.WithAnnotations(0, [Note("a"), null!]));
        Should.Throw<ArgumentOutOfRangeException>(() => _plan.WithAnnotations(4, [Note("a")]));

        var page = _plan.WithAnnotations(0, [Note("a"), Note("b")])[0];
        page.FindAnnotation("b").ShouldNotBeNull().Id.ShouldBe("b");
        page.FindAnnotation("c").ShouldBeNull();
    }

    private static NoteAnnotation Note(string id) => new(id, new RgbColor(255, 230, 0), 1, "Lan", Created, Created, "Ghi chú", new PdfPoint(100, 700));
}
