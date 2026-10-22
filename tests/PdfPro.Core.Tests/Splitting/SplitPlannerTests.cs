using PdfPro.Core.Organizing;
using PdfPro.Core.Splitting;
using PdfPro.Core.Tests.Organizing;

namespace PdfPro.Core.Tests.Splitting;

public sealed class SplitPlannerTests
{
    [Fact]
    public void ByRanges_MakesOneFilePerPart()
    {
        SplitPlanner.TryByRanges("1-3, 4-10; 11-", 12, out var segments, out var error).ShouldBeTrue();

        error.ShouldBeNull();
        Pages(segments).ShouldBe([[0, 1, 2], [3, 4, 5, 6, 7, 8, 9], [10, 11]]);
    }

    [Fact]
    public void ByRanges_AllowsReversedOverlappingAndKeywordParts()
    {
        SplitPlanner.TryByRanges("3-1, 2 4, chẵn", 5, out var segments, out _).ShouldBeTrue();

        Pages(segments).ShouldBe([[0, 1, 2], [1, 3], [1, 3]]);
    }

    [Theory]
    [InlineData("", PageRangeErrorKind.Empty)]
    [InlineData(" , ;", PageRangeErrorKind.Empty)]
    [InlineData("1-3, 9", PageRangeErrorKind.OutOfRange)]
    [InlineData("1-3, x", PageRangeErrorKind.InvalidSyntax)]
    public void ByRanges_ReportsTheBadPart(string text, PageRangeErrorKind kind)
    {
        SplitPlanner.TryByRanges(text, 5, out var segments, out var error).ShouldBeFalse();

        segments.ShouldBeNull();
        error!.Kind.ShouldBe(kind);
    }

    [Theory]
    [InlineData(10, 3, new[] { 3, 3, 3, 1 })]
    [InlineData(6, 3, new[] { 3, 3 })]
    [InlineData(2, 5, new[] { 2 })]
    [InlineData(4, 1, new[] { 1, 1, 1, 1 })]
    public void EveryN_CutsEqualFilesWithAShorterLastOne(int pageCount, int n, int[] sizes)
    {
        var segments = SplitPlanner.EveryN(pageCount, n);

        segments.Select(s => s.PageCount).ShouldBe(sizes);
        segments.SelectMany(s => s.Pages).ShouldBe(Enumerable.Range(0, pageCount));
        Should.Throw<ArgumentOutOfRangeException>(() => SplitPlanner.EveryN(pageCount, 0));
    }

    [Fact]
    public void ByStarts_RunsEachFileToTheNextStart()
    {
        var segments = SplitPlanner.ByStarts(9, [new(5, "B"), new(2, "A"), new(5, "ignored"), new(8)]);

        Pages(segments).ShouldBe([[0, 1], [2, 3, 4], [5, 6, 7], [8]]);
        segments.Select(s => s.Title).ShouldBe([null, "A", "B", null]);
        Should.Throw<ArgumentOutOfRangeException>(() => SplitPlanner.ByStarts(9, [new(9)]));
    }

    [Fact]
    public void ByMarks_FollowsTheUsersFirstPages()
    {
        // A scanned bundle: documents start on pages 1, 4, 5 and 9 (1-based); page 4 got a name.
        var plan = Plans.Identity(10).WithSplitMarks(new Dictionary<int, SplitMark>
        {
            [3] = new("QĐ 12-2024 bổ nhiệm"),
            [4] = SplitMark.Unnamed,
            [8] = SplitMark.Unnamed,
        });

        var segments = SplitPlanner.ByMarks(plan);

        Pages(segments).ShouldBe([[0, 1, 2], [3], [4, 5, 6, 7], [8, 9]]);
        segments.Select(s => s.FileName).ShouldBe([null, "QĐ 12-2024 bổ nhiệm", null, null]);
        Pages(SplitPlanner.ByMarks(Plans.Identity(3))).ShouldBe([[0, 1, 2]]);
    }

    [Fact]
    public void ByMarks_UsesTheFirstPagesNameForTheFirstFile()
    {
        var plan = Plans.Identity(4).WithSplitMarks(new Dictionary<int, SplitMark> { [0] = new("Tờ trình"), [2] = SplitMark.Unnamed });

        SplitPlanner.ByMarks(plan).Select(s => s.FileName).ShouldBe(["Tờ trình", null]);
    }

