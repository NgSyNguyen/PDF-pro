using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Tests.TestSupport;

namespace PdfPro.Operations.Tests.Organizing;

public sealed class InterleaveOperationTests(PdfiumFixture pdfium)
{
    private readonly InterleaveOperation _operation = new(new PdfSharpPlanMaterializer(new PdfiumSourceRepairer(pdfium.Gate)), pdfium.Loader);

    [Fact]
    public void SameDocument_FrontsThenReversedBacks()
    {
        // A stack of 3 sheets scanned on one side (pages 1-3), turned over and scanned again (pages 4-6: backs 3, 2, 1).
        var plan = PagePlan.Identity("s0", 6, RandomPageKeys.Instance);

        var result = _operation.ApplyToPlan(plan, new InterleaveParams(BackStart: 4, ReverseBacks: true), Context(plan));

        result.Select(p => p.SourcePageIndex).ShouldBe([0, 5, 1, 4, 2, 3]);
        result.Select(p => p.PageKey).Order().ShouldBe(plan.Select(p => p.PageKey).Order(), "the same pages, only reordered");
        _operation.Id.ShouldBe("organize.interleave");
        _operation.ChangesContent.ShouldBeTrue();
    }

    [Fact]
    public void BacksInAnotherFile_AreAddedBetweenTheFronts()
    {
        var plan = PagePlan.Identity("s0", 3, RandomPageKeys.Instance);
        var context = Context(plan, ("s1", 3));

        var result = _operation.ApplyToPlan(plan, new InterleaveParams(BackSourceId: "s1"), context);

        result.Select(p => (p.SourceId, p.SourcePageIndex)).ShouldBe([("s0", 0), ("s1", 0), ("s0", 1), ("s1", 1), ("s0", 2), ("s1", 2)]);
    }

    [Fact]
    public void MissingBacks_GoToTheEnd()
    {
        var plan = PagePlan.Identity("s0", 3, RandomPageKeys.Instance);

        var result = _operation.ApplyToPlan(plan, new InterleaveParams(BackSourceId: "s1", BackPages: "1"), Context(plan, ("s1", 4)));

        result.Select(p => (p.SourceId, p.SourcePageIndex)).ShouldBe([("s0", 0), ("s1", 0), ("s0", 1), ("s0", 2)]);
    }

    [Fact]
    public void AnOrderThatDoesNotChange_ReturnsTheSamePlan()
    {
        var plan = PagePlan.Identity("s0", 2, RandomPageKeys.Instance);

        _operation.ApplyToPlan(plan, new InterleaveParams(BackStart: 2), Context(plan)).ShouldBeSameAs(plan);
        InterleaveOperation.DefaultBackStart(7).ShouldBe(5);
        InterleaveOperation.DefaultBackStart(6).ShouldBe(4);
    }

    [Theory]
    [InlineData(1, null, null)]
    [InlineData(7, null, null)]
    [InlineData(0, "s9", null)]
    [InlineData(0, "s1", "5")]
    public void BadParameters_AreRefused(int backStart, string? source, string? pages)
    {
        var plan = PagePlan.Identity("s0", 6, RandomPageKeys.Instance);

        Should.Throw<OperationValidationException>(() => _operation.ApplyToPlan(plan, new InterleaveParams(backStart, source, pages), Context(plan, ("s1", 3))));
    }

    private static PlanEditContext Context(PagePlan plan, params (string Id, int Pages)[] others)
    {
        var sizes = new Dictionary<string, IReadOnlyList<PdfSize>> { ["s0"] = [.. Enumerable.Repeat(new PdfSize(595, 842), plan.Count)] };
        foreach (var (id, pages) in others)
        {
            sizes[id] = [.. Enumerable.Repeat(new PdfSize(595, 842), pages)];
        }

        return new PlanEditContext(sizes, RandomPageKeys.Instance);
    }
}
