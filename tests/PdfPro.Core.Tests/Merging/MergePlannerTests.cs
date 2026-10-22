using PdfPro.Core.Geometry;
using PdfPro.Core.Merging;
using PdfPro.Core.Tests.Organizing;

namespace PdfPro.Core.Tests.Merging;

public sealed class MergePlannerTests
{
    private static readonly PdfSize A4 = new(595, 842);
    private static readonly PdfSize Landscape = new(842, 595);

    [Fact]
    public void Plan_PutsTheFilesOneAfterAnother()
    {
        var layout = MergePlanner.Plan([File("s0", 3), File("s1", 2, [1])], 0, A4, startFilesOnOddPages: false, new SequentialKeys());

        layout.Plan.Select(p => (p.SourceId, p.SourcePageIndex)).ShouldBe([("s0", 0), ("s0", 1), ("s0", 2), ("s1", 1)]);
        layout.FirstPositions.ShouldBe([0, 3]);
        layout.TableOfContentsPages.ShouldBe(0);
        layout.AddedBlankPages.ShouldBe(0);
    }

    [Fact]
    public void Plan_ReservesBlankTableOfContentsPagesFirst()
    {
        var layout = MergePlanner.Plan([File("s0", 1), File("s1", 1)], 2, A4, startFilesOnOddPages: false, new SequentialKeys());

        layout.Plan.Take(2).ShouldAllBe(p => p.IsBlank && p.BlankSize == A4);
        layout.FirstPositions.ShouldBe([2, 3]);
    }

    [Fact]
    public void Plan_StartsEveryFileOnAnOddPageWhenAsked()
    {
        // 3 + 1 + 2 pages: file 2 would start on page 4 and file 3 on page 6 (1-based) without padding.
        var files = new[] { File("s0", 3, sizes: [A4, A4, Landscape]), File("s1", 1), File("s2", 2) };

        var layout = MergePlanner.Plan(files, 0, A4, startFilesOnOddPages: true, new SequentialKeys());

        layout.FirstPositions.Select(p => p + 1).ShouldBe([1, 5, 7]);
        layout.AddedBlankPages.ShouldBe(2);
        layout.Plan[3].IsBlank.ShouldBeTrue();
        layout.Plan[3].BlankSize.ShouldBe(Landscape, "a padding page takes the size of the page before it");
        layout.Plan[5].IsBlank.ShouldBeTrue();
        layout.Plan.Count.ShouldBe(8);
    }

    [Fact]
    public void Plan_AnOddTableOfContents_IsPaddedToo()
    {
        var layout = MergePlanner.Plan([File("s0", 2)], 1, A4, startFilesOnOddPages: true, new SequentialKeys(), page => MergePlanner.Normalize(page, A4));

        layout.FirstPositions.ShouldBe([2]);
        layout.AddedBlankPages.ShouldBe(1);
    }

    [Fact]
    public void Plan_PaddingPagesFollowTheNormalizedSize()
    {
        var files = new[] { File("s0", 1, sizes: [new PdfSize(300, 400)]), File("s1", 1) };

        var layout = MergePlanner.Plan(files, 0, A4, startFilesOnOddPages: true, new SequentialKeys(), page => MergePlanner.Normalize(page, A4));

        layout.Plan[1].BlankSize.ShouldBe(A4);
    }

    [Fact]
    public void Plan_ValidatesItsInput()
    {
        Should.Throw<ArgumentException>(() => MergePlanner.Plan([], 0, A4, false, new SequentialKeys()));
        Should.Throw<ArgumentException>(() => new MergeFile("s0", [], [A4]));
        Should.Throw<ArgumentOutOfRangeException>(() => new MergeFile("s0", [1], [A4]));
        Should.Throw<ArgumentException>(() => new MergeFile("blank", [0], [A4]));
    }

    [Theory]
    [InlineData(0, 34, 0)]
    [InlineData(1, 34, 1)]
    [InlineData(34, 34, 1)]
    [InlineData(35, 34, 2)]
    public void TableOfContentsPageCount_RoundsUp(int entries, int perPage, int pages)
        => MergePlanner.TableOfContentsPageCount(entries, perPage).ShouldBe(pages);

    [Fact]
    public void Normalize_KeepsEachPagesOrientation()
    {
        MergePlanner.Normalize(new PdfSize(100, 50), A4).ShouldBe(Landscape);
        MergePlanner.Normalize(new PdfSize(50, 100), Landscape).ShouldBe(A4);
    }

    private static MergeFile File(string sourceId, int pageCount, int[]? pages = null, PdfSize[]? sizes = null)
        => new(sourceId, pages ?? [.. Enumerable.Range(0, pageCount)], sizes ?? [.. Enumerable.Repeat(A4, pageCount)]);
}
