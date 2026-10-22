using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Tests.TestSupport;

namespace PdfPro.Operations.Tests.Organizing;

public sealed class SplitMarkOperationsTests(PdfiumFixture pdfium)
{
    private static readonly PlanEditContext Context = new(new Dictionary<string, IReadOnlyList<Core.Geometry.PdfSize>>(), RandomPageKeys.Instance);

    private readonly PdfSharpPlanMaterializer _materializer = new(new PdfiumSourceRepairer(pdfium.Gate));

    private PagePlan Plan { get; } = PagePlan.Identity("s0", 6, RandomPageKeys.Instance);

    [Fact]
    public void MarkSplit_MarksAndUnmarksWithoutChangingContent()
    {
        var operation = new MarkSplitOperation(_materializer, pdfium.Loader);

        var marked = operation.ApplyToPlan(Plan, new MarkSplitParams("3, 5"), Context);
        var unmarked = operation.ApplyToPlan(marked, new MarkSplitParams("3", Marked: false), Context);

        operation.Id.ShouldBe("organize.markSplit");
        operation.Kind.ShouldBe(OperationKind.PlanOnly);
        operation.ChangesContent.ShouldBeFalse();
        ((IPlanOperation)operation).ChangesContent.ShouldBeFalse();
        marked.GetSplitStarts().ShouldBe([0, 2, 4]);
        unmarked.GetSplitStarts().ShouldBe([0, 4]);
        Should.Throw<OperationValidationException>(() => operation.ApplyToPlan(Plan, new MarkSplitParams("7"), Context));
    }

    [Fact]
    public void SetSplitMarks_ReplacesEveryMarkWithNames()
    {
        var operation = new SetSplitMarksOperation(_materializer, pdfium.Loader);
        var before = Plan.MarkSplit([1, 2], marked: true);

        var after = operation.ApplyToPlan(before, new SetSplitMarksParams([new(1, "Tờ trình"), new(4), new(6, "Hợp đồng")]), Context);
        var cleared = operation.ApplyToPlan(after, new SetSplitMarksParams([]), Context);

        operation.ChangesContent.ShouldBeFalse();
        after.GetSplitStarts().ShouldBe([0, 3, 5]);
        after[0].SplitMark!.FileName.ShouldBe("Tờ trình");
        after[5].SplitMark!.FileName.ShouldBe("Hợp đồng");
        after[3].SplitMark!.FileName.ShouldBeNull();
        cleared.HasSplitMarks.ShouldBeFalse();
        after.HasSameContent(before).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(7, null)]
    [InlineData(2, " ")]
    public void SetSplitMarks_RefusesBadEntries(int page, string? name)
    {
        var operation = new SetSplitMarksOperation(_materializer, pdfium.Loader);

        Should.Throw<OperationValidationException>(() => operation.ApplyToPlan(Plan, new SetSplitMarksParams([new(page, name)]), Context));
        Should.Throw<OperationValidationException>(() => operation.ApplyToPlan(Plan, new SetSplitMarksParams([new(2), new(2)]), Context));
    }

    [Fact]
    public void PageOperations_ChangeContent()
        => new RotatePagesOperation(_materializer, pdfium.Loader).ChangesContent.ShouldBeTrue();
}
