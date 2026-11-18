using PdfPro.Operations.Comparing;
using PdfPro.Operations.Tests.TestSupport;

namespace PdfPro.Operations.Tests.Comparing;

/// <summary>
/// The text of two documents compared: contract-v2 (one page inserted, three places changed) against contract-v1 gives
/// exactly four changes, nothing on the pages that only moved (their footers say "Trang 4/5" instead of "Trang 3/4");
/// a document against itself gives none.
/// </summary>
public sealed class TextComparerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheTwoContracts_DifferByExactlyFourChanges()
    {
        var comparison = await TextComparer.CompareAsync(Samples.ContractV1, null, Samples.ContractV2, null, new TextCompareOptions(), Token);

        comparison.Changes.Count.ShouldBe(4, string.Join(" | ", comparison.Changes.Select(c => $"{c.Kind}: {c.OldText} → {c.NewText}")));
        var (price, appendix, payment, place) = (comparison.Changes[0], comparison.Changes[1], comparison.Changes[2], comparison.Changes[3]);

        price.Kind.ShouldBe(ChangeKind.Modified);
        price.OldText.ShouldContain("100.000.000");
        price.NewText.ShouldContain("120.000.000");
        price.NewText.ShouldContain("hai mươi");
        (price.OldPage, price.NewPage).ShouldBe((0, 0));

        appendix.Kind.ShouldBe(ChangeKind.Added);
        appendix.NewText.ShouldStartWith("PHỤ LỤC 1");
        appendix.OldMarks.ShouldBeEmpty();
        appendix.NewMarks.ShouldAllBe(m => m.Page == 2 && m.Kind == ChangeKind.Added);
        appendix.OldPage.ShouldBe(1, "it comes after the second page of the old version");

        payment.Kind.ShouldBe(ChangeKind.Modified);
        (payment.OldText, payment.NewText).ShouldBe(("30", "45"));
        (payment.OldPage, payment.NewPage).ShouldBe((2, 3));
        payment.OldMarks.ShouldHaveSingleItem().Kind.ShouldBe(ChangeKind.Modified);

        place.Kind.ShouldBe(ChangeKind.Modified);
        (place.OldText, place.NewText).ShouldBe(("Hà Nội,", "Đà Nẵng,"));
        (place.OldPage, place.NewPage).ShouldBe((3, 4));

        comparison.Pages.ShouldBe([new PagePair(0, 0), new PagePair(1, 1), new PagePair(null, 2), new PagePair(2, 3), new PagePair(3, 4)]);
    }

    [Fact]
    public async Task ADocument_IsTheSameAsItself()
    {
        var comparison = await TextComparer.CompareAsync(Samples.VanBanVi, null, Samples.VanBanVi, null, new TextCompareOptions(), Token);

        comparison.AreSame.ShouldBeTrue();
        comparison.Pages.ShouldAllBe(p => p.OldPage == p.NewPage);
        comparison.Pages.Count.ShouldBe(14);
    }

    [Fact]
    public async Task WithFootersCompared_EveryMovedPageChanges()
    {
        var comparison = await TextComparer.CompareAsync(Samples.ContractV1, null, Samples.ContractV2, null, new TextCompareOptions(IgnoreRunningLines: false), Token);

        comparison.Changes.Count.ShouldBeGreaterThan(4, "\"Trang 1/4\" became \"Trang 1/5\" and so on");
    }

    [Fact]
    public async Task ScansWithoutText_ArePairedInOrder()
    {
        var comparison = await TextComparer.CompareAsync(Samples.ScanVi, null, Samples.ScanVi, null, new TextCompareOptions(), Token);

        comparison.AreSame.ShouldBeTrue();
        comparison.Pages.Select(p => (p.OldPage, p.NewPage)).ShouldBe(Enumerable.Range(0, 18).Select(i => ((int?)i, (int?)i)));
    }

    [Fact]
    public async Task AProtectedDocument_NeedsItsPassword()
    {
        await Should.ThrowAsync<PdfPro.Operations.Security.DocumentPasswordException>(() => TextComparer.CompareAsync(Samples.Encrypted, null, Samples.BasicVi, null, new TextCompareOptions(), Token));
        var comparison = await TextComparer.CompareAsync(Samples.Encrypted, "test123", Samples.Encrypted, "test123", new TextCompareOptions(), Token);
        comparison.AreSame.ShouldBeTrue();
    }
}
