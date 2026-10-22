using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;

namespace PdfPro.Core.Tests.Organizing;

public sealed class SplitMarkTests
{
    private readonly PagePlan _plan = Plans.Identity(6);

    [Fact]
    public void SplitMark_RejectsBlankOrOverlongNames()
    {
        new SplitMark().FileName.ShouldBeNull();
        new SplitMark("QĐ 12").FileName.ShouldBe("QĐ 12");
        SplitMark.Unnamed.ShouldBe(new SplitMark());
        Should.Throw<ArgumentException>(() => new SplitMark("  "));
        Should.Throw<ArgumentOutOfRangeException>(() => new SplitMark(new string('a', SplitMark.MaxFileNameLength + 1)));
    }

    [Fact]
    public void MarkSplit_MarksAndUnmarksPages()
    {
        var marked = _plan.MarkSplit([3, 1], marked: true);

        marked.GetSplitStarts().ShouldBe([0, 1, 3]);
        marked.HasSplitMarks.ShouldBeTrue();
        marked[1].SplitMark.ShouldBe(SplitMark.Unnamed);
        _plan.HasSplitMarks.ShouldBeFalse();
        _plan.GetSplitStarts().ShouldBe([0]);
        marked.MarkSplit([1], marked: false).GetSplitStarts().ShouldBe([0, 3]);
    }

    [Fact]
    public void MarkSplit_ThatChangesNothing_ReturnsTheSamePlan()
    {
        var marked = _plan.MarkSplit([2], marked: true);

        marked.MarkSplit([2], marked: true).ShouldBeSameAs(marked);
        _plan.MarkSplit([2], marked: false).ShouldBeSameAs(_plan);
        Should.Throw<ArgumentOutOfRangeException>(() => _plan.MarkSplit([6], marked: true));
        Should.Throw<ArgumentException>(() => _plan.MarkSplit([], marked: true));
    }

    [Fact]
    public void MarkingTheFirstPage_OnlyNamesTheFirstFile()
    {
        var plan = _plan.WithSplitMarks(new Dictionary<int, SplitMark> { [0] = new("Tờ trình"), [4] = new("Quyết định") });

        plan.GetSplitStarts().ShouldBe([0, 4]);
        plan[0].SplitMark!.FileName.ShouldBe("Tờ trình");
    }

    [Fact]
    public void MarkSplit_KeepsTheNameOfAPageAlreadyMarked()
    {
        var named = _plan.WithSplitMarks(new Dictionary<int, SplitMark> { [2] = new("Hợp đồng") });

        named.MarkSplit([2, 3], marked: true)[2].SplitMark!.FileName.ShouldBe("Hợp đồng");
    }

    [Fact]
    public void WithSplitMarks_ReplacesEveryMark()
    {
        var plan = _plan.MarkSplit([1, 2], marked: true);

        var replaced = plan.WithSplitMarks(new Dictionary<int, SplitMark> { [4] = SplitMark.Unnamed });

        replaced.GetSplitStarts().ShouldBe([0, 4]);
        replaced.WithSplitMarks(new Dictionary<int, SplitMark>()).HasSplitMarks.ShouldBeFalse();
        replaced.WithSplitMarks(new Dictionary<int, SplitMark> { [4] = SplitMark.Unnamed }).ShouldBeSameAs(replaced);
        Should.Throw<ArgumentOutOfRangeException>(() => plan.WithSplitMarks(new Dictionary<int, SplitMark> { [9] = SplitMark.Unnamed }));
    }

    [Fact]
    public void Marks_FollowTheirPagesThroughEdits()
    {
        var plan = _plan.MarkSplit([3], marked: true);

        plan.Move([3], 0).GetSplitStarts().ShouldBe([0]);
        plan.Move([3], 0)[0].SplitMark.ShouldNotBeNull();
        plan.Move([0], 6).GetSplitStarts().ShouldBe([0, 2]);
        plan.Rotate([3], 90)[3].SplitMark.ShouldNotBeNull();
        plan.Crop([3], new PageCrop(0, 0, 100, 100))[3].SplitMark.ShouldNotBeNull();
        plan.Delete([3]).HasSplitMarks.ShouldBeFalse();
        plan.InsertBlank(0, new PdfSize(100, 100), new SequentialKeys(50)).GetSplitStarts().ShouldBe([0, 4]);
    }

    [Fact]
    public void Duplicate_DoesNotCopyMarks()
    {
        var plan = _plan.MarkSplit([3], marked: true).Duplicate([3], new SequentialKeys(50));

        plan.GetSplitStarts().ShouldBe([0, 3]);
        plan[4].SplitMark.ShouldBeNull();
    }

    [Fact]
    public void WithSplitMarksOf_CopiesMarksPositionallyWhenTheCountMatches()
    {
        var marked = _plan.MarkSplit([2, 5], marked: true);
        var revision = PagePlan.Identity("r1", 6, new SequentialKeys(100));

        revision.WithSplitMarksOf(marked).GetSplitStarts().ShouldBe([0, 2, 5]);
        revision.WithSplitMarksOf(_plan).ShouldBeSameAs(revision);
        PagePlan.Identity("r1", 5, new SequentialKeys(100)).WithSplitMarksOf(marked).HasSplitMarks.ShouldBeFalse();
    }

    [Fact]
    public void HasSameContent_IgnoresOnlyMarks()
    {
        var marked = _plan.MarkSplit([1], marked: true);

        marked.HasSameContent(_plan).ShouldBeTrue();
        marked.Rotate([1], 90).HasSameContent(_plan).ShouldBeFalse();
        marked.Move([1], 3).HasSameContent(_plan).ShouldBeFalse();
        marked.Delete([1]).HasSameContent(_plan).ShouldBeFalse();
        marked[1].HasSameContent(_plan[1]).ShouldBeTrue();
        marked[1].ShouldNotBe(_plan[1]);
    }

    [Fact]
    public void PageRef_WithSplitMark_KeepsEverythingElse()
    {
        var page = new PageRef("s1", 4, 90, new PageCrop(1, 2, 30, 40), SequentialKeys.Key(7));

        var marked = page.WithSplitMark(new SplitMark("A"));

        marked.SplitMark!.FileName.ShouldBe("A");
        marked.HasSameContent(page).ShouldBeTrue();
        marked.WithSplitMark(new SplitMark("A")).ShouldBeSameAs(marked);
        marked.WithRotation(180).SplitMark.ShouldBe(marked.SplitMark);
        marked.WithKey(SequentialKeys.Key(8)).SplitMark.ShouldBeNull();
        marked.WithSplitMark(null).ShouldBe(page);
    }
}