    [Fact]
    public void ByBookmarks_SplitsAtTheChosenLevelAndAbove()
    {
        SplitBookmark[] outline =
        [
            new(0, 1, "Chương 1"),
            new(0, 2, "1.1 Mục đích"),
            new(2, 2, "1.2 Phạm vi"),
            new(3, 3, "1.2.1 Chi tiết"),
            new(4, 1, "Chương 2"),
            new(-1, 1, "Liên kết hỏng"),
            new(6, 2, "2.1 Phụ lục"),
        ];

        var top = SplitPlanner.ByBookmarks(8, outline, level: 1);
        var second = SplitPlanner.ByBookmarks(8, outline, level: 2);

        Pages(top).ShouldBe([[0, 1, 2, 3], [4, 5, 6, 7]]);
        top.Select(s => s.Title).ShouldBe(["Chương 1", "Chương 2"]);
        Pages(second).ShouldBe([[0, 1], [2, 3], [4, 5], [6, 7]]);
        second.Select(s => s.Title).ShouldBe(["1.1 Mục đích", "1.2 Phạm vi", "Chương 2", "2.1 Phụ lục"]);
    }

    [Fact]
    public void ByBookmarks_PagesBeforeTheFirstBookmarkFormAnUntitledFile()
    {
        var segments = SplitPlanner.ByBookmarks(5, [new(2, 1, "Nội dung")], level: 1);

        Pages(segments).ShouldBe([[0, 1], [2, 3, 4]]);
        segments[0].Title.ShouldBeNull();
    }

    [Fact]
    public void BySeparators_LeavesSeparatorPagesOut()
    {
        var segments = SplitPlanner.BySeparators(10, new HashSet<int> { 0, 3, 4, 7, 9 });

        Pages(segments).ShouldBe([[1, 2], [5, 6], [8]]);
        SplitPlanner.BySeparators(2, new HashSet<int> { 0, 1 }).ShouldBeEmpty();
        Pages(SplitPlanner.BySeparators(3, new HashSet<int>())).ShouldBe([[0, 1, 2]]);
    }

    [Fact]
    public void Without_DropsBlankBacksAndEmptyFiles()
    {
        var segments = SplitPlanner.ByStarts(6, [new(2, FileName: "B"), new(4)]);

        var trimmed = SplitPlanner.Without(segments, new HashSet<int> { 1, 4, 5 });

        Pages(trimmed).ShouldBe([[0], [2, 3]]);
        trimmed[1].FileName.ShouldBe("B");
        SplitPlanner.Without(segments, new HashSet<int>()).ShouldBeSameAs(segments);
    }

    [Fact]
    public void BySize_GathersPagesWhileTheEstimateFits()
    {
        // Every page has its own 40-byte content; pages 0-3 share a 100-byte font (counted once per file).
        IReadOnlyList<SizedPart> Parts(int page) => page < 4 ? [new(page, 40), new(100, 100)] : [new(page, 40)];

        var segments = SplitPlanner.BySize(6, Parts, overheadBytes: 10, limitBytes: 200);

        // File 1: 10 + 40 + 100 + 40 = 190; page 2 would reach 230. File 2: pages 2-3 (190), file 3: pages 4-5 (90).
        Pages(segments).ShouldBe([[0, 1], [2, 3], [4, 5]]);
        segments.Select(s => SplitPlanner.EstimateSize(s, Parts, 10)).ShouldBe([190L, 190L, 90L]);
        segments.Select(s => SplitPlanner.IsOverLimit(s, Parts, 10, 200)).ShouldAllBe(over => !over);
    }

    [Fact]
    public void BySize_GivesAnOversizedPageItsOwnFile()
    {
        IReadOnlyList<SizedPart> Parts(int page) => [new(page, page == 1 ? 500 : 30)];

        var segments = SplitPlanner.BySize(3, Parts, overheadBytes: 0, limitBytes: 100);

        Pages(segments).ShouldBe([[0], [1], [2]]);
        SplitPlanner.IsOverLimit(segments[1], Parts, 0, 100).ShouldBeTrue();
        SplitPlanner.IsOverLimit(segments[0], Parts, 0, 100).ShouldBeFalse();
    }

    [Fact]
    public void SplitSegment_ValidatesAndNormalizesBlankNames()
    {
        var segment = new SplitSegment([3, 4], " ", "");

        segment.Title.ShouldBeNull();
        segment.FileName.ShouldBeNull();
        segment.First.ShouldBe(3);
        segment.Last.ShouldBe(4);
        Should.Throw<ArgumentException>(() => new SplitSegment([]));
        Should.Throw<ArgumentOutOfRangeException>(() => new SplitSegment([-1]));
        segment.Without(new HashSet<int> { 3, 4 }).ShouldBeNull();
    }

    private static int[][] Pages(IReadOnlyList<SplitSegment>? segments) => segments!.Select(s => s.Pages.ToArray()).ToArray();
}
