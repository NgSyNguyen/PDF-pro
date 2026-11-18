using PdfPro.Core.Geometry;
using PdfPro.Operations.Comparing;
using PdfPro.Operations.Tests.TestSupport;

namespace PdfPro.Operations.Tests.Comparing;

/// <summary>The comparison window's view of a comparison: which pair of pages shows a change, and what each pair highlights.</summary>
public sealed class ComparisonPagesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachChangeOfTheContracts_IsShownOnThePairOfItsPages_WithItsWordsHighlighted()
    {
        var comparison = await TextComparer.CompareAsync(Samples.ContractV1, null, Samples.ContractV2, null, new TextCompareOptions(), Token);

        comparison.Changes.Select(c => ComparisonPages.PairOf(comparison, c)).ShouldBe([0, 2, 3, 4]);

        var inserted = ComparisonPages.HighlightsOf(comparison, null, 2);
        inserted.Old.ShouldBeEmpty("the inserted page has no old page");
        inserted.New.ShouldNotBeEmpty();
        inserted.New.ShouldAllBe(h => h.Kind == ChangeKind.Added && h.Change == 1);

        var payment = ComparisonPages.HighlightsOf(comparison, null, 3);
        payment.Old.ShouldHaveSingleItem().Change.ShouldBe(2);
        payment.New.ShouldHaveSingleItem().Kind.ShouldBe(ChangeKind.Modified);

        ComparisonPages.HighlightsOf(comparison, null, 1).New.ShouldBeEmpty("the second page did not change");
    }

    [Fact]
    public void RegionsThatLookDifferent_AreHighlightedToo_FoundByPosition_OrByTheirPages()
    {
        var pages = new List<PagePair> { new(0, 0), new(null, 1), new(1, 2) };
        var comparison = new TextComparison([], pages, 2, 3);
        var region = new PdfRect(10, 10, 50, 30);
        var visual = new List<VisualDifference>
        {
            new(pages[0], [], [], 0),
            new(pages[1], [], [new PdfRect(0, 0, 595, 842)], 1),
            new(pages[2], [region], [region], 0.01),
        };

        var last = ComparisonPages.HighlightsOf(comparison, visual, 2);
        last.Old.ShouldBe([new PageHighlight(region, null, null)]);
        last.New.ShouldBe([new PageHighlight(region, null, null)]);
        ComparisonPages.VisualOf(comparison, [visual[2]], 2).ShouldBe(visual[2], "found by its pages when the list is shorter");
        ComparisonPages.VisualOf(comparison, visual, 0)!.IsSame.ShouldBeTrue();
        ComparisonPages.VisualOf(comparison, null, 0).ShouldBeNull();
    }

    [Fact]
    public void ADeletedText_IsShownOnItsOldPage_AnAddedOne_OnItsNewPage()
    {
        var pages = new List<PagePair> { new(0, null), new(1, 0) };
        var comparison = new TextComparison([], pages, 2, 1);
        var box = new PdfRect(0, 0, 10, 10);
        var deleted = new DocumentChange(ChangeKind.Deleted, "a", string.Empty, 0, 0, [new ChangeMark(0, box, ChangeKind.Deleted)], []);
        var added = new DocumentChange(ChangeKind.Added, string.Empty, "b", 0, 0, [], [new ChangeMark(0, box, ChangeKind.Added)]);

        ComparisonPages.PairOf(comparison, deleted).ShouldBe(0);
        ComparisonPages.PairOf(comparison, added).ShouldBe(1);
    }
}
