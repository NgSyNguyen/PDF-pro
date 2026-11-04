using PdfPro.Core.Annotations;
using PdfPro.Core.Forms;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfPro.Core.Overlay;

namespace PdfPro.Core.Tests.Organizing;

public sealed class PagePlanTests
{
    private readonly PagePlan _plan = Plans.Identity(5);

    [Fact]
    public void Identity_ListsEverySourcePageInOrder()
    {
        _plan.Count.ShouldBe(5);
        Plans.Order(_plan).ShouldBe([0, 1, 2, 3, 4]);
        _plan.ShouldAllBe(p => p.SourceId == "s0" && p.RotationDelta == 0 && p.CropBoxOverride == null);
        _plan.Select(p => p.PageKey).Distinct().Count().ShouldBe(5);
        _plan.IsIdentity("s0", 5).ShouldBeTrue();
    }

    [Fact]
    public void AddsOnlyAnnotations_WhenTheyAreTheOnlyChange()
    {
        var note = new NoteAnnotation("n", new RgbColor(255, 230, 0), 1, "Lan", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "", new PdfPoint(10, 10));
        var annotated = _plan.WithAnnotations(2, [note]);

        _plan.AddsOnlyAnnotations("s0", 5).ShouldBeFalse("nothing is added");
        annotated.AddsOnlyAnnotations("s0", 5).ShouldBeTrue();
        annotated.IsIdentity("s0", 5).ShouldBeFalse();
        annotated.AddsOnlyAnnotations("s0", 6).ShouldBeFalse();
        annotated.AddsOnlyAnnotations("s1", 5).ShouldBeFalse();
        annotated.Rotate([0], 90).AddsOnlyAnnotations("s0", 5).ShouldBeFalse();
        annotated.Move([4], 0).AddsOnlyAnnotations("s0", 5).ShouldBeFalse();
        annotated.WithFormValues([new FormValue("Ten", "An")]).AddsOnlyAnnotations("s0", 5).ShouldBeFalse();
    }

    [Fact]
    public void Identity_NeedsAtLeastOnePage()
        => Should.Throw<ArgumentOutOfRangeException>(() => PagePlan.Identity("s0", 0, new SequentialKeys()));

    [Fact]
    public void Create_RejectsDuplicateKeysAndNullPages()
    {
        var page = new PageRef("s0", 0, 0, null, SequentialKeys.Key(1));

        Should.Throw<ArgumentException>(() => PagePlan.Create([page, page.WithRotation(90)]));
        Should.Throw<ArgumentException>(() => PagePlan.Create([page, null!]));
        PagePlan.Create([]).Count.ShouldBe(0);
    }

    [Fact]
    public void Lookups_FindPagesByKeyAndBySourcePage()
    {
        var plan = _plan.Move([4], 0);

        plan.IndexOf(SequentialKeys.Key(5)).ShouldBe(0);
        plan.IndexOf(SequentialKeys.Key(1)).ShouldBe(1);
        plan.IndexOf(SequentialKeys.Key(99)).ShouldBe(-1);
        plan.FirstIndexOf("s0", 4).ShouldBe(0);
        plan.FirstIndexOf("s0", 7).ShouldBe(-1);
        plan.FirstIndexOf("s1", 0).ShouldBe(-1);
        plan.IsIdentity("s0", 5).ShouldBeFalse();
        _plan.IsIdentity("s0", 4).ShouldBeFalse();
        _plan.IsIdentity("s1", 5).ShouldBeFalse();
    }

    [Fact]
    public void GetSourceIds_IgnoresBlankPages()
    {
        var plan = _plan
            .InsertFromSource(0, "s1", [0], new SequentialKeys(100))
            .InsertBlank(0, new PdfSize(100, 100), new SequentialKeys(200));

        plan.GetSourceIds().ShouldBe(["s0", "s1"], ignoreOrder: true);
    }

    [Theory]
    [InlineData(new[] { 1 }, 4, new[] { 0, 2, 3, 1, 4 })]
    [InlineData(new[] { 3 }, 1, new[] { 0, 3, 1, 2, 4 })]
    [InlineData(new[] { 1, 3 }, 5, new[] { 0, 2, 4, 1, 3 })]
    [InlineData(new[] { 1, 3 }, 0, new[] { 1, 3, 0, 2, 4 })]
    [InlineData(new[] { 3, 1, 3 }, 0, new[] { 1, 3, 0, 2, 4 })]
    [InlineData(new[] { 0, 4 }, 2, new[] { 1, 0, 4, 2, 3 })]
    [InlineData(new[] { 0, 1, 2, 3 }, 5, new[] { 4, 0, 1, 2, 3 })]
    public void Move_PutsPagesBeforeTheTarget(int[] indices, int target, int[] expected)
        => Plans.Order(_plan.Move(indices, target)).ShouldBe(expected);

    [Theory]
    [InlineData(new[] { 0 }, 0)]
    [InlineData(new[] { 0 }, 1)]
    [InlineData(new[] { 4 }, 5)]
    [InlineData(new[] { 1, 2 }, 2)]
    [InlineData(new[] { 1, 2 }, 3)]
    [InlineData(new[] { 0, 1, 2, 3, 4 }, 3)]
    public void Move_ThatChangesNothing_ReturnsTheSamePlan(int[] indices, int target)
        => _plan.Move(indices, target).ShouldBeSameAs(_plan);

    [Fact]
    public void Move_KeepsThePageInstances()
    {
        var moved = _plan.Move([0], 5);

        moved[4].ShouldBeSameAs(_plan[0]);
        moved[0].ShouldBeSameAs(_plan[1]);
        Plans.Order(_plan).ShouldBe([0, 1, 2, 3, 4]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void Move_RejectsTargetsOutsideThePlan(int target)
        => Should.Throw<ArgumentOutOfRangeException>(() => _plan.Move([1], target));

    [Fact]
    public void Edits_RejectInvalidSelections()
    {
        Should.Throw<ArgumentNullException>(() => _plan.Move(null!, 0));
        Should.Throw<ArgumentException>(() => _plan.Rotate([], 90));
        Should.Throw<ArgumentOutOfRangeException>(() => _plan.Delete([5]));
        Should.Throw<ArgumentOutOfRangeException>(() => _plan.Duplicate([-1], new SequentialKeys(100)));
        Should.Throw<ArgumentOutOfRangeException>(() => _plan.Crop([2, 9], new PageCrop(0, 0, 10, 10)));
    }

    [Fact]
    public void Rotate_TurnsOnlyTheSelectedPages()
    {
        var rotated = _plan.Rotate([0, 2], 90);

        rotated.Select(p => p.RotationDelta).ShouldBe([90, 0, 90, 0, 0]);
        rotated[1].ShouldBeSameAs(_plan[1]);
        rotated[0].PageKey.ShouldBe(_plan[0].PageKey);
        _plan[0].RotationDelta.ShouldBe(0);
    }

    [Fact]
    public void Rotate_AddsToTheCurrentRotation()
    {
        var plan = _plan.Rotate([1], -90).Rotate([1], 180);

        plan[1].RotationDelta.ShouldBe(90);
        plan.Rotate([1], 270)[1].RotationDelta.ShouldBe(0);
    }

    [Fact]
    public void Rotate_ByAFullTurn_ReturnsTheSamePlan()
    {
        _plan.Rotate([1], 360).ShouldBeSameAs(_plan);
        _plan.Rotate([1], 0).ShouldBeSameAs(_plan);
        Should.Throw<ArgumentOutOfRangeException>(() => _plan.Rotate([1], 45));
    }

    [Theory]
    [InlineData(new[] { 1, 3 }, new[] { 0, 2, 4 })]
    [InlineData(new[] { 0 }, new[] { 1, 2, 3, 4 })]
    [InlineData(new[] { 4 }, new[] { 0, 1, 2, 3 })]
    [InlineData(new[] { 0, 1, 2, 3 }, new[] { 4 })]
    public void Delete_RemovesTheSelectedPages(int[] indices, int[] expected)
        => Plans.Order(_plan.Delete(indices)).ShouldBe(expected);

    [Fact]
    public void Delete_CannotRemoveEveryPage()
        => Should.Throw<ArgumentException>(() => _plan.Delete([0, 1, 2, 3, 4]));

    [Fact]
    public void Duplicate_InsertsCopiesAfterTheLastSelectedPage()
    {
        var plan = _plan.Rotate([1], 90).Crop([3], new PageCrop(0, 0, 50, 50));

        var duplicated = plan.Duplicate([3, 1], new SequentialKeys(100));

        Plans.Order(duplicated).ShouldBe([0, 1, 2, 3, 1, 3, 4]);
        duplicated[4].PageKey.ShouldBe(SequentialKeys.Key(100));
        duplicated[5].PageKey.ShouldBe(SequentialKeys.Key(101));
        duplicated[4].RotationDelta.ShouldBe(90);
        duplicated[5].CropBoxOverride.ShouldBe(new PageCrop(0, 0, 50, 50));
    }

    [Fact]
    public void Duplicate_OfTheLastPage_AppendsTheCopy()
        => Plans.Order(_plan.Duplicate([4], new SequentialKeys(100))).ShouldBe([0, 1, 2, 3, 4, 4]);

    [Fact]
    public void NewPages_NeedFreshKeys()
    {
        Should.Throw<InvalidOperationException>(() => _plan.Duplicate([0], new SequentialKeys(1)));
        Should.Throw<InvalidOperationException>(() => _plan.Duplicate([0, 1], new ConstantKeys(SequentialKeys.Key(50))));
        Should.Throw<InvalidOperationException>(() => _plan.InsertBlank(0, new PdfSize(10, 10), new ConstantKeys(Guid.Empty)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public void InsertBlank_AddsBlankPagesAtThePosition(int index)
    {
        var plan = _plan.InsertBlank(index, new PdfSize(595, 842), new SequentialKeys(100), count: 2);

        plan.Count.ShouldBe(7);
        plan[index].IsBlank.ShouldBeTrue();
        plan[index + 1].IsBlank.ShouldBeTrue();
        plan[index].BlankSize.ShouldBe(new PdfSize(595, 842));
        plan.Count(p => p.IsBlank).ShouldBe(2);
    }

    [Fact]
    public void InsertBlank_RejectsInvalidArguments()
    {
        var keys = new SequentialKeys(100);

        Should.Throw<ArgumentOutOfRangeException>(() => _plan.InsertBlank(-1, new PdfSize(10, 10), keys));
        Should.Throw<ArgumentOutOfRangeException>(() => _plan.InsertBlank(6, new PdfSize(10, 10), keys));
        Should.Throw<ArgumentOutOfRangeException>(() => _plan.InsertBlank(0, new PdfSize(10, 10), keys, count: 0));
        Should.Throw<ArgumentOutOfRangeException>(() => _plan.InsertBlank(0, new PdfSize(0, 10), keys));
    }

    [Fact]
    public void InsertFromSource_AddsThePagesInTheGivenOrder()
    {
        var plan = _plan.InsertFromSource(2, "s1", [5, 0, 5], new SequentialKeys(100));

        plan.Count.ShouldBe(8);
        plan.Skip(2).Take(3).Select(p => (p.SourceId, p.SourcePageIndex)).ShouldBe([("s1", 5), ("s1", 0), ("s1", 5)]);
        Plans.Order(plan.Delete([2, 3, 4])).ShouldBe([0, 1, 2, 3, 4]);
    }

    [Fact]
    public void InsertFromSource_RejectsInvalidArguments()
    {
        var keys = new SequentialKeys(100);

        Should.Throw<ArgumentException>(() => _plan.InsertFromSource(0, "s1", [], keys));
        Should.Throw<ArgumentException>(() => _plan.InsertFromSource(0, SourceIds.Blank, [0], keys));
        Should.Throw<ArgumentException>(() => _plan.InsertFromSource(0, "S1", [0], keys));
        Should.Throw<ArgumentOutOfRangeException>(() => _plan.InsertFromSource(6, "s1", [0], keys));
        Should.Throw<ArgumentOutOfRangeException>(() => _plan.InsertFromSource(0, "s1", [-1], keys));
    }

    [Fact]
    public void Crop_SetsAndRemovesCrops()
    {
        var crop = new PageCrop(5, 5, 100, 100);

        var cropped = _plan.Crop([0, 1], crop);
        var reset = cropped.Crop([0], null);

        cropped.Select(p => p.CropBoxOverride).ShouldBe([crop, crop, null, null, null]);
        reset[0].CropBoxOverride.ShouldBeNull();
        reset[1].CropBoxOverride.ShouldBe(crop);
        cropped.Crop([0, 1], crop).ShouldBeSameAs(cropped);
    }

    [Fact]
    public void Crop_PerPage_GivesEachPageItsOwnRectangle()
    {
        var crops = new Dictionary<int, PageCrop?> { [1] = new PageCrop(0, 0, 10, 20), [3] = new PageCrop(1, 1, 30, 40) };

        var cropped = _plan.Crop(crops);

        cropped[1].CropBoxOverride.ShouldBe(new PageCrop(0, 0, 10, 20));
        cropped[3].CropBoxOverride.ShouldBe(new PageCrop(1, 1, 30, 40));
        cropped[0].ShouldBeSameAs(_plan[0]);
        Should.Throw<ArgumentException>(() => _plan.Crop(new Dictionary<int, PageCrop?>()));
    }
}
